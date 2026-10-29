using System.Security.Cryptography;
using System.Text;
using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Safety;

public enum StorageBus { Unknown, Usb, Sd, Mmc, Ata, Sata, Scsi, Sas, Nvme, Raid, Virtual, FileBackedVirtual, Other }

/// <summary>Everything the application knows about a physical disk, as reported by the OS.</summary>
public sealed record DiskDescriptor
{
    public required int Number { get; init; }
    public required string DevicePath { get; init; }
    public string FriendlyName { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>Kept in memory for identity checks only. Never written to logs or reports.</summary>
    public string SerialNumber { get; init; } = "";
    public string UniqueId { get; init; } = "";
    public long SizeBytes { get; init; }
    public int LogicalSectorSize { get; init; } = 512;
    public int PhysicalSectorSize { get; init; } = 512;
    public StorageBus Bus { get; init; }
    public bool IsRemovableMedia { get; init; }
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public bool HoldsPageFile { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsOffline { get; init; }
    public bool HasMedia { get; init; } = true;
    public string PartitionStyle { get; init; } = "";
    public IReadOnlyList<string> DriveLetters { get; init; } = [];
    /// <summary>Where the OS has mounted the disk's readable (FAT) partitions, in partition order: "E:\" or "/media/user/SD0".</summary>
    public IReadOnlyList<string> MountPoints { get; init; } = [];
    /// <summary>How the OS names the disk for people, e.g. "Disk 2" or "/dev/sdb".</summary>
    public string Location { get; init; } = "";
    /// <summary>Mounted volumes (e.g. \\?\Volume{...}\) that must be locked and dismounted before raw writes.</summary>
    public IReadOnlyList<string> VolumePaths { get; init; } = [];

    public bool IsCardReaderBus => Bus is StorageBus.Usb or StorageBus.Sd or StorageBus.Mmc;

    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? LocationText : FriendlyName.Trim();
    public string LocationText => string.IsNullOrEmpty(Location) ? $"Disk {Number}" : Location;

    /// <summary>Short, non-reversible identifier for logs.</summary>
    public string LogId => $"disk{Number}-{ShortHash(SerialNumber + "|" + UniqueId)}";

    public DeviceIdentity Identity => new(DevicePath, Number, Model.Trim(), SerialNumber.Trim(), UniqueId, SizeBytes, LogicalSectorSize);

    private static string ShortHash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8];
}

/// <summary>Captured when the user selects a target; re-checked immediately before every write.</summary>
public sealed record DeviceIdentity(string DevicePath, int Number, string Model, string SerialNumber, string UniqueId, long SizeBytes, int SectorSize);

/// <summary>Platform services for discovering and opening physical disks.</summary>
public interface IDiskService
{
    bool IsElevated { get; }
    Task<IReadOnlyList<DiskDescriptor>> EnumerateAsync(CancellationToken ct = default);
    /// <summary>Opens a disk read-only. Does not lock or dismount anything.</summary>
    IBlockDevice OpenRead(DiskDescriptor disk);
    /// <summary>
    /// Locks and dismounts all volumes on the disk and opens it for writing. Implementations must
    /// verify that the disk at the path still matches <paramref name="identity"/> after opening.
    /// </summary>
    IExclusiveDiskHandle OpenForWrite(DeviceIdentity identity);
}

public interface IExclusiveDiskHandle : IDisposable
{
    IBlockDevice Device { get; }
    /// <summary>Asks the OS to re-read the partition table after writing.</summary>
    void RefreshPartitionTable();
}
