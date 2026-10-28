using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;
using Xunit.Abstractions;

namespace AmigaSdCreator.Core.Tests;

public class FfsVolumeTests(ITestOutputHelper output)
{
    private static readonly DateTime When = new(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified);

    private static byte[] Payload(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static MemoryBlockDevice Formatted(DosType dt, int blocks = 40320)
    {
        var dev = new MemoryBlockDevice(blocks * 512L);
        AmigaFfsFormatter.Format(dev, dt, "Work", When);
        return dev;
    }

    [Theory]
    [InlineData(0x444F5301u)] // FFS
    [InlineData(0x444F5303u)] // FFS Intl
    [InlineData(0x444F5300u)] // OFS
    public void WriteReadDelete_RoundTrip(uint dosType)
    {
        var dev = Formatted(new DosType(dosType));
        long freeBefore;
        using (var vol = AmigaFfsVolume.Open(dev, writable: true))
        {
            freeBefore = vol.FreeBytes;
            vol.CreateDirectory("S");
            vol.CreateDirectory("S/Deep");
            foreach (var size in new[] { 0, 1, 511, 512, 513, 488 * 72, 512 * 72 + 1, 300_000 })
                vol.WriteFile($"S/Deep/file{size}", new MemoryStream(Payload(size, size)), When, protection: 0x40, comment: $"size {size}");
        }

        Assert.True(AmigaFfsInspector.Inspect(dev).StructureValidated);
        using (var vol = AmigaFfsVolume.Open(dev, writable: true))
        {
            var list = vol.List("S/Deep");
            Assert.Equal(8, list.Count);
            foreach (var e in list)
            {
                int size = int.Parse(e.Name[4..]);
                Assert.Equal(size, e.Size);
                Assert.Equal($"size {size}", e.Comment);
                Assert.Equal(0x40u, e.Protection);
                Assert.Equal(When, e.Modified);
                var ms = new MemoryStream();
                vol.ReadFile(e.Path, ms);
                Assert.Equal(Payload(size, size), ms.ToArray());
            }
            Assert.Equal("-s--rwed", vol.GetEntry("S/Deep/file1")!.ProtectionText);
            Assert.NotNull(vol.GetEntry("s/deep/FILE1")); // AmigaDOS names are case-insensitive

            foreach (var e in list) vol.Delete(e.Path);
            vol.Delete("S/Deep");
            vol.Delete("S");
            Assert.Empty(vol.List(""));
        }
        using (var vol = AmigaFfsVolume.Open(dev, writable: false))
            Assert.Equal(freeBefore, vol.FreeBytes); // every block was returned to the bitmap
    }

    [Fact]
    public void ManyFilesInOneDirectory_ShareHashChains()
    {
        var dev = Formatted(DosType.FfsIntl);
        using (var vol = AmigaFfsVolume.Open(dev, writable: true))
            for (int i = 0; i < 300; i++) vol.WriteFile($"f{i}.info", new MemoryStream([(byte)i]), When);
        using (var vol = AmigaFfsVolume.Open(dev, writable: false))
        {
            Assert.Equal(300, vol.List("").Count);
            var ms = new MemoryStream();
            vol.ReadFile("F299.INFO", ms);
            Assert.Equal([(byte)43], ms.ToArray());
        }
    }

    [Fact]
    public void Overwrite_RequiresFlag_AndReplacesContent()
    {
        var dev = Formatted(DosType.Ffs);
        using var vol = AmigaFfsVolume.Open(dev, writable: true);
        vol.WriteFile("a", new MemoryStream([1, 2, 3]), When);
        Assert.Throws<IOException>(() => vol.WriteFile("A", new MemoryStream([9]), When));
        vol.WriteFile("A", new MemoryStream([9]), When, overwrite: true);
        var ms = new MemoryStream();
        vol.ReadFile("a", ms);
        Assert.Equal([9], ms.ToArray());
        Assert.Single(vol.List(""));
    }

    [Fact]
    public void Rejects_InvalidNames_FullVolume_AndNonEmptyDirectoryDelete()
    {
        var dev = Formatted(DosType.Ffs, blocks: 2016);
        using var vol = AmigaFfsVolume.Open(dev, writable: true);
        Assert.Throws<AmigaFsException>(() => AmigaFfsVolume.ValidateName("bad:name"));
        Assert.Throws<AmigaFsException>(() => AmigaFfsVolume.ValidateName("a/b"));
        Assert.Throws<AmigaFsException>(() => vol.WriteFile(new string('x', 31), new MemoryStream(), When));
        Assert.Throws<IOException>(() => vol.WriteFile("big", new MemoryStream(new byte[2 * 1024 * 1024]), When));
        vol.CreateDirectory("D");
        vol.WriteFile("D/x", new MemoryStream([1]), When);
        Assert.Throws<IOException>(() => vol.Delete("D"));
    }

    [Fact]
    public void InterruptedSession_LeavesBitmapFlaggedInvalid()
    {
        var dev = Formatted(DosType.Ffs);
        var vol = AmigaFfsVolume.Open(dev, writable: true);
        vol.WriteFile("x", new MemoryStream([1]), When);
        // Simulate a crash: no Flush/Dispose.
        Assert.False(AmigaFfsInspector.Inspect(dev).StructureValidated);
        Assert.Throws<AmigaFsException>(() => AmigaFfsVolume.Open(dev, writable: true));
        using (AmigaFfsVolume.Open(dev, writable: false)) { } // still readable
        vol.Flush();
        Assert.True(AmigaFfsInspector.Inspect(dev).StructureValidated);
    }

    [Fact]
    public void DirCacheVolumes_OpenReadOnly()
    {
        var dev = Formatted(DosType.Ffs);
        var boot = dev.ReadSector(0);
        Endian.PutU32BE(boot, 0, DosType.FfsDirCache.Value);
        dev.WriteSector(0, boot);
        using var vol = AmigaFfsVolume.Open(dev, writable: true);
        Assert.True(vol.IsReadOnly);
        Assert.Throws<AmigaFsException>(() => vol.CreateDirectory("x"));
    }

    // ----- Interop with amitools xdftool -----

    [Theory]
    [InlineData("ffs+intl", 0x444F5303u)]
    [InlineData("ofs", 0x444F5300u)]
    public void XdftoolReadsFilesWeWrote(string _, uint dosType)
    {
        if (TestSupport.AmitoolsBin is not { } bin) { output.WriteLine("SKIPPED: amitools not installed."); return; }
        var path = TestSupport.TempFile(".hdf");
        const int blocks = 40320;
        var big = Payload(150_000, 7); // needs extension blocks
        using (var dev = FileBlockDevice.Create(path, blocks * 512L))
        {
            AmigaFfsFormatter.Format(dev, new DosType(dosType), "Work", When);
            using var vol = AmigaFfsVolume.Open(dev, writable: true);
            vol.CreateDirectory("Devs");
            vol.WriteFile("Devs/big.bin", new MemoryStream(big), When, comment: "hello");
            vol.WriteFile("small.txt", new MemoryStream("Amiga!"u8.ToArray()), When);
        }

        string chs = $"chs={blocks / 1008},16,63";
        var outDir = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "xdftool"), path, "open", chs, "+", "list", "+", "unpack", outDir);
        output.WriteLine(text);
        Assert.Equal(0, exit);
        Assert.Contains("hello", text); // comment shown in listing
        var root = Path.Combine(outDir, "Work");
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(root, "Devs", "big.bin")));
        Assert.Equal("Amiga!", File.ReadAllText(Path.Combine(root, "small.txt")));
    }

    [Fact]
    public void WeReadFilesXdftoolWrote()
    {
        if (TestSupport.AmitoolsBin is not { } bin) { output.WriteLine("SKIPPED: amitools not installed."); return; }
        var dir = Path.Combine(Path.GetTempPath(), "asdc-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var big = Payload(200_000, 3);
        File.WriteAllBytes(Path.Combine(dir, "big.bin"), big);
        var path = Path.Combine(dir, "x.hdf");
        var (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "xdftool"), path, "create", "size=20Mi", "+", "format", "Their", "ffs",
            "+", "makedir", "C", "+", "write", Path.Combine(dir, "big.bin"), "C");
        output.WriteLine(text);
        Assert.Equal(0, exit);

        using var dev = FileBlockDevice.OpenReadWrite(path);
        using (var vol = AmigaFfsVolume.Open(dev, writable: true))
        {
            Assert.Equal("Their", vol.VolumeName);
            var ms = new MemoryStream();
            vol.ReadFile("C/big.bin", ms);
            Assert.Equal(big, ms.ToArray());
            vol.WriteFile("C/added", new MemoryStream([42]), When);
        }
        // And xdftool still accepts the volume after our modification.
        (exit, text) = TestSupport.Run(TestSupport.Tool(bin, "xdftool"), path, "list");
        output.WriteLine(text);
        Assert.Equal(0, exit);
        Assert.Contains("added", text);
    }
}
