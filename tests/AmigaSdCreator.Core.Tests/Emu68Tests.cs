using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;
using Xunit.Abstractions;

namespace AmigaSdCreator.Core.Tests;

public class Emu68Tests(ITestOutputHelper output)
{
    [Fact]
    public void PiStormCard_HasFatBootAnd0x76RdbPartition_AndVerifies()
    {
        var dev = new MemoryBlockDevice(1024 * TestSupport.MiB);
        var (p, i) = TestSupport.Iface("pistorm-sd");
        var plan = LayoutPlanner.Recommend(p, i, IntendedUse.BootableSystem, dev.Length);
        Assert.Equal(LayoutKind.Emu68, plan.Kind);
        Assert.False(LayoutValidator.HasErrors(LayoutValidator.Validate(plan, i)));

        DiskImageBuilder.Apply(dev, plan, i);
        var result = LayoutVerifier.Verify(dev, plan);
        Assert.True(result.Passed, string.Join("\n", result.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));

        var mbr = MbrPartitionTable.TryParse(dev.ReadSector(0), dev.SectorCount())!;
        Assert.Equal([MbrPartitionTypes.Fat32Lba, MbrPartitionTypes.AmigaRdb], mbr.UsedEntries.Select(e => e.Type));
        Assert.Contains(result.Inspection.Partitions, x => x.Table == "RDB in 0x76" && x.Bootable && x.Filesystem.StructureValidated);

        var report = DiagnosticEngine.Analyse(result.Inspection, p, i);
        Assert.Contains(report.Findings, f => f.Code == "emu68.ok");
    }

    private static string MakeZip(params (string Name, string Text)[] files)
    {
        var path = TestSupport.TempFile(".zip");
        using var z = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (n, t) in files)
        {
            using var w = new StreamWriter(z.CreateEntry(n).Open());
            w.Write(t);
        }
        return path;
    }

    [Fact]
    public void Installer_WithoutRom_DisablesInitramfs_WithRom_CopiesKickRom()
    {
        const string config = "kernel=Emu68-pistorm\r\n# comment\r\ninitramfs kick.rom\r\n";
        var zip = MakeZip(("config.txt", config), ("Emu68-pistorm", "kernel"), ("start4.elf", "fw"));

        var noRom = Directory.CreateTempSubdirectory().FullName;
        var files = Imaging.Emu68Installer.Install(zip, noRom, null);
        Assert.Equal(3, files.Count);
        var text = File.ReadAllText(Path.Combine(noRom, "config.txt"));
        Assert.Contains("# initramfs kick.rom", text);
        Assert.DoesNotContain("\ninitramfs kick.rom", text);
        Assert.False(File.Exists(Path.Combine(noRom, "kick.rom")));

        var withRom = Directory.CreateTempSubdirectory().FullName;
        var rom = TestSupport.TempFile(".rom");
        File.WriteAllBytes(rom, [1, 2, 3]);
        Imaging.Emu68Installer.Install(zip, withRom, rom);
        Assert.Contains("\ninitramfs kick.rom", File.ReadAllText(Path.Combine(withRom, "config.txt")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(withRom, "kick.rom")));
    }

    [Fact]
    public void Installer_RejectsPathTraversal()
    {
        var zip = MakeZip(("../evil.txt", "x"));
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.Throws<InvalidDataException>(() => Imaging.Emu68Installer.Install(zip, dir, null));
    }

    [Fact]
    public void Installer_HandlesRealRelease_WhenAvailable()
    {
        var real = Environment.GetEnvironmentVariable("ASDC_EMU68_ZIP");
        if (real is null || !File.Exists(real)) { output.WriteLine("SKIPPED: set ASDC_EMU68_ZIP to an Emu68 release zip."); return; }
        var dir = Directory.CreateTempSubdirectory().FullName;
        var files = Imaging.Emu68Installer.Install(real, dir, null);
        output.WriteLine(string.Join(", ", files));
        Assert.Contains("config.txt", files);
        Assert.Contains(files, f => f.StartsWith("Emu68-", StringComparison.Ordinal));
        Assert.Matches(@"(?m)^# initramfs kick\.rom", File.ReadAllText(Path.Combine(dir, "config.txt")));
    }

    [Fact]
    public void RdbtoolAcceptsTheRdbInside0x76()
    {
        if (TestSupport.AmitoolsBin is not { } bin) { output.WriteLine("SKIPPED: amitools not installed."); return; }
        var (p, i) = TestSupport.Iface("pistorm-sd");
        var plan = LayoutPlanner.Recommend(p, i, IntendedUse.HardDrive, 600 * TestSupport.MiB);
        var dev = new MemoryBlockDevice(600 * TestSupport.MiB);
        DiskImageBuilder.Apply(dev, plan, i);
        var region = TestSupport.TempFile(".hdf");
        File.WriteAllBytes(region, dev.Data.AsSpan((int)(plan.AmigaStartLba * 512L), (int)plan.AmigaRegionBytes).ToArray());
        var (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "rdbtool"), region, "info");
        output.WriteLine(text);
        Assert.Equal(0, exit);
        Assert.Contains("'DH0'", text);
    }
}
