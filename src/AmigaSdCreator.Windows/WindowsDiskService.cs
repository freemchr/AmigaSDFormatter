using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace AmigaSdCreator.Windows;

/// <summary>
/// Disk discovery through the Windows Storage Management API (MSFT_Disk / MSFT_Partition), which
/// reports IsSystem, IsBoot and the bus type directly, plus raw access via \\.\PhysicalDriveN.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskService : IDiskService
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public Task<IReadOnlyList<DiskDescriptor>> EnumerateAsync(CancellationToken ct = default) =>
        Task.Run(() => Enumerate(), ct);

    public IReadOnlyList<DiskDescriptor> Enumerate()
    {
        var partitions = QueryPartitions();
        var pageFileLetters = QueryPageFileLetters();
        string systemLetter = Path.GetPathRoot(Environment.SystemDirectory)?[..1].ToUpperInvariant() ?? "C";
        var disks = new List<DiskDescriptor>();

        using var searcher = new ManagementObjectSearcher(StorageScope, "SELECT * FROM MSFT_Disk");
        foreach (ManagementObject d in searcher.Get())
        {
            using (d)
            {
                int number = Convert.ToInt32(d["Number"]);
                var parts = partitions.Where(p => p.Disk == number).ToList();
                var letters = parts.Where(p => p.Letter != '\0').Select(p => p.Letter.ToString()).ToList();
                disks.Add(new DiskDescriptor
                {
                    Number = number,
                    DevicePath = $@"\\.\PhysicalDrive{number}",
                    FriendlyName = S(d["FriendlyName"]),
                    Manufacturer = S(d["Manufacturer"]),
                    Model = S(d["Model"]),
                    SerialNumber = S(d["SerialNumber"]),
                    UniqueId = S(d["UniqueId"]),
                    SizeBytes = Convert.ToInt64(d["Size"] ?? 0L),
                    LogicalSectorSize = Convert.ToInt32(d["LogicalSectorSize"] ?? 512),
                    PhysicalSectorSize = Convert.ToInt32(d["PhysicalSectorSize"] ?? 512),
                    Bus = MapBus(Convert.ToInt32(d["BusType"] ?? 0)),
                    IsSystem = B(d["IsSystem"]) || letters.Contains(systemLetter),
                    IsBoot = B(d["IsBoot"]) || B(d["BootFromDisk"]) && B(d["IsSystem"]),
                    HoldsPageFile = letters.Any(pageFileLetters.Contains),
                    IsReadOnly = B(d["IsReadOnly"]),
                    IsOffline = B(d["IsOffline"]),
                    HasMedia = Convert.ToInt64(d["Size"] ?? 0L) > 0,
                    IsRemovableMedia = MapBus(Convert.ToInt32(d["BusType"] ?? 0)) is StorageBus.Usb or StorageBus.Sd or StorageBus.Mmc,
                    PartitionStyle = Convert.ToInt32(d["PartitionStyle"] ?? 0) switch { 1 => "MBR", 2 => "GPT", _ => "RAW" },
                    DriveLetters = letters,
                    VolumePaths = parts.SelectMany(p => p.VolumePaths).Distinct().ToList(),
                });
            }
        }
        return disks.OrderBy(d => d.Number).ToList();
    }

    public IBlockDevice OpenRead(DiskDescriptor disk) =>
        new RawDiskDevice(Native.Open(disk.DevicePath, write: false), $"{disk.DisplayName} (disk {disk.Number})", canWrite: false);

    public IExclusiveDiskHandle OpenForWrite(DeviceIdentity identity)
    {
        var current = Enumerate().FirstOrDefault(d => d.DevicePath == identity.DevicePath)
            ?? throw new IOException("The selected card is no longer connected. Nothing was written.");
        if (current.Identity != identity)
            throw new IOException("The device at this position is not the card that was selected. Nothing was written.");
        if (current.IsSystem || current.IsBoot || current.HoldsPageFile)
            throw new UnauthorizedAccessException("Refusing to open a Windows system disk for writing.");

        var locks = new List<SafeFileHandle>();
        try
        {
            foreach (var vol in current.VolumePaths)
                locks.Add(LockAndDismount(vol, current.Number));

            var handle = Native.Open(identity.DevicePath, write: true);
            var device = new RawDiskDevice(handle, $"{current.DisplayName} (disk {current.Number})", canWrite: true);
            if (device.Length != identity.SizeBytes || device.SectorSize != identity.SectorSize)
            {
                device.Dispose();
                throw new IOException("The opened disk's size does not match the selected card. Nothing was written.");
            }
            return new ExclusiveDiskHandle(device, locks);
        }
        catch
        {
            foreach (var l in locks) l.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a volume, confirms it lives on the target disk, and locks + dismounts it so that
    /// Windows does not cache or rewrite filesystem data while the card is being written.
    /// </summary>
    private static SafeFileHandle LockAndDismount(string volumePath, int diskNumber)
    {
        string path = volumePath.TrimEnd('\\');
        var h = Native.Open(path, write: true);
        try
        {
            var onDisks = Native.VolumeDiskNumbers(h);
            if (onDisks.Count > 0 && !onDisks.All(n => n == diskNumber))
                throw new IOException("A volume on the card also spans another disk; refusing to continue.");
            bool locked = false;
            for (int attempt = 0; attempt < 10 && !locked; attempt++)
            {
                try { Native.Ioctl(h, Native.FsctlLockVolume, "Locking the card's volume"); locked = true; }
                catch (IOException) when (attempt < 9) { Thread.Sleep(300); }
            }
            Native.Ioctl(h, Native.FsctlDismountVolume, "Dismounting the card's volume");
            return h;
        }
        catch (IOException ex)
        {
            h.Dispose();
            throw new IOException("Windows could not release the card. Close any Explorer windows or programs using it and try again. " + ex.Message, ex);
        }
    }

    private sealed record PartitionInfo(int Disk, char Letter, IReadOnlyList<string> VolumePaths);

    private static List<PartitionInfo> QueryPartitions()
    {
        var list = new List<PartitionInfo>();
        using var searcher = new ManagementObjectSearcher(StorageScope, "SELECT DiskNumber, DriveLetter, AccessPaths FROM MSFT_Partition");
        foreach (ManagementObject p in searcher.Get())
        {
            using (p)
            {
                var access = (p["AccessPaths"] as string[]) ?? [];
                char letter = p["DriveLetter"] is char c ? c : p["DriveLetter"] is ushort u ? (char)u : '\0';
                list.Add(new PartitionInfo(Convert.ToInt32(p["DiskNumber"]), char.ToUpperInvariant(letter),
                    access.Where(a => a.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)).ToList()));
            }
        }
        return list;
    }

    private static HashSet<string> QueryPageFileLetters()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
            foreach (ManagementObject p in s.Get())
                using (p)
                    if (S(p["Name"]) is { Length: > 0 } name) set.Add(name[..1]);
        }
        catch (ManagementException) { }
        return set;
    }

    private static StorageBus MapBus(int t) => t switch
    {
        1 => StorageBus.Scsi, 3 => StorageBus.Ata, 7 => StorageBus.Usb, 8 => StorageBus.Raid, 10 => StorageBus.Sas,
        11 => StorageBus.Sata, 12 => StorageBus.Sd, 13 => StorageBus.Mmc, 14 => StorageBus.Virtual,
        15 => StorageBus.FileBackedVirtual, 17 => StorageBus.Nvme, 0 => StorageBus.Unknown, _ => StorageBus.Other,
    };

    private static string S(object? o) => o?.ToString()?.Trim() ?? "";
    private static bool B(object? o) => o is bool b && b;

    private sealed class ExclusiveDiskHandle(RawDiskDevice device, List<SafeFileHandle> volumeLocks) : IExclusiveDiskHandle
    {
        public IBlockDevice Device => device;

        public void RefreshPartitionTable()
        {
            try { Native.Ioctl(device.Handle, Native.IoctlDiskUpdateProperties, "Refreshing the partition table"); }
            catch (IOException) { /* Windows will re-read the table when the card is reinserted. */ }
        }

        public void Dispose()
        {
            device.Dispose();
            foreach (var l in volumeLocks) l.Dispose(); // closing the handle releases the lock
        }
    }
}
