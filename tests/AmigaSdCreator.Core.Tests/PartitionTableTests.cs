using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.Tests;

public class PartitionTableTests
{
    [Fact]
    public void Mbr_RoundTrips()
    {
        var mbr = new MbrPartitionTable
        {
            DiskSignature = 0x12345678,
            Entries = [new MbrPartitionEntry(1, 0x80, MbrPartitionTypes.Fat32Lba, 2048, 1_000_000)],
        };
        var parsed = MbrPartitionTable.TryParse(mbr.ToSector(), 2_000_000);
        Assert.NotNull(parsed);
        Assert.Equal(0x12345678u, parsed.DiskSignature);
        var e = Assert.Single(parsed.UsedEntries);
        Assert.Equal((uint)2048, e.FirstLba);
        Assert.Equal((uint)1_000_000, e.SectorCount);
        Assert.True(e.IsActive);
    }

    [Fact]
    public void Mbr_RejectsPartitionBeyondDevice()
    {
        var mbr = new MbrPartitionTable { Entries = [new MbrPartitionEntry(1, 0, MbrPartitionTypes.Fat32Lba, 2048, 5_000_000)] };
        Assert.Null(MbrPartitionTable.TryParse(mbr.ToSector(), 1_000_000));
    }

    [Fact]
    public void Mbr_RejectsFatSuperfloppyBootSector()
    {
        // A FAT boot sector has 0x55AA but its "partition entries" are boot code and invalid status bytes.
        var s = new byte[512];
        s[0] = 0xEB; s[1] = 0x58; s[2] = 0x90;
        for (int i = 446; i < 510; i++) s[i] = 0x33;
        s[510] = 0x55; s[511] = 0xAA;
        Assert.Null(MbrPartitionTable.TryParse(s, 1_000_000));
    }

    [Fact]
    public void Rdb_WriteThenRead_PreservesAllFields()
    {
        var dev = new MemoryBlockDevice(64 * TestSupport.MiB);
        var geo = RdbGeometry.ForBlockCount(dev.SectorCount());
        var handler = TestSupport.FakeHandler();
        var spec = new RdbLayoutSpec(geo, 2,
            [
                new RdbPartitionSpec("DH0", 2, 40, DosType.FfsIntl, Bootable: true, BootPri: 5),
                new RdbPartitionSpec("DH1", 41, geo.Cylinders - 1, DosType.Pfs3, MaxTransfer: 0xFE00, NoMount: true),
            ],
            [new RdbFileSystemSpec(DosType.Pfs3, 19 << 16 | 2, handler)]);
        RdbWriter.Write(dev, spec);

        var rdb = RdbReader.TryRead(dev);
        Assert.NotNull(rdb);
        Assert.Empty(rdb.Problems);
        Assert.True(rdb.Rdsk.ChecksumValid);
        Assert.Equal(geo, rdb.Rdsk.Geometry);
        Assert.Equal(2u, rdb.Rdsk.LoCylinder);
        Assert.Equal(2, rdb.Partitions.Count);

        var dh0 = rdb.Partitions[0];
        Assert.Equal("DH0", dh0.DriveName);
        Assert.True(dh0.IsBootable);
        Assert.Equal(5, dh0.BootPri);
        Assert.Equal(DosType.FfsIntl, dh0.DosType);
        Assert.Equal(2L * geo.CylinderBlocks * 512, dh0.StartByte);

        var dh1 = rdb.Partitions[1];
        Assert.True(dh1.IsNoMount);
        Assert.Equal(0xFE00u, dh1.MaxTransfer);
        Assert.Equal(dev.Length / (geo.CylinderBlocks * 512) * geo.CylinderBlocks * 512, dh1.EndByteExclusive);

        var fs = Assert.Single(rdb.FileSystems);
        Assert.Equal(DosType.Pfs3, fs.DosType);
        Assert.Equal("19.2", fs.VersionText);
        Assert.Equal(RdbWriter.LoadSegBlocksFor(handler.Length), fs.LoadSegBlockCount);
        var data = RdbReader.ReadLoadSegData(dev, fs.SegListBlock);
        Assert.Equal(handler, data.AsSpan(0, handler.Length).ToArray());
    }

    [Fact]
    public void Rdb_FoundAnywhereInFirst16Blocks()
    {
        var dev = new MemoryBlockDevice(16 * TestSupport.MiB);
        var geo = RdbGeometry.ForBlockCount(dev.SectorCount());
        RdbWriter.Write(dev, new RdbLayoutSpec(geo, 2, [new RdbPartitionSpec("DH0", 2, geo.Cylinders - 1, DosType.FfsIntl)], []));
        var rdsk = dev.ReadSector(0);
        dev.WriteSector(0, new byte[512]);
        dev.WriteSector(7, rdsk);
        Assert.Equal(7, RdbReader.FindRdsk(dev));
        dev.WriteSector(7, new byte[512]);
        dev.WriteSector(16, rdsk);
        Assert.Equal(-1, RdbReader.FindRdsk(dev));
    }

    [Fact]
    public void Rdb_ReportsBadChecksum_WithoutThrowing()
    {
        var dev = new MemoryBlockDevice(16 * TestSupport.MiB);
        var geo = RdbGeometry.ForBlockCount(dev.SectorCount());
        RdbWriter.Write(dev, new RdbLayoutSpec(geo, 2, [new RdbPartitionSpec("DH0", 2, geo.Cylinders - 1, DosType.FfsIntl)], []));
        var b = dev.ReadSector(1);
        b[40] ^= 0xFF; // corrupt the partition name
        dev.WriteSector(1, b);
        var rdb = RdbReader.TryRead(dev)!;
        Assert.False(rdb.Partitions[0].ChecksumValid);
        Assert.Contains(rdb.Problems, p => p.Contains("checksum"));
    }

    [Fact]
    public void Rdb_DetectsLoopInPartitionList()
    {
        var dev = new MemoryBlockDevice(16 * TestSupport.MiB);
        var geo = RdbGeometry.ForBlockCount(dev.SectorCount());
        RdbWriter.Write(dev, new RdbLayoutSpec(geo, 2, [new RdbPartitionSpec("DH0", 2, geo.Cylinders - 1, DosType.FfsIntl)], []));
        var b = dev.ReadSector(1);
        IO.Endian.PutU32BE(b, 16, 1); // next -> itself
        AmigaChecksum.SetSumToZero(b, 64, 8);
        dev.WriteSector(1, b);
        var rdb = RdbReader.TryRead(dev)!;
        Assert.Single(rdb.Partitions);
        Assert.Contains(rdb.Problems, p => p.Contains("broken"));
    }

    [Theory]
    [InlineData("DH0", true)]
    [InlineData("SDH1", true)]
    [InlineData("", false)]
    [InlineData("DH:0", false)]
    [InlineData("A/B", false)]
    [InlineData("WITH SPACE", false)]
    public void Rdb_DriveNameValidation(string name, bool valid) => Assert.Equal(valid, RdbWriter.IsValidDriveName(name));

    [Theory]
    [InlineData("DOS\\3", 0x444F5303u)]
    [InlineData("PFS\\3", 0x50465303u)]
    [InlineData("0x444F5301", 0x444F5301u)]
    public void DosType_Parse(string text, uint value)
    {
        Assert.Equal(value, DosType.Parse(text).Value);
        Assert.Equal(text.StartsWith("0x") ? "DOS\\1" : text, DosType.Parse(text).ToString());
    }

    [Fact]
    public void HunkFile_ReadsVersion()
    {
        var v = HunkFile.ReadVersion(TestSupport.FakeHandler());
        Assert.NotNull(v);
        Assert.Equal(19u, v.Value.Major);
        Assert.Equal(2u, v.Value.Minor);
        Assert.True(HunkFile.IsHunkExecutable(TestSupport.FakeHandler()));
        Assert.False(HunkFile.IsHunkExecutable(new byte[64]));
    }
}
