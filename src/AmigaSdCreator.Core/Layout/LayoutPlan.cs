using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Layout;

public enum MessageSeverity { Info, Warning, Error }

public sealed record PlanMessage(MessageSeverity Severity, string Text)
{
    public static PlanMessage Info(string t) => new(MessageSeverity.Info, t);
    public static PlanMessage Warn(string t) => new(MessageSeverity.Warning, t);
    public static PlanMessage Error(string t) => new(MessageSeverity.Error, t);
}

public sealed record PartitionPlan
{
    public required string DeviceName { get; init; }
    public required string VolumeName { get; init; }
    /// <summary>Requested size; 0 means "all remaining space". Actual size is cylinder rounded.</summary>
    public long RequestedBytes { get; init; }
    public DosType DosType { get; init; } = DosType.FfsIntl;
    public bool Bootable { get; init; }
    public int BootPriority { get; init; }
    /// <summary>Create an empty filesystem now (only possible for OFS/FFS). Otherwise the partition is left for the Amiga to format.</summary>
    public bool FormatNow { get; init; } = true;
    public uint MaxTransfer { get; init; } = 0x1FE00;
    public uint Mask { get; init; } = 0x7FFFFFFE;
    public uint NumBuffers { get; init; } = 30;

    public uint LowCyl { get; init; }
    public uint HighCyl { get; init; }

    public bool WillBeFormatted => FormatNow && FileSystems.AmigaFfsFormatter.CanFormat(DosType);
}

public sealed record FileSystemDriverPlan(DosType DosType, string SourceName, byte[] Binary, uint Version, string VersionText)
{
    public RdbFileSystemSpec ToSpec() => new(DosType, Version, Binary);
}

public sealed record LayoutPlan
{
    public required LayoutKind Kind { get; init; }
    public required long DeviceBytes { get; init; }
    public int SectorSize { get; init; } = 512;
    public required string ProfileId { get; init; }
    public required string InterfaceId { get; init; }
    public string? CompatibilityModeId { get; init; }
    public IntendedUse Use { get; init; }

    // MBR + FAT32
    public string FatLabel { get; init; } = "SD0";
    public int? FatClusterBytes { get; init; }
    public uint FatStartLba { get; init; } = 2048;
    public uint FatSectorCount { get; init; }

    // RDB
    public RdbGeometry? Geometry { get; init; }
    public uint RdbCylinders { get; init; } = 2;
    public IReadOnlyList<PartitionPlan> Partitions { get; init; } = [];
    public IReadOnlyList<FileSystemDriverPlan> FileSystems { get; init; } = [];

    public long CylinderBytes => Geometry is null ? 0 : (long)Geometry.CylinderBlocks * SectorSize;
    public long PartitionBytes(PartitionPlan p) => (p.HighCyl - p.LowCyl + 1L) * CylinderBytes;
    public long PartitionStartBytes(PartitionPlan p) => p.LowCyl * CylinderBytes;

    public long UnallocatedBytes => Kind switch
    {
        LayoutKind.Rdb when Geometry is not null => DeviceBytes - (Partitions.Count == 0 ? RdbCylinders * CylinderBytes : (Partitions.Max(p => p.HighCyl) + 1L) * CylinderBytes),
        LayoutKind.MbrFat32 => DeviceBytes - ((long)FatStartLba + FatSectorCount) * SectorSize,
        _ => 0,
    };

    public RdbLayoutSpec ToRdbSpec() => new(
        Geometry ?? throw new InvalidOperationException("RDB plan has no geometry."),
        RdbCylinders,
        Partitions.Select(p => new RdbPartitionSpec(p.DeviceName, p.LowCyl, p.HighCyl, p.DosType, p.Bootable, p.BootPriority,
            NumBuffers: p.NumBuffers, MaxTransfer: p.MaxTransfer, Mask: p.Mask)).ToList(),
        FileSystems.Select(f => f.ToSpec()).ToList());
}
