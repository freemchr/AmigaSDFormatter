using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Layout;

/// <summary>
/// Checks a plan against the hardware profile. Any Error blocks image generation and disk writing.
/// Limits from a Kickstart compatibility mode are hard errors; interface-level caps that are
/// marked unverified in the profile are warnings.
/// </summary>
public static class LayoutValidator
{
    public static IReadOnlyList<PlanMessage> Validate(LayoutPlan plan, StorageInterfaceProfile iface)
    {
        var m = new List<PlanMessage>();

        switch (iface.Status)
        {
            case VerificationStatus.Planned:
                m.Add(PlanMessage.Error($"{iface.Name} is planned but not yet supported."));
                break;
            case VerificationStatus.Protected when plan.Kind != LayoutKind.ImageRestore:
                m.Add(PlanMessage.Error($"{iface.Name} holds the system installation; only backup and authorised image restore are allowed."));
                break;
            case VerificationStatus.Experimental:
                m.Add(PlanMessage.Warn($"{iface.Name} is experimental: the layout follows Amiga standards but has not been confirmed on this hardware. See the unverified items in the profile."));
                break;
        }

        if (plan.SectorSize > iface.Constraints.MaxSectorSize)
            m.Add(PlanMessage.Error($"The card reports {plan.SectorSize}-byte sectors; this interface needs {iface.Constraints.MaxSectorSize}-byte sectors."));
        if (iface.Constraints.RecommendedMaxCapacityBytes is long rec && plan.DeviceBytes > rec)
            m.Add(PlanMessage.Warn($"This card ({Format.Bytes(plan.DeviceBytes)}) is larger than the {Format.Bytes(rec)} the manufacturer documents for this slot. It may work, but a 32 GB or smaller SDHC card is the documented choice."));

        switch (plan.Kind)
        {
            case LayoutKind.MbrFat32: ValidateFat(plan, iface, m); break;
            case LayoutKind.Rdb: ValidateRdb(plan, iface, m); break;
            case LayoutKind.ImageRestore: break;
        }
        return m;
    }

    public static bool HasErrors(IEnumerable<PlanMessage> messages) => messages.Any(x => x.Severity == MessageSeverity.Error);

    private static void ValidateFat(LayoutPlan plan, StorageInterfaceProfile iface, List<PlanMessage> m)
    {
        try { Fat32Formatter.NormaliseLabel(plan.FatLabel); }
        catch (ArgumentException ex) { m.Add(PlanMessage.Error(ex.Message)); }
        if (iface.Defaults.FatLabel is string label && !label.Equals(plan.FatLabel, StringComparison.OrdinalIgnoreCase))
            m.Add(PlanMessage.Info($"The documented volume label for this slot is '{label}'."));

        long volBytes = (long)plan.FatSectorCount * plan.SectorSize;
        int cluster = plan.FatClusterBytes ?? Fat32Formatter.ChooseClusterBytes(volBytes);
        if (iface.Constraints.MaxFatClusterBytes is long maxCluster && cluster > maxCluster)
            m.Add(PlanMessage.Error($"Cluster size {Format.Bytes(cluster)} exceeds the {Format.Bytes(maxCluster)} maximum for this interface."));
        long clusters = volBytes / Math.Max(cluster, 1);
        if (clusters < Fat32Formatter.MinClusters)
            m.Add(PlanMessage.Error("The card is too small for FAT32 with this cluster size."));
        if ((long)plan.FatStartLba + plan.FatSectorCount > plan.DeviceBytes / plan.SectorSize)
            m.Add(PlanMessage.Error("The FAT32 partition extends beyond the end of the card."));
        if (plan.UnallocatedBytes > 1024L * 1024 * 1024)
            m.Add(PlanMessage.Info($"{Format.Bytes(plan.UnallocatedBytes)} beyond the 2 TiB MBR limit will be unused."));
    }

    private static void ValidateRdb(LayoutPlan plan, StorageInterfaceProfile iface, List<PlanMessage> m)
    {
        if (plan.Geometry is null) { m.Add(PlanMessage.Error("No disk geometry.")); return; }
        if (plan.SectorSize != 512) m.Add(PlanMessage.Error("RDB layouts are only generated for 512-byte sector cards."));
        if (plan.Partitions.Count == 0) { m.Add(PlanMessage.Error("Add at least one partition.")); return; }

        var mode = iface.CompatibilityModes.FirstOrDefault(x => x.Id == plan.CompatibilityModeId);
        var limits = iface.EffectiveLimits(plan.CompatibilityModeId);
        var available = new HashSet<DosType>(limits.RomFs);
        foreach (var fs in plan.FileSystems) available.Add(fs.DosType);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint expectedLow = plan.RdbCylinders;
        foreach (var p in plan.Partitions)
        {
            string label = $"{p.DeviceName} ({p.VolumeName})";
            if (!RdbWriter.IsValidDriveName(p.DeviceName)) m.Add(PlanMessage.Error($"'{p.DeviceName}' is not a valid device name (1-30 characters, no ':' or '/')."));
            else if (!names.Add(p.DeviceName)) m.Add(PlanMessage.Error($"Device name {p.DeviceName} is used twice."));
            try { AmigaFfsFormatter.ValidateVolumeName(p.VolumeName); }
            catch (ArgumentException ex) { m.Add(PlanMessage.Error($"{label}: {ex.Message}")); }
            if (!volumes.Add(p.VolumeName)) m.Add(PlanMessage.Warn($"Volume name '{p.VolumeName}' is used twice; AmigaDOS will confuse the volumes."));

            if (p.HighCyl < p.LowCyl) { m.Add(PlanMessage.Error($"{label} has no space left on the card.")); continue; }
            if (p.LowCyl < expectedLow) m.Add(PlanMessage.Error($"{label} overlaps the previous partition."));
            if (p.HighCyl >= plan.Geometry.Cylinders) m.Add(PlanMessage.Error($"{label} extends past the end of the card."));
            expectedLow = p.HighCyl + 1;

            long bytes = plan.PartitionBytes(p);
            long end = (p.HighCyl + 1L) * plan.CylinderBytes;
            if (limits.MaxEnd is long maxEnd && end > maxEnd)
                m.Add(PlanMessage.Error($"{label} ends at {Format.Bytes(end)}, beyond the {Format.Bytes(maxEnd)} this configuration can address ({mode?.Name ?? iface.Name})."));
            if (p.Bootable && limits.BootMaxEnd is long bootEnd && end > bootEnd)
                m.Add(PlanMessage.Error($"Boot partition {label} must end below {Format.Bytes(bootEnd)}."));
            if (p.DosType.IsDosFamily && limits.MaxFfs is long maxFfs && bytes > maxFfs)
            {
                var text = $"{label} is {Format.Bytes(bytes)}; {Format.Bytes(maxFfs)} is the largest FFS partition for this configuration.";
                m.Add(mode?.MaxFfsPartitionBytes is not null ? PlanMessage.Error(text) : PlanMessage.Warn(text + " (Conservative, unverified limit.)"));
            }
            if (bytes < 4L * 1024 * 1024) m.Add(PlanMessage.Error($"{label} is too small."));

            if (!available.Contains(p.DosType))
            {
                var text = $"{label} uses {p.DosType.FriendlyName}, which is not built into this system's ROM. Add the filesystem handler to the RDB (Advanced) or install it on the Amiga before mounting.";
                m.Add(p.Bootable ? PlanMessage.Error(text) : PlanMessage.Warn(text));
            }
            if (!p.WillBeFormatted)
                m.Add(PlanMessage.Info($"{label} will be created unformatted and will show as 'Uninitialized' on the Amiga until you format it there. This is expected."));
            if (p.MaxTransfer is 0 || p.MaxTransfer > 0x7FFFFFFF) m.Add(PlanMessage.Error($"{label} has an invalid MaxTransfer value."));
        }

        if (!plan.Partitions.Any(p => p.Bootable) && plan.Use == IntendedUse.BootableSystem)
            m.Add(PlanMessage.Error("A bootable system card needs one partition marked bootable."));
        var bootable = plan.Partitions.Where(p => p.Bootable).ToList();
        if (bootable.Count > 1 && bootable.Count(p => p.BootPriority == bootable.Max(x => x.BootPriority)) > 1)
            m.Add(PlanMessage.Warn("Several bootable partitions share the highest boot priority; the Amiga's choice between them is not defined."));

        long rdbBlocks = 1 + plan.Partitions.Count + plan.FileSystems.Sum(f => 1 + RdbWriter.LoadSegBlocksFor(f.Binary.Length));
        if (rdbBlocks > plan.RdbCylinders * (long)plan.Geometry.CylinderBlocks)
            m.Add(PlanMessage.Error("The reserved RDB area is too small; increase the reserved cylinders."));
        foreach (var fs in plan.FileSystems)
        {
            if (!HunkFile.IsHunkExecutable(fs.Binary)) m.Add(PlanMessage.Error($"{fs.SourceName} is not an AmigaDOS executable."));
            if (!plan.Partitions.Any(p => p.DosType == fs.DosType)) m.Add(PlanMessage.Info($"Filesystem {fs.DosType.FriendlyName} is embedded but not used by any partition."));
        }
        if (plan.UnallocatedBytes >= 64L * 1024 * 1024)
            m.Add(PlanMessage.Info($"{Format.Bytes(plan.UnallocatedBytes)} of the card is left unallocated."));
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} bytes" : $"{v:0.##} {units[u]}";
    }

    public static string Hex(uint v) => $"0x{v:X8}";
}
