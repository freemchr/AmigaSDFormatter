using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Layout;

/// <summary>
/// Writes a validated <see cref="LayoutPlan"/> to a block device (an image file or, through the
/// safety layer, a physical card). Callers must have run <see cref="LayoutValidator"/> first;
/// this class re-validates and refuses plans with errors.
/// </summary>
public static class DiskImageBuilder
{
    private const long ClearBytes = 1024 * 1024;

    public static void Apply(IBlockDevice device, LayoutPlan plan, StorageInterfaceProfile iface, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!device.CanWrite) throw new InvalidOperationException("Target device is read-only.");
        if (device.Length < plan.DeviceBytes) throw new InvalidOperationException("Target device is smaller than the plan was made for.");
        if (device.SectorSize != plan.SectorSize) throw new InvalidOperationException("Target sector size does not match the plan.");
        var messages = LayoutValidator.Validate(plan, iface);
        if (LayoutValidator.HasErrors(messages))
            throw new InvalidOperationException("The layout has errors: " + string.Join(" ", messages.Where(m => m.Severity == MessageSeverity.Error).Select(m => m.Text)));

        // Remove stale partition tables: a leftover RDSK in blocks 1-15 or a backup GPT header at the
        // end of the card can make Windows or the Amiga interpret the card differently from the new layout.
        progress?.Report("Clearing old partition tables");
        device.Zero(0, Math.Min(ClearBytes, device.Length), cancellationToken);
        long tail = Math.Min(ClearBytes, device.Length);
        device.Zero(device.Length - tail, tail, cancellationToken);

        switch (plan.Kind)
        {
            case LayoutKind.MbrFat32:
                ApplyFat(device, plan, progress, cancellationToken);
                break;
            case LayoutKind.Rdb:
                ApplyRdb(device, plan, progress, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Image-restore plans are applied with DiskImager, not DiskImageBuilder.");
        }
        device.Flush();
        progress?.Report("Layout written");
    }

    private static void ApplyFat(IBlockDevice device, LayoutPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Writing MBR partition table");
        var mbr = new MbrPartitionTable
        {
            DiskSignature = (uint)Random.Shared.NextInt64(1, uint.MaxValue),
            Entries = [new MbrPartitionEntry(1, 0x00, MbrPartitionTypes.Fat32Lba, plan.FatStartLba, plan.FatSectorCount)],
        };
        device.WriteSector(0, mbr.ToSector());

        progress?.Report($"Creating FAT32 volume '{plan.FatLabel}'");
        using var volume = new SubBlockDevice(device, (long)plan.FatStartLba * plan.SectorSize, (long)plan.FatSectorCount * plan.SectorSize);
        Fat32Formatter.Format(volume, new Fat32FormatOptions
        {
            Label = plan.FatLabel,
            ClusterBytes = plan.FatClusterBytes,
            HiddenSectors = plan.FatStartLba,
        }, ct);
    }

    private static void ApplyRdb(IBlockDevice device, LayoutPlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Writing Rigid Disk Block");
        RdbWriter.Write(device, plan.ToRdbSpec());

        foreach (var p in plan.Partitions)
        {
            ct.ThrowIfCancellationRequested();
            using var volume = new SubBlockDevice(device, plan.PartitionStartBytes(p), plan.PartitionBytes(p));
            if (p.WillBeFormatted)
            {
                progress?.Report($"Formatting {p.DeviceName}: as '{p.VolumeName}' ({p.DosType.FriendlyName})");
                AmigaFfsFormatter.Format(volume, p.DosType, p.VolumeName, cancellationToken: ct);
            }
            else
            {
                // Leave a clean, unformatted partition for the Amiga's Format command.
                progress?.Report($"Preparing {p.DeviceName}: for formatting on the Amiga");
                volume.Zero(0, Math.Min(64 * 1024, volume.Length), ct);
            }
        }
    }
}
