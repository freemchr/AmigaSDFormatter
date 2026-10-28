using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Layout;

/// <summary>Generates recommended layouts from a hardware profile and recalculates cylinder ranges.</summary>
public static class LayoutPlanner
{
    public const long MiB = 1024 * 1024;
    public const long GiB = 1024 * MiB;
    public const uint FatAlignmentLba = 2048; // 1 MiB, as Windows / Rufus align
    public const int MaxAutoPartitions = 8;
    public const long DefaultSystemPartitionBytes = 2 * GiB;

    public static LayoutPlan Recommend(HardwareProfile profile, StorageInterfaceProfile iface, IntendedUse use, long deviceBytes, int sectorSize = 512, string? compatibilityModeId = null)
    {
        if (!iface.Uses.Contains(use))
            throw new InvalidOperationException($"{iface.Name} does not support '{use}'.");
        var layout = iface.Layouts.FirstOrDefault();
        return layout switch
        {
            LayoutKind.MbrFat32 => RecommendFat(profile, iface, use, deviceBytes, sectorSize),
            LayoutKind.Rdb => RecommendRdb(profile, iface, use, deviceBytes, sectorSize, compatibilityModeId),
            LayoutKind.ImageRestore => new LayoutPlan
            {
                Kind = LayoutKind.ImageRestore, DeviceBytes = deviceBytes, SectorSize = sectorSize,
                ProfileId = profile.Id, InterfaceId = iface.Id, Use = use,
            },
            _ => throw new InvalidOperationException($"{iface.Name} has no supported layout."),
        };
    }

    private static LayoutPlan RecommendFat(HardwareProfile profile, StorageInterfaceProfile iface, IntendedUse use, long deviceBytes, int sectorSize)
    {
        long sectors = deviceBytes / sectorSize;
        long count = Math.Min(sectors - FatAlignmentLba, uint.MaxValue);
        int cluster = Fat32Formatter.ChooseClusterBytes(count * sectorSize);
        if (iface.Constraints.MaxFatClusterBytes is long max && cluster > max) cluster = (int)max;
        return new LayoutPlan
        {
            Kind = LayoutKind.MbrFat32,
            DeviceBytes = deviceBytes,
            SectorSize = sectorSize,
            ProfileId = profile.Id,
            InterfaceId = iface.Id,
            Use = use,
            FatLabel = iface.Defaults.FatLabel ?? "AMIGA",
            FatClusterBytes = cluster,
            FatStartLba = FatAlignmentLba,
            FatSectorCount = (uint)Math.Max(0, count),
        };
    }

    private static LayoutPlan RecommendRdb(HardwareProfile profile, StorageInterfaceProfile iface, IntendedUse use, long deviceBytes, int sectorSize, string? modeId)
    {
        var limits = iface.EffectiveLimits(modeId);
        var mode = iface.CompatibilityModes.FirstOrDefault(m => m.Id == modeId) ?? iface.CompatibilityModes.FirstOrDefault();
        var geometry = RdbGeometry.ForBlockCount(deviceBytes / sectorSize);
        long cylBytes = (long)geometry.CylinderBlocks * sectorSize;
        uint rdbCyl = (uint)Math.Max(2, (MiB + cylBytes - 1) / cylBytes);

        long usableEnd = (long)geometry.Cylinders * cylBytes;
        if (limits.MaxEnd is long maxEnd) usableEnd = Math.Min(usableEnd, maxEnd / cylBytes * cylBytes);
        long available = usableEnd - rdbCyl * cylBytes;

        var dosType = iface.Defaults.DefaultDosType;
        long perPartitionCap = limits.MaxFfs ?? long.MaxValue;
        string devPrefix = iface.Defaults.DeviceNamePrefix ?? "DH";
        string volPrefix = iface.Defaults.VolumeNamePrefix ?? "Work";

        var parts = new List<PartitionPlan>();
        int n = 0;
        if (use == IntendedUse.BootableSystem)
        {
            long sys = Math.Min(DefaultSystemPartitionBytes, Math.Min(perPartitionCap, available));
            if (limits.BootMaxEnd is long bootEnd) sys = Math.Min(sys, bootEnd - rdbCyl * cylBytes);
            parts.Add(NewPartition(iface, $"{devPrefix}{n++}", "System", sys, dosType, bootable: true));
            available -= RoundToCylinders(sys, cylBytes);
        }

        int dataPartitions = 0;
        // Small leftovers (< 64 MiB) are left unallocated, unless the card is so small that
        // this would mean having no data partition at all.
        while ((available >= 64 * MiB || (parts.Count == 0 && available >= 4 * MiB)) && parts.Count < MaxAutoPartitions)
        {
            long size = Math.Min(available, perPartitionCap);
            string vol = ++dataPartitions == 1 ? volPrefix : $"{volPrefix}{dataPartitions}";
            // A partition that takes everything left is requested as 0 ("rest") so no cylinder is wasted.
            parts.Add(NewPartition(iface, $"{devPrefix}{n++}", vol, size == available ? 0 : size, dosType, bootable: false));
            available -= RoundToCylinders(size, cylBytes);
        }

        var plan = new LayoutPlan
        {
            Kind = LayoutKind.Rdb,
            DeviceBytes = deviceBytes,
            SectorSize = sectorSize,
            ProfileId = profile.Id,
            InterfaceId = iface.Id,
            CompatibilityModeId = mode?.Id,
            Use = use,
            Geometry = geometry,
            RdbCylinders = rdbCyl,
            Partitions = parts,
        };
        return Recalculate(plan, limits.MaxEnd);
    }

    private static PartitionPlan NewPartition(StorageInterfaceProfile iface, string dev, string vol, long bytes, DosType dosType, bool bootable) => new()
    {
        DeviceName = dev,
        VolumeName = vol,
        RequestedBytes = bytes,
        DosType = dosType,
        Bootable = bootable,
        BootPriority = bootable ? 0 : -128,
        MaxTransfer = iface.Defaults.DefaultMaxTransfer,
        Mask = iface.Defaults.DefaultMask,
    };

    private static long RoundToCylinders(long bytes, long cylBytes) => Math.Max(1, (bytes + cylBytes / 2) / cylBytes) * cylBytes;

    /// <summary>
    /// Assigns consecutive cylinder ranges to partitions in order. A RequestedBytes of 0 takes all the
    /// remaining space up to <paramref name="maxEndBytes"/> (or the end of the device).
    /// </summary>
    public static LayoutPlan Recalculate(LayoutPlan plan, long? maxEndBytes = null)
    {
        if (plan.Kind != LayoutKind.Rdb || plan.Geometry is null) return plan;
        long cylBytes = plan.CylinderBytes;
        uint lastCyl = plan.Geometry.Cylinders - 1;
        if (maxEndBytes is long me) lastCyl = (uint)Math.Min(lastCyl, me / cylBytes - 1);

        uint next = plan.RdbCylinders;
        var result = new List<PartitionPlan>();
        foreach (var p in plan.Partitions)
        {
            uint low = next;
            uint high;
            if (p.RequestedBytes <= 0) high = lastCyl;
            else
            {
                long cyls = Math.Max(1, (p.RequestedBytes + cylBytes / 2) / cylBytes);
                high = (uint)Math.Min(low + cyls - 1, lastCyl);
            }
            if (low > lastCyl) high = low - 1; // no room: an invalid range the validator reports
            result.Add(p with { LowCyl = low, HighCyl = high });
            next = high + 1;
        }
        return plan with { Partitions = result };
    }
}
