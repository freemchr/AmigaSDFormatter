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
