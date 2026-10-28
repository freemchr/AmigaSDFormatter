using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.FileSystems;

public enum FilesystemKind
{
    Unknown,
    Blank,
    Fat12,
    Fat16,
    Fat32,
    ExFat,
    Ntfs,
    AmigaDos,
    Pfs,
    Sfs,
    OtherAmiga,
}

public sealed record FilesystemInfo
{
    public FilesystemKind Kind { get; init; }
    public string? Label { get; init; }
    public DosType? DosType { get; init; }
    public long? ClusterBytes { get; init; }
    public long? TotalBytes { get; init; }
    public long? FreeBytes { get; init; }
    public int? BytesPerSector { get; init; }

    /// <summary>True when the structure was validated beyond a signature check (e.g. root block checksum).</summary>
    public bool StructureValidated { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string DisplayName => Kind switch
    {
        FilesystemKind.Blank => "Empty (no filesystem)",
        FilesystemKind.Fat12 => "FAT12",
        FilesystemKind.Fat16 => "FAT16",
        FilesystemKind.Fat32 => "FAT32",
        FilesystemKind.ExFat => "exFAT",
        FilesystemKind.Ntfs => "NTFS",
        FilesystemKind.AmigaDos or FilesystemKind.Pfs or FilesystemKind.Sfs or FilesystemKind.OtherAmiga =>
            DosType?.FriendlyName ?? "Amiga filesystem",
        _ => "Unknown / unrecognised",
    };

    public bool IsFat => Kind is FilesystemKind.Fat12 or FilesystemKind.Fat16 or FilesystemKind.Fat32;
    public bool IsAmiga => Kind is FilesystemKind.AmigaDos or FilesystemKind.Pfs or FilesystemKind.Sfs or FilesystemKind.OtherAmiga;
}
