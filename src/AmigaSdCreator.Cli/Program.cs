using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

// Amiga SD Creator command-line tool. Works on disk IMAGE FILES only; it never opens physical disks.
return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        Amiga SD Creator (image files only)

          asdc profiles
          asdc inspect <image> [--interface <id>] [--use <DataExchange|HardDrive|BootableSystem>] [--report <file.md|file.json>]
          asdc create <image> --size <bytes|8G|512M> --interface <id> [--use <use>] [--mode <compat-mode>]
                      [--label <FAT label>] [--dostype <DOS\3|PFS\3|...>] [--unformatted]
                      [--fs <handler file> --fs-dostype <PFS\3>]

        Examples:
          asdc create data.img --size 8G --interface a6000-sd-data
          asdc create hdd.img --size 4G --interface a1200-ide --use BootableSystem --mode ks31
          asdc inspect card.img --interface a6000-sd-data --report report.md
        """;

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
            var opts = ParseOptions(args.Skip(1).ToArray(), out var positional);
            var catalog = ProfileCatalog.LoadBuiltIn();
            return args[0] switch
            {
                "profiles" => ListProfiles(catalog),
                "inspect" when positional.Count == 1 => Inspect(catalog, positional[0], opts),
                "create" when positional.Count == 1 => Create(catalog, positional[0], opts),
                _ => Fail(Usage),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or FormatException or NotSupportedException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int ListProfiles(ProfileCatalog catalog)
    {
        foreach (var p in catalog.Profiles)
        {
            Console.WriteLine($"{p.Name} [{p.Status}]");
            foreach (var i in p.Interfaces)
                Console.WriteLine($"  {i.Id,-18} {i.Name} [{i.Status}] uses: {string.Join(", ", i.Uses)}" +
                    (i.CompatibilityModes.Count > 0 ? $" modes: {string.Join(", ", i.CompatibilityModes.Select(m => m.Id))}" : ""));
        }
        return 0;
    }

    private static int Inspect(ProfileCatalog catalog, string path, Dictionary<string, string?> o)
    {
        using var device = FileBlockDevice.OpenRead(path);
        var inspection = DiskInspector.Inspect(device);
        HardwareProfile? profile = null;
        StorageInterfaceProfile? iface = null;
        if (o.TryGetValue("interface", out var id) && id is not null)
        {
            var found = catalog.FindInterface(id) ?? throw new ArgumentException($"Unknown interface '{id}'. Run 'asdc profiles'.");
            (profile, iface) = found;
        }
        IntendedUse? use = o.TryGetValue("use", out var u) && u is not null ? Enum.Parse<IntendedUse>(u, true) : null;
        var report = DiagnosticEngine.Analyse(inspection, profile, iface, use, Path.GetFileName(path));
        var md = ReportExporter.ToMarkdown(report);
        Console.WriteLine(md);
        if (o.TryGetValue("report", out var reportPath) && reportPath is not null)
        {
            File.WriteAllText(reportPath, reportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ReportExporter.ToJson(report) : md);
            Console.WriteLine($"Report written to {reportPath}");
        }
        return report.Verdict == CompatibilityVerdict.Incompatible ? 3 : 0;
    }

    private static int Create(ProfileCatalog catalog, string path, Dictionary<string, string?> o)
    {
        if (File.Exists(path)) throw new IOException($"'{path}' already exists; refusing to overwrite.");
        long size = ParseSize(Require(o, "size"));
        var (profile, iface) = catalog.FindInterface(Require(o, "interface")) ?? throw new ArgumentException("Unknown interface. Run 'asdc profiles'.");
        var use = o.TryGetValue("use", out var u) && u is not null ? Enum.Parse<IntendedUse>(u, true) : iface.Uses.First();

        var plan = LayoutPlanner.Recommend(profile, iface, use, size, compatibilityModeId: o.GetValueOrDefault("mode"));
        if (o.TryGetValue("label", out var label) && label is not null) plan = plan with { FatLabel = label };
        if (o.TryGetValue("dostype", out var dt) && dt is not null)
        {
            var dosType = DosType.Parse(dt);
            plan = plan with { Partitions = plan.Partitions.Select(p => p with { DosType = dosType }).ToList() };
        }
        if (o.ContainsKey("unformatted"))
            plan = plan with { Partitions = plan.Partitions.Select(p => p with { FormatNow = false }).ToList() };
        if (o.TryGetValue("fs", out var fsPath) && fsPath is not null)
        {
            var bin = File.ReadAllBytes(fsPath);
            var ver = HunkFile.ReadVersion(bin);
            var fsType = DosType.Parse(Require(o, "fs-dostype"));
            plan = plan with { FileSystems = [new FileSystemDriverPlan(fsType, Path.GetFileName(fsPath), bin, (ver?.Major ?? 0) << 16 | (ver?.Minor ?? 0), ver?.Text ?? "unknown")] };
        }

        var messages = LayoutValidator.Validate(plan, iface);
        foreach (var m in messages) Console.WriteLine($"[{m.Severity}] {m.Text}");
        if (LayoutValidator.HasErrors(messages)) return 4;

        PrintPlan(plan);
        using (var device = FileBlockDevice.Create(path, size))
        {
            DiskImageBuilder.Apply(device, plan, iface, new Progress<string>(s => Console.WriteLine($"  {s}")));
            var result = LayoutVerifier.Verify(device, plan);
            Console.WriteLine("Verification:");
            foreach (var c in result.Checks) Console.WriteLine($"  [{(c.Passed ? "PASS" : "FAIL")}] {c.Name}: {c.Detail}");
            if (!result.Passed) return 5;
        }
        Console.WriteLine($"Image created: {path}");
        return 0;
    }

    private static void PrintPlan(LayoutPlan plan)
    {
        Console.WriteLine($"Layout: {plan.Kind}, {Format.Bytes(plan.DeviceBytes)}");
        if (plan.Kind == LayoutKind.MbrFat32)
            Console.WriteLine($"  FAT32 '{plan.FatLabel}' at LBA {plan.FatStartLba}, {Format.Bytes((long)plan.FatSectorCount * plan.SectorSize)}, clusters {Format.Bytes(plan.FatClusterBytes ?? 0)}");
        foreach (var p in plan.Partitions)
            Console.WriteLine($"  {p.DeviceName}: '{p.VolumeName}' cyl {p.LowCyl}-{p.HighCyl} {Format.Bytes(plan.PartitionBytes(p))} {p.DosType.FriendlyName}{(p.Bootable ? " bootable" : "")}{(p.WillBeFormatted ? "" : " (unformatted)")}");
    }

    private static Dictionary<string, string?> ParseOptions(string[] args, out List<string> positional)
    {
        var o = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        positional = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                string key = args[i][2..];
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                o[key] = hasValue && key != "unformatted" ? args[++i] : null;
            }
            else positional.Add(args[i]);
        }
        return o;
    }

    private static string Require(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out var v) && v is not null ? v : throw new ArgumentException($"--{key} is required.");

    internal static long ParseSize(string s)
    {
        s = s.Trim().ToUpperInvariant();
        long mult = s.EndsWith('G') ? 1L << 30 : s.EndsWith('M') ? 1L << 20 : s.EndsWith('K') ? 1L << 10 : 1;
        var number = mult == 1 ? s : s[..^1];
        long v = checked(long.Parse(number) * mult);
        if (v % 512 != 0) throw new ArgumentException("Size must be a multiple of 512 bytes.");
        return v;
    }

    private static int Fail(string text) { Console.Error.WriteLine(text); return 1; }
}
