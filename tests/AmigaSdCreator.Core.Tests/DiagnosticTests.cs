using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Tests;

/// <summary>Fixture cards modelled on real-world situations, analysed against the A6000 profiles.</summary>
public class DiagnosticTests
{
    private static DiagnosticReport Diagnose(IBlockDevice dev, string iface)
    {
        var (p, i) = TestSupport.Iface(iface);
        return DiagnosticEngine.Analyse(DiskInspector.Inspect(dev), p, i);
    }

    private static MemoryBlockDevice MbrCardWith(Action<IBlockDevice> formatPartition, byte type = MbrPartitionTypes.Fat32Lba, long size = 256 * TestSupport.MiB)
    {
        var dev = new MemoryBlockDevice(size);
        uint count = (uint)(dev.SectorCount() - 2048);
        dev.WriteSector(0, new MbrPartitionTable { Entries = [new MbrPartitionEntry(1, 0, type, 2048, count)] }.ToSector());
        formatPartition(new SubBlockDevice(dev, 2048 * 512L, count * 512L));
        return dev;
    }

    [Fact]
    public void WindowsFat32Card_WithDefaultLabel_IsCompatibleWithLabelHint()
    {
        var dev = MbrCardWith(v => Fat32Formatter.Format(v, new Fat32FormatOptions { Label = "NO NAME" }));
        var r = Diagnose(dev, "a6000-sd-data");
        Assert.Equal(CompatibilityVerdict.Compatible, r.Verdict);
        Assert.Contains(r.Findings, f => f.Code == "data.label");
        Assert.Contains(r.Findings, f => f.Title.Contains("MBR partition table detected"));
        Assert.Contains(r.Findings, f => f.Code == "rdb.absent");
    }

    [Fact]
    public void ExFatCard_IsIncompatible_WithHighConfidenceUninitializedCause()
    {
        var dev = MbrCardWith(v =>
        {
            var s = new byte[512];
            s[0] = 0xEB; s[1] = 0x76; s[2] = 0x90;
            "EXFAT   "u8.CopyTo(s.AsSpan(3));
            s[510] = 0x55; s[511] = 0xAA;
            v.WriteSector(0, s);
        }, MbrPartitionTypes.NtfsExfat);
        var r = Diagnose(dev, "a6000-sd-data");
        Assert.Equal(CompatibilityVerdict.Incompatible, r.Verdict);
        var cause = r.UninitializedCauses.First();
        Assert.Equal("data.exfat", cause.Code);
        Assert.Equal(Confidence.High, cause.Confidence);
        Assert.Contains(r.Recommendations, x => x.Action == RecommendedAction.PrepareDataCard);
        Assert.Contains(r.Recommendations, x => x.Action == RecommendedAction.BackupFirst);
    }

    [Fact]
    public void Fat32CardWithStaleRdb_IsFlagged()
    {
        // A card used as an Amiga hard drive (RDSK moved to block 3, as some tools place it)
        // and later reformatted FAT32 on Windows, which rewrote sector 0 only.
        var dev = new MemoryBlockDevice(256 * TestSupport.MiB);
        var geo = RdbGeometry.ForBlockCount(dev.SectorCount());
        RdbWriter.Write(dev, new RdbLayoutSpec(geo, 4, [new RdbPartitionSpec("DH0", 4, geo.Cylinders - 1, DosType.FfsIntl)], []));
        var rdsk = dev.ReadSector(0);
        dev.WriteSector(3, rdsk);
        uint count = (uint)(dev.SectorCount() - 8192);
        dev.WriteSector(0, new MbrPartitionTable { Entries = [new MbrPartitionEntry(1, 0, MbrPartitionTypes.Fat32Lba, 8192, count)] }.ToSector());
        Fat32Formatter.Format(new SubBlockDevice(dev, 8192 * 512L, count * 512L), new Fat32FormatOptions { Label = "SD0" });

        var r = Diagnose(dev, "a6000-sd-data");
        Assert.True(r.Inspection.HasConflictingRdbAndMbr);
        Assert.Equal(PartitionScheme.Mbr, r.Inspection.Scheme);
        Assert.Contains(r.UninitializedCauses, f => f.Code == "layout.stale-rdb" && f.Confidence == Confidence.High);
        Assert.Equal(CompatibilityVerdict.Incompatible, r.Verdict);
    }

    [Fact]
    public void GptCard_IsFlaggedForDataSlot()
    {
        var dev = new MemoryBlockDevice(64 * TestSupport.MiB);
        dev.WriteSector(0, new MbrPartitionTable { Entries = [new MbrPartitionEntry(1, 0, MbrPartitionTypes.GptProtective, 1, (uint)(dev.SectorCount() - 1))] }.ToSector());
        var hdr = new byte[512];
        "EFI PART"u8.CopyTo(hdr);
        IO.Endian.PutU32LE(hdr, 72, 2);    // entries LBA
        IO.Endian.PutU32LE(hdr, 80, 128);  // entry count
        IO.Endian.PutU32LE(hdr, 84, 128);  // entry size
        dev.WriteSector(1, hdr);
        var entry = new byte[512];
        GptPartitionEntry.MicrosoftBasicData.ToByteArray().CopyTo(entry, 0);
        Guid.NewGuid().ToByteArray().CopyTo(entry, 16);
        IO.Endian.PutU32LE(entry, 32, 2048);
        IO.Endian.PutU32LE(entry, 40, (uint)(dev.SectorCount() - 34));
        dev.WriteSector(2, entry);

        var r = Diagnose(dev, "a6000-sd-data");
        Assert.Equal(PartitionScheme.Gpt, r.Inspection.Scheme);
        Assert.Contains(r.Findings, f => f.Code == "data.gpt");
        Assert.Equal(CompatibilityVerdict.Incompatible, r.Verdict);
    }

    [Fact]
    public void RdbCard_InDataSlot_IsExplainedNotCalledCorrupt()
    {
        var dev = new MemoryBlockDevice(128 * TestSupport.MiB);
        var (p, i) = TestSupport.Iface("a6000-sd-hdd");
        DiskImageBuilder.Apply(dev, LayoutPlanner.Recommend(p, i, IntendedUse.HardDrive, dev.Length), i);

        var r = Diagnose(dev, "a6000-sd-data");
        Assert.Contains(r.Findings, f => f.Code == "data.is-rdb" && f.Detail.Contains("not damaged"));
        Assert.DoesNotContain(r.Findings, f => f.Title.Contains("corrupt", StringComparison.OrdinalIgnoreCase));

        // The same card against the hard-drive interface is fine.
        var ok = Diagnose(dev, "a6000-sd-hdd");
        Assert.NotEqual(CompatibilityVerdict.Incompatible, ok.Verdict);
        Assert.Contains(ok.Findings, f => f.Code.StartsWith("hdd.ok"));
    }

    [Fact]
    public void Fat32Card_InHardDriveSlot_ExplainsUninitialized()
    {
        var dev = MbrCardWith(v => Fat32Formatter.Format(v, new Fat32FormatOptions { Label = "SD0" }));
        var r = Diagnose(dev, "a6000-sd-hdd");
        Assert.Contains(r.UninitializedCauses, f => f.Code == "hdd.pc-layout");
        Assert.Contains(r.Recommendations, x => x.Action == RecommendedAction.PrepareHardDriveCard);
    }

    [Fact]
    public void UnknownContent_IsLowConfidence_AndNeverCorrupt()
    {
        var dev = new MemoryBlockDevice(8 * TestSupport.MiB);
        var junk = new byte[512];
        new Random(42).NextBytes(junk);
        junk[510] = 0; // no boot signature
        dev.WriteSector(0, junk);
        var r = Diagnose(dev, "a6000-sd-data");
        Assert.Equal(PartitionScheme.Unknown, r.Inspection.Scheme);
        Assert.Contains(r.Findings, f => f.Code == "layout.unknown" && f.Confidence == Confidence.Low);
        Assert.DoesNotContain(r.Findings, f => (f.Title + f.Detail).Contains("corrupted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuperfloppyFat32_IsDetectedAsUnpartitioned()
    {
        var dev = new MemoryBlockDevice(128 * TestSupport.MiB);
        Fat32Formatter.Format(dev, new Fat32FormatOptions { Label = "SD0" });
        var r = Diagnose(dev, "a6000-sd-data");
        Assert.Equal(PartitionScheme.Unpartitioned, r.Inspection.Scheme);
        Assert.Contains(r.Findings, f => f.Code == "data.superfloppy" && f.Confidence == Confidence.Low);
    }

    [Fact]
    public void Reports_ExportMarkdownAndJson_WithoutSerials()
    {
        var dev = MbrCardWith(v => Fat32Formatter.Format(v, new Fat32FormatOptions { Label = "SD0" }));
        var r = Diagnose(dev, "a6000-sd-data");
        var md = ReportExporter.ToMarkdown(r);
        Assert.Contains("## Result: Compatible", md);
        Assert.Contains("No data on the card was changed", md);
        var json = ReportExporter.ToJson(r);
        Assert.Contains("\"verdict\": \"Compatible\"", json);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoProfile_GivesStructureOnly()
    {
        var dev = new MemoryBlockDevice(8 * TestSupport.MiB);
        var r = DiagnosticEngine.Analyse(DiskInspector.Inspect(dev));
        Assert.Equal(CompatibilityVerdict.NotAssessed, r.Verdict);
        Assert.Contains(r.Findings, f => f.Code == "layout.blank");
    }
}
