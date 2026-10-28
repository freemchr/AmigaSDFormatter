using Xunit.Abstractions;
using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Tests;

/// <summary>
/// Cross-checks against amitools (rdbtool/xdftool), an independent implementation of the
/// Amiga RDB and OFS/FFS formats. Install with:
///   uv venv ~/.cache/asdc-tools/venv &amp;&amp; uv pip install --python ~/.cache/asdc-tools/venv/bin/python amitools==0.8.1
/// or set ASDC_AMITOOLS_BIN. Tests pass trivially (and say so) when the tools are absent.
/// </summary>
public class AmitoolsInteropTests(ITestOutputHelper output)
{
    private string? Bin()
    {
        var bin = TestSupport.AmitoolsBin;
        if (bin is null) output.WriteLine("SKIPPED: amitools not installed (set ASDC_AMITOOLS_BIN).");
        return bin;
    }

    [Fact]
    public void RdbtoolAcceptsOurRdb_WithMatchingPartitions()
    {
        if (Bin() is not { } bin) return;
        var path = TestSupport.TempFile();
        var (p, i) = TestSupport.Iface("a1200-ide");
        var plan = LayoutPlanner.Recommend(p, i, IntendedUse.BootableSystem, 300 * TestSupport.MiB, compatibilityModeId: "ks31");
        plan = plan with { FileSystems = [new FileSystemDriverPlan(DosType.Pfs3, "testfs", TestSupport.FakeHandler(1000), 19 << 16 | 2, "19.2")] };
        using (var dev = FileBlockDevice.Create(path, 300 * TestSupport.MiB)) DiskImageBuilder.Apply(dev, plan, i);

        var (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "rdbtool"), path, "info");
        output.WriteLine(text);
        Assert.Equal(0, exit);
        foreach (var part in plan.Partitions)
            Assert.Contains($"'{part.DeviceName}'", text);
        Assert.Contains("boot(0)", text);
        Assert.Contains("DOS3", text);

        Assert.Contains("PFS3/0x50465303 version=19.2", text);

        (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "rdbtool"), path, "show");
        Assert.Equal(0, exit);
        Assert.DoesNotContain("valid:     False", text);
    }

    [Fact]
    public void OurRdbReader_ReadsRdbtoolCreatedDisk()
    {
        if (Bin() is not { } bin) return;
        var path = TestSupport.TempFile(".hdf");
        var (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "rdbtool"), path, "create", "size=64Mi", "+", "init", "+",
            "add", "size=40%", "name=DH0", "bootable", "pri=3", "+", "add", "size=60%", "name=DH1", "dostype=PFS3");
        output.WriteLine(text);
        Assert.Equal(0, exit);

        using var dev = FileBlockDevice.OpenRead(path);
        var rdb = RdbReader.TryRead(dev);
        Assert.NotNull(rdb);
        Assert.Empty(rdb.Problems);
        Assert.Equal(["DH0", "DH1"], rdb.Partitions.Select(x => x.DriveName));
        Assert.True(rdb.Partitions[0].IsBootable);
        Assert.Equal(3, rdb.Partitions[0].BootPri);
        Assert.Equal(DosType.Pfs3, rdb.Partitions[1].DosType);

        var report = DiagnosticEngine.Analyse(DiskInspector.Inspect(dev), TestSupport.Iface("a1200-ide").Profile, TestSupport.Iface("a1200-ide").Interface);
        Assert.Contains(report.Findings, f => f.Code.StartsWith("hdd.unformatted"));
    }

    [Theory]
    [InlineData(2016)]    // 1 bitmap block
    [InlineData(262080)]  // bitmap extension blocks
    public void XdftoolReadsOurFfs_AndCanWriteToIt(int blocks)
    {
        if (Bin() is not { } bin) return;
        var path = TestSupport.TempFile(".hdf");
        using (var dev = FileBlockDevice.Create(path, blocks * 512L))
            AmigaFfsFormatter.Format(dev, DosType.FfsIntl, "Work");
        string chs = $"chs={blocks / 1008},16,63";
        string xdf = TestSupport.Tool(bin, "xdftool");

        var (exit, text) = TestSupport.Run(xdf, path, "open", chs, "+", "info");
        output.WriteLine(text);
        Assert.Equal(0, exit);
        long bitmaps = (blocks - 2 + 4063) / 4064;
        long ext = bitmaps > 25 ? (bitmaps - 25 + 126) / 127 : 0;
        Assert.Matches($@"used:\s+{2 + 1 + bitmaps + ext}\s", text); // amitools counts the 2 boot blocks as used

        // AmigaDOS names are limited to 30 characters (amitools enforces this too).
        var src = Path.Combine(Path.GetDirectoryName(path)!, $"r{Random.Shared.Next(100000)}.txt");
        File.WriteAllText(src, string.Concat(Enumerable.Repeat("Amiga SD Creator ", 200)));
        (exit, text) = TestSupport.Run(xdf, path, "open", chs, "+", "write", src, "+", "makedir", "Dir", "+", "list");
        output.WriteLine(text);
        Assert.Equal(0, exit);
        Assert.Contains("Work", text);
        Assert.Contains("DOS3:ffs+intl", text);

        using var check = FileBlockDevice.OpenRead(path);
        var info = AmigaFfsInspector.Inspect(check);
        Assert.True(info.StructureValidated, string.Join(" ", info.Notes));
        Assert.Contains("Dir", AmigaFfsInspector.ListRoot(check));
    }

    [Fact]
    public void OurInspector_ReadsXdftoolFormattedVolume()
    {
        if (Bin() is not { } bin) return;
        var path = TestSupport.TempFile(".hdf");
        var (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "xdftool"), path, "create", "size=20Mi", "+", "format", "Fresh", "ffs+intl");
        output.WriteLine(text);
        Assert.Equal(0, exit);
        using var dev = FileBlockDevice.OpenRead(path);
        var info = FilesystemProbe.Probe(dev);
        Assert.Equal(FilesystemKind.AmigaDos, info.Kind);
        Assert.Equal("Fresh", info.Label);
        Assert.Equal(DosType.FfsIntl, info.DosType);
        Assert.True(info.StructureValidated, string.Join(" ", info.Notes));
    }
}
