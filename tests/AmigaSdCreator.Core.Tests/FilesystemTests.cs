using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.Tests;

public class FilesystemTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(300)]
    [InlineData(1024)]
    public void Fat32_FormatProducesSpecCompliantLayout(int mib)
    {
        var dev = new MemoryBlockDevice(mib * TestSupport.MiB);
        Fat32Formatter.Format(dev, new Fat32FormatOptions { Label = "sd0", VolumeId = 0xCAFEF00D, HiddenSectors = 2048 });

        var bs = FatBootSector.TryParse(dev.ReadSector(0));
        Assert.NotNull(bs);
        Assert.Equal(FilesystemKind.Fat32, bs.Kind);
        Assert.Equal(dev.SectorCount(), bs.TotalSectors);
        Assert.Equal(2048u, bs.HiddenSectors);
        Assert.Equal(0xCAFEF00Du, bs.VolumeId);
        Assert.Equal("SD0", bs.BootSectorLabel);
        Assert.Equal(Fat32Formatter.ChooseClusterBytes(dev.Length), bs.ClusterBytes);
        Assert.True(bs.ClusterCount >= Fat32Formatter.MinClusters);
        // The FAT must be large enough to map every cluster (+2 reserved entries).
        Assert.True(bs.SectorsPerFat * 512 / 4 >= bs.ClusterCount + 2);
        // Backup boot sector identical to primary.
        Assert.Equal(dev.ReadSector(0), dev.ReadSector(6));

        var info = FilesystemProbe.Probe(dev);
        Assert.Equal(FilesystemKind.Fat32, info.Kind);
        Assert.Equal("SD0", info.Label);
        Assert.True(info.StructureValidated);
        Assert.Equal((bs.ClusterCount - 1) * bs.ClusterBytes, info.FreeBytes);
    }

    [Fact]
    public void Fat32_RejectsTooSmallVolume()
    {
        var dev = new MemoryBlockDevice(16 * TestSupport.MiB);
        Assert.Throws<InvalidOperationException>(() => Fat32Formatter.Format(dev, new Fat32FormatOptions { ClusterBytes = 4096 }));
    }

    [Theory]
    [InlineData("TOO-LONG-LABEL")]
    [InlineData("BAD:LABEL")]
    public void Fat32_RejectsInvalidLabels(string label) => Assert.Throws<ArgumentException>(() => Fat32Formatter.NormaliseLabel(label));

    [Theory]
    [InlineData(2016)]     // tiny, single bitmap block
    [InlineData(40320)]    // ~20 MiB, several bitmap blocks
    [InlineData(262080)]   // ~128 MiB: >25 bitmap blocks, needs bitmap extension blocks
    public void Ffs_FormatThenInspect(int blocks)
    {
        var dev = new MemoryBlockDevice(blocks * 512L);
        AmigaFfsFormatter.Format(dev, DosType.FfsIntl, "Work", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));
        var info = FilesystemProbe.Probe(dev);
        Assert.Equal(FilesystemKind.AmigaDos, info.Kind);
        Assert.Equal(DosType.FfsIntl, info.DosType);
        Assert.Equal("Work", info.Label);
        Assert.True(info.StructureValidated, string.Join(" ", info.Notes));

        long bitmaps = (blocks - 2 + AmigaFfsFormatter.BitsPerBitmapBlock - 1) / AmigaFfsFormatter.BitsPerBitmapBlock;
        long ext = bitmaps > 25 ? (bitmaps - 25 + 126) / 127 : 0;
        long used = 1 + bitmaps + ext; // root + bitmap + extension blocks (reserved blocks are not in the bitmap)
        Assert.Equal((blocks - 2 - used) * 512, info.FreeBytes);
    }

    [Fact]
    public void Ffs_RootBlockIsAtMiddle_LikeFloppy()
    {
        Assert.Equal(880, AmigaFfsFormatter.RootBlockFor(1760));  // DD floppy
        Assert.Equal(1760, AmigaFfsFormatter.RootBlockFor(3520)); // HD floppy
    }

    [Fact]
    public void Ffs_DetectsCorruptedRootChecksumAsUnvalidated()
    {
        var dev = new MemoryBlockDevice(2016 * 512L);
        AmigaFfsFormatter.Format(dev, DosType.Ffs, "X");
        long root = AmigaFfsFormatter.RootBlockFor(2016);
        var r = dev.ReadSector(root);
        r[440] ^= 1;
        dev.WriteSector(root, r);
        var info = FilesystemProbe.Probe(dev);
        Assert.Equal(FilesystemKind.AmigaDos, info.Kind);
        Assert.False(info.StructureValidated);
    }

    [Fact]
    public void Ffs_RefusesUnsupportedVariants()
    {
        var dev = new MemoryBlockDevice(2016 * 512L);
        Assert.Throws<NotSupportedException>(() => AmigaFfsFormatter.Format(dev, DosType.FfsDirCache, "X"));
        Assert.Throws<NotSupportedException>(() => AmigaFfsFormatter.Format(dev, DosType.Pfs3, "X"));
        Assert.Throws<ArgumentException>(() => AmigaFfsFormatter.Format(dev, DosType.Ffs, "Bad:Name"));
    }

    [Fact]
    public void Probe_RecognisesExFatNtfsAndBlank()
    {
        var dev = new MemoryBlockDevice(4 * TestSupport.MiB);
        Assert.Equal(FilesystemKind.Blank, FilesystemProbe.Probe(dev).Kind);

        var s = new byte[512];
        s[0] = 0xEB; s[1] = 0x76; s[2] = 0x90;
        "EXFAT   "u8.CopyTo(s.AsSpan(3));
        s[510] = 0x55; s[511] = 0xAA;
        dev.WriteSector(0, s);
        Assert.Equal(FilesystemKind.ExFat, FilesystemProbe.Probe(dev).Kind);

        "NTFS    "u8.CopyTo(s.AsSpan(3));
        dev.WriteSector(0, s);
        Assert.Equal(FilesystemKind.Ntfs, FilesystemProbe.Probe(dev).Kind);
    }

    [Fact]
    public void AmigaDate_RoundTrips()
    {
        var t = new DateTime(2026, 10, 28, 13, 37, 42, 500, DateTimeKind.Unspecified);
        var d = AmigaDate.From(t);
        Assert.Equal(t, d.ToDateTime());
        Assert.Equal(new AmigaDate(0, 0, 0), AmigaDate.From(new DateTime(1978, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)));
    }
}
