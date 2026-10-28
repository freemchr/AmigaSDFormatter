namespace AmigaSdCreator.Core.Safety;

public sealed record SafetyCheck(string Name, bool Passed, string Detail);

public sealed record SafetyPolicy
{
    /// <summary>Only USB/SD/MMC card-reader disks may be written unless this is enabled (Advanced).</summary>
    public bool AllowNonRemovableBus { get; init; }
    /// <summary>Cards above this size need <see cref="AllowLargeDisks"/> (protects external hard drives).</summary>
    public long LargeDiskThresholdBytes { get; init; } = 512L * 1024 * 1024 * 1024;
    public bool AllowLargeDisks { get; init; }
    public const string ConfirmationWord = "ERASE";
}

/// <summary>
/// The single gate every destructive operation passes through. All checks must pass; system and
/// boot disks can never be written, regardless of policy.
/// </summary>
public static class WriteGuard
{
    public static IReadOnlyList<SafetyCheck> Evaluate(DiskDescriptor? current, DeviceIdentity selected, long requiredBytes, string typedConfirmation, SafetyPolicy policy)
    {
        var c = new List<SafetyCheck>();
        if (current is null)
        {
            c.Add(new SafetyCheck("Device present", false, "The selected card is no longer connected. Nothing will be written."));
            return c;
        }

        bool same = current.DevicePath == selected.DevicePath
            && current.Number == selected.Number
            && current.SizeBytes == selected.SizeBytes
            && current.LogicalSectorSize == selected.SectorSize
            && string.Equals(current.Model.Trim(), selected.Model, StringComparison.Ordinal)
            && string.Equals(current.SerialNumber.Trim(), selected.SerialNumber, StringComparison.Ordinal)
            && string.Equals(current.UniqueId, selected.UniqueId, StringComparison.Ordinal);
        c.Add(new SafetyCheck("Same device as selected", same,
            same ? "Path, model, serial and size match the selection." : "The device at this position is not the card you selected (it may have been swapped). Select the card again."));

        bool system = current.IsSystem || current.IsBoot || current.HoldsPageFile;
        c.Add(new SafetyCheck("Not a Windows system disk", !system, system ? "This disk holds Windows, its boot files or the page file. It can never be written." : "Not a system, boot or page-file disk."));

        bool busOk = current.IsCardReaderBus || policy.AllowNonRemovableBus;
        c.Add(new SafetyCheck("Card reader / USB device", busOk,
            busOk ? $"Connected via {current.Bus}." : $"Connected via {current.Bus}. Only USB and SD card readers are allowed (see Advanced settings)."));

        bool sizeOk = current.SizeBytes <= policy.LargeDiskThresholdBytes || policy.AllowLargeDisks;
        c.Add(new SafetyCheck("Plausible card size", sizeOk,
            sizeOk ? $"{current.SizeBytes:N0} bytes." : "Larger than 512 GB - this looks like a hard drive, not an SD card. Enable large disks in Advanced settings to continue."));

        c.Add(new SafetyCheck("Media present and writable", current.HasMedia && !current.IsReadOnly && !current.IsOffline,
            !current.HasMedia ? "No card in the reader." : current.IsReadOnly ? "The card is write-protected (check the lock switch)." : current.IsOffline ? "The disk is offline." : "Writable."));

        c.Add(new SafetyCheck("Enough capacity", current.SizeBytes >= requiredBytes,
            current.SizeBytes >= requiredBytes ? "The card is large enough." : $"The card holds {current.SizeBytes:N0} bytes but {requiredBytes:N0} are needed."));

        bool confirmed = string.Equals(typedConfirmation?.Trim(), SafetyPolicy.ConfirmationWord, StringComparison.Ordinal);
        c.Add(new SafetyCheck("Erase confirmed", confirmed, confirmed ? "Confirmed by the user." : $"Type {SafetyPolicy.ConfirmationWord} to confirm."));
        return c;
    }

    /// <summary>
    /// Checks for non-destructive filesystem edits (copying files onto an Amiga partition): the same
    /// identity, system-disk, bus and write-protect rules, without the erase confirmation.
    /// </summary>
    public static IReadOnlyList<SafetyCheck> EvaluateFileAccess(DiskDescriptor? current, DeviceIdentity selected, SafetyPolicy policy) =>
        Evaluate(current, selected, 0, SafetyPolicy.ConfirmationWord, policy).Where(c => c.Name != "Erase confirmed").ToList();

    public static bool AllPassed(IEnumerable<SafetyCheck> checks) => checks.All(x => x.Passed);

    /// <summary>Why a disk can never be offered as a target, or null if it can be.</summary>
    public static string? ProtectionReason(DiskDescriptor d, SafetyPolicy policy)
    {
        if (d.IsSystem || d.IsBoot || d.HoldsPageFile) return "Windows system disk";
        if (!d.IsCardReaderBus && !policy.AllowNonRemovableBus) return $"Internal {d.Bus} disk";
        if (d.SizeBytes > policy.LargeDiskThresholdBytes && !policy.AllowLargeDisks) return "Larger than 512 GB";
        if (!d.HasMedia) return "No card inserted";
        return null;
    }
}
