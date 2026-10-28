using AmigaSdCreator.Core.Imaging;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;
using AmigaSdCreator.Core.Safety;

namespace AmigaSdCreator.Core.Tests;

public class SafetyAndImagingTests
{
    private static DiskDescriptor Card(int n = 3, StorageBus bus = StorageBus.Usb, long size = 256 * TestSupport.MiB) => new()
    {
        Number = n, DevicePath = $@"\\.\PhysicalDrive{n}", Model = "Generic SD Reader", SerialNumber = "000000001234",
        UniqueId = "USBSTOR\\DISK&VEN_GENERIC", SizeBytes = size, Bus = bus, IsRemovableMedia = true,
    };

    [Fact]
    public void Guard_PassesForSelectedCard()
    {
        var c = Card();
        var checks = WriteGuard.Evaluate(c, c.Identity, c.SizeBytes, "ERASE", new SafetyPolicy());
        Assert.True(WriteGuard.AllPassed(checks), string.Join(" ", checks.Where(x => !x.Passed).Select(x => x.Detail)));
    }

    [Fact]
    public void Guard_BlocksSwappedCard_EvenAtSamePath()
    {
        var selected = Card();
        var swapped = selected with { SerialNumber = "999", SizeBytes = 512 * TestSupport.MiB };
        var checks = WriteGuard.Evaluate(swapped, selected.Identity, 1, "ERASE", new SafetyPolicy());
        Assert.False(checks.Single(x => x.Name == "Same device as selected").Passed);
    }

    [Fact]
    public void Guard_BlocksMissingDevice()
    {
        var checks = WriteGuard.Evaluate(null, Card().Identity, 1, "ERASE", new SafetyPolicy());
        Assert.False(WriteGuard.AllPassed(checks));
    }

    [Fact]
    public void Guard_NeverAllowsSystemDisk_EvenWithPermissivePolicy()
    {
        var sys = Card(0, StorageBus.Nvme) with { IsSystem = true, IsBoot = true };
        var policy = new SafetyPolicy { AllowNonRemovableBus = true, AllowLargeDisks = true };
        Assert.False(WriteGuard.AllPassed(WriteGuard.Evaluate(sys, sys.Identity, 1, "ERASE", policy)));
        Assert.Equal("Windows system disk", WriteGuard.ProtectionReason(sys, policy));
    }

    [Theory]
    [InlineData(StorageBus.Sata, "Internal Sata disk")]
    [InlineData(StorageBus.Nvme, "Internal Nvme disk")]
    public void Guard_BlocksInternalDisksByDefault(StorageBus bus, string reason)
    {
        var d = Card(1, bus);
        Assert.Equal(reason, WriteGuard.ProtectionReason(d, new SafetyPolicy()));
        Assert.False(WriteGuard.AllPassed(WriteGuard.Evaluate(d, d.Identity, 1, "ERASE", new SafetyPolicy())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("erase")]
    [InlineData("yes")]
    public void Guard_RequiresExactConfirmation(string typed)
    {
        var c = Card();
        Assert.False(WriteGuard.AllPassed(WriteGuard.Evaluate(c, c.Identity, 1, typed, new SafetyPolicy())));
    }

    [Fact]
    public void Guard_BlocksWriteProtectedAndLargeDisks()
    {
        var ro = Card() with { IsReadOnly = true };
        Assert.False(WriteGuard.AllPassed(WriteGuard.Evaluate(ro, ro.Identity, 1, "ERASE", new SafetyPolicy())));
        var big = Card(size: 2L << 40);
        Assert.Equal("Larger than 512 GB", WriteGuard.ProtectionReason(big, new SafetyPolicy()));
    }

    [Theory]
    [InlineData(".img")]
    [InlineData(".img.gz")]
    public async Task Backup_Then_Restore_RoundTrips(string ext)
    {
        var source = new MemoryBlockDevice(32 * TestSupport.MiB);
        var (p, i) = TestSupport.Iface("a6000-sd-hdd");
        DiskImageBuilder.Apply(source, LayoutPlanner.Recommend(p, i, IntendedUse.HardDrive, source.Length), i);
        new Random(1).NextBytes(source.Data.AsSpan(20 * 1024 * 1024, 4096));

        var path = TestSupport.TempFile(ext);
        var stages = new List<string>();
        var manifest = await DiskImager.BackupAsync(source, path, new SyncProgress<ImagingProgress>(x => stages.Add(x.Stage)));
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(DiskImager.ManifestPath(path)));
        Assert.False(File.Exists(path + ".partial"));
        Assert.Contains("Checking image", stages);

        var info = await DiskImager.InspectImageAsync(path);
        Assert.True(info.ManifestMatches);
        Assert.Equal(manifest.Sha256, info.Sha256);
        Assert.Equal(ext.EndsWith(".gz"), info.Compressed);

        var target = new MemoryBlockDevice(64 * TestSupport.MiB);
        await DiskImager.RestoreAsync(info, target);
        Assert.Equal(source.Data, target.Data.AsSpan(0, source.Data.Length).ToArray());
    }

    [Fact]
    public async Task Restore_RefusesDamagedImage_BeforeWriting()
    {
        var source = new MemoryBlockDevice(4 * TestSupport.MiB);
        source.Data[1000] = 1;
        var path = TestSupport.TempFile();
        await DiskImager.BackupAsync(source, path);
        await using (var f = File.OpenWrite(path)) { f.Position = 5000; f.WriteByte(0xAA); }

        var info = await DiskImager.InspectImageAsync(path);
        Assert.False(info.ManifestMatches);
        var target = new MemoryBlockDevice(4 * TestSupport.MiB);
        await Assert.ThrowsAsync<ImagingException>(() => DiskImager.RestoreAsync(info, target));
        Assert.True(target.Data.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public async Task Restore_RefusesImageLargerThanTarget()
    {
        var source = new MemoryBlockDevice(8 * TestSupport.MiB);
        var path = TestSupport.TempFile();
        await DiskImager.BackupAsync(source, path);
        var info = await DiskImager.InspectImageAsync(path);
        var ex = await Assert.ThrowsAsync<ImagingException>(() => DiskImager.RestoreAsync(info, new MemoryBlockDevice(4 * TestSupport.MiB)));
        Assert.False(ex.TargetModified);
    }

    [Fact]
    public async Task Restore_DetectsFakeCapacityCard()
    {
        var source = new MemoryBlockDevice(8 * TestSupport.MiB);
        new Random(7).NextBytes(source.Data);
        var path = TestSupport.TempFile();
        await DiskImager.BackupAsync(source, path);
        var info = await DiskImager.InspectImageAsync(path);
        var ex = await Assert.ThrowsAsync<ImagingException>(() => DiskImager.RestoreAsync(info, new WrappingDevice(16 * TestSupport.MiB, 4 * TestSupport.MiB)));
        Assert.True(ex.TargetModified);
        Assert.Contains("counterfeit", ex.Message);
    }

    [Fact]
    public async Task Restore_ReportsInterruptedWrite()
    {
        var source = new MemoryBlockDevice(16 * TestSupport.MiB);
        var path = TestSupport.TempFile();
        await DiskImager.BackupAsync(source, path);
        var info = await DiskImager.InspectImageAsync(path);
        var ex = await Assert.ThrowsAsync<ImagingException>(() => DiskImager.RestoreAsync(info, new FailingDevice(16 * TestSupport.MiB, failAfterBytes: 8 * TestSupport.MiB)));
        Assert.True(ex.TargetModified);
        Assert.Contains("incomplete", ex.Message);
    }

    [Fact]
    public async Task CardOperations_NeverFallsBackToAnotherDisk()
    {
        var selected = Card(3);
        var other = Card(4);
        var service = new FakeDiskService([other]); // card 3 was removed; card 4 is present
        var ops = new CardOperations(service, new SafetyPolicy());
        var (p, i) = TestSupport.Iface("a6000-sd-data");
        var plan = LayoutPlanner.Recommend(p, i, IntendedUse.DataExchange, selected.SizeBytes);
        var ex = await Assert.ThrowsAsync<SafetyCheckFailedException>(() => ops.PrepareAsync(selected.Identity, "ERASE", plan, i));
        Assert.Contains(ex.Checks, c => c.Name == "Device present" && !c.Passed);
        Assert.Equal(0, service.WriteOpens);
    }

    [Fact]
    public async Task CardOperations_PreparesAndVerifies_SelectedCard()
    {
        var card = Card(3);
        var service = new FakeDiskService([card]);
        var ops = new CardOperations(service, new SafetyPolicy());
        var (p, i) = TestSupport.Iface("a6000-sd-data");
        var plan = LayoutPlanner.Recommend(p, i, IntendedUse.DataExchange, card.SizeBytes);
        var result = await ops.PrepareAsync(card.Identity, "ERASE", plan, i);
        Assert.True(result.Passed);
        Assert.Equal(1, service.WriteOpens);
        Assert.True(service.Refreshed);
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    /// <summary>Simulates a counterfeit card whose real capacity is smaller: addresses wrap around.</summary>
    private sealed class WrappingDevice(long claimed, long real) : IBlockDevice
    {
        private readonly MemoryBlockDevice _inner = new(real);
        public string Description => "fake card";
        public long Length => claimed;
        public int SectorSize => 512;
        public bool CanWrite => true;
        public void Read(long offset, Span<byte> buffer) => _inner.Read(offset % real, buffer);
        public void Write(long offset, ReadOnlySpan<byte> buffer) => _inner.Write(offset % real, buffer);
        public void Flush() { }
        public void Dispose() { }
    }

    private sealed class FailingDevice(long length, long failAfterBytes) : IBlockDevice
    {
        private long _written;
        public string Description => "failing card";
        public long Length => length;
        public int SectorSize => 512;
        public bool CanWrite => true;
        public void Read(long offset, Span<byte> buffer) => buffer.Clear();
        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            if (_written >= failAfterBytes) throw new IOException("The device is not ready.");
            _written += buffer.Length;
        }
        public void Flush() { }
        public void Dispose() { }
    }

    private sealed class FakeDiskService(IReadOnlyList<DiskDescriptor> disks) : IDiskService
    {
        private readonly Dictionary<string, MemoryBlockDevice> _media = disks.ToDictionary(d => d.DevicePath, d => new MemoryBlockDevice(d.SizeBytes));
        public int WriteOpens { get; private set; }
        public bool Refreshed { get; private set; }
        public bool IsElevated => true;
        public Task<IReadOnlyList<DiskDescriptor>> EnumerateAsync(CancellationToken ct = default) => Task.FromResult(disks);
        public IBlockDevice OpenRead(DiskDescriptor disk) => _media[disk.DevicePath];
        public IExclusiveDiskHandle OpenForWrite(DeviceIdentity identity)
        {
            WriteOpens++;
            return new Handle(_media[identity.DevicePath], this);
        }

        private sealed class Handle(IBlockDevice device, FakeDiskService owner) : IExclusiveDiskHandle
        {
            public IBlockDevice Device => device;
            public void RefreshPartitionTable() => owner.Refreshed = true;
            public void Dispose() { }
        }
    }
}
