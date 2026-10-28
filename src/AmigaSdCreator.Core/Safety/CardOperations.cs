using AmigaSdCreator.Core.Imaging;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Safety;

public sealed class SafetyCheckFailedException(IReadOnlyList<SafetyCheck> checks)
    : Exception("Safety checks failed: " + string.Join(" ", checks.Where(c => !c.Passed).Select(c => c.Detail)))
{
    public IReadOnlyList<SafetyCheck> Checks { get; } = checks;
}

/// <summary>
/// Orchestrates operations on physical cards. Every destructive method re-enumerates disks,
/// re-runs <see cref="WriteGuard"/> immediately before opening the device, and never falls back
/// to another disk: if the selected device is gone, the operation fails.
/// </summary>
public sealed class CardOperations(IDiskService disks, SafetyPolicy policy)
{
    public SafetyPolicy Policy { get; } = policy;

    public async Task<IReadOnlyList<SafetyCheck>> PreflightAsync(DeviceIdentity selected, long requiredBytes, string confirmation, CancellationToken ct = default)
    {
        var current = (await disks.EnumerateAsync(ct)).FirstOrDefault(d => d.DevicePath == selected.DevicePath);
        return WriteGuard.Evaluate(current, selected, requiredBytes, confirmation, Policy);
    }

    public async Task<VerificationResult> PrepareAsync(DeviceIdentity selected, string confirmation, LayoutPlan plan, StorageInterfaceProfile iface, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var messages = LayoutValidator.Validate(plan, iface);
        if (LayoutValidator.HasErrors(messages))
            throw new InvalidOperationException("The layout has errors and cannot be written.");
        await EnsureSafeAsync(selected, plan.DeviceBytes, confirmation, ct);

        progress?.Report("Locking and dismounting the card's volumes");
        using var handle = disks.OpenForWrite(selected);
        DiskImageBuilder.Apply(handle.Device, plan, iface, progress, ct);
        progress?.Report("Verifying");
        var result = LayoutVerifier.Verify(handle.Device, plan);
        handle.RefreshPartitionTable();
        return result;
    }

    public async Task RestoreAsync(DeviceIdentity selected, string confirmation, ImageInfo image, IProgress<ImagingProgress>? progress = null, CancellationToken ct = default)
    {
        await EnsureSafeAsync(selected, image.SizeBytes, confirmation, ct);
        using var handle = disks.OpenForWrite(selected);
        try
        {
            await DiskImager.RestoreAsync(image, handle.Device, progress, ct);
        }
        finally
        {
            handle.RefreshPartitionTable();
        }
    }

    /// <summary>Opens a card for editing files on its Amiga partitions, after the file-access safety checks.</summary>
    public async Task<IExclusiveDiskHandle> OpenForFileAccessAsync(DeviceIdentity selected, CancellationToken ct = default)
    {
        var current = (await disks.EnumerateAsync(ct)).FirstOrDefault(d => d.DevicePath == selected.DevicePath);
        var checks = WriteGuard.EvaluateFileAccess(current, selected, Policy);
        if (!WriteGuard.AllPassed(checks)) throw new SafetyCheckFailedException(checks);
        return disks.OpenForWrite(selected);
    }

    /// <summary>Read-only: no lock, no dismount, no guard needed beyond read access.</summary>
    public async Task<ImageManifest> BackupAsync(DiskDescriptor disk, string imagePath, IProgress<ImagingProgress>? progress = null, CancellationToken ct = default)
    {
        using var device = disks.OpenRead(disk);
        return await DiskImager.BackupAsync(device, imagePath, progress, ct);
    }

    private async Task EnsureSafeAsync(DeviceIdentity selected, long required, string confirmation, CancellationToken ct)
    {
        var checks = await PreflightAsync(selected, required, confirmation, ct);
        if (!WriteGuard.AllPassed(checks)) throw new SafetyCheckFailedException(checks);
    }
}
