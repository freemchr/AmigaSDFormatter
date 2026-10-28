using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Tests;

internal static class TestSupport
{
    public const long MiB = 1024 * 1024;

    public static readonly ProfileCatalog Catalog = ProfileCatalog.LoadBuiltIn();

    public static (HardwareProfile Profile, StorageInterfaceProfile Interface) Iface(string id) =>
        Catalog.FindInterface(id) ?? throw new InvalidOperationException($"Interface {id} missing from built-in profiles.");

    public static string TempFile(string extension = ".img")
    {
        var dir = Path.Combine(Path.GetTempPath(), "asdc-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}{extension}");
    }

    /// <summary>A minimal valid AmigaDOS hunk executable with a $VER string, standing in for a filesystem handler.</summary>
    public static byte[] FakeHandler(int codeLongs = 300)
    {
        var ver = System.Text.Encoding.ASCII.GetBytes("$VER: testfs 19.2 (01.01.2026)\0\0");
        int verLongs = (ver.Length + 3) / 4;
        int total = codeLongs + verLongs;
        var data = new List<byte>();
        void L(uint v) { data.Add((byte)(v >> 24)); data.Add((byte)(v >> 16)); data.Add((byte)(v >> 8)); data.Add((byte)v); }
        L(0x3F3); L(0); L(1); L(0); L(0); L((uint)total);  // HUNK_HEADER, 1 hunk
        L(0x3E9); L((uint)total);                          // HUNK_CODE
        for (int i = 0; i < codeLongs; i++) L(0x4E714E71); // NOPs
        data.AddRange(ver);
        while (data.Count % 4 != 0) data.Add(0);
        L(0x3F2);                                          // HUNK_END
        return data.ToArray();
    }

    /// <summary>Path to the amitools virtualenv bin directory, or null when not installed (tests are then skipped).</summary>
    public static string? AmitoolsBin
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ASDC_AMITOOLS_BIN");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[] { env, Path.Combine(home, ".cache", "asdc-tools", "venv", "bin"), Path.Combine(home, ".cache", "asdc-tools", "venv", "Scripts") };
            return candidates.FirstOrDefault(c => c is not null && File.Exists(Tool(c, "rdbtool")));
        }
    }

    /// <summary>Path of an amitools entry point (".exe" on Windows).</summary>
    public static string Tool(string bin, string name) => Path.Combine(bin, OperatingSystem.IsWindows() ? name + ".exe" : name);

    public static (int Exit, string Output) Run(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    public static LayoutPlan Plan(string interfaceId, IntendedUse use, long bytes, string? mode = null)
    {
        var (p, i) = Iface(interfaceId);
        return LayoutPlanner.Recommend(p, i, use, bytes, compatibilityModeId: mode);
    }
}
