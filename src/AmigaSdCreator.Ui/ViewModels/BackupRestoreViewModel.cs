using System.IO;
using AmigaSdCreator.Ui.Services;
using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.Imaging;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Safety;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.Ui.ViewModels;

public sealed partial class BackupRestoreViewModel(MainViewModel main) : PageViewModel
{
    private CancellationTokenSource? _cts;
    private DeviceIdentity? _targetIdentity;

    public override string Title => "Backup & Restore";
    public MainViewModel Main => main;
    public DeviceList Devices => main.Devices;

    // Backup
    [ObservableProperty] public partial DeviceItem? SelectedSource { get; set; }
    [ObservableProperty] public partial bool Compress { get; set; } = true;

    // Restore
    [ObservableProperty] public partial string? RestoreImagePath { get; set; }
    [ObservableProperty] public partial ImageInfo? RestoreImage { get; set; }
    [ObservableProperty] public partial string? RestoreImageContents { get; set; }
    [ObservableProperty] public partial DeviceItem? SelectedTarget { get; set; }
    [ObservableProperty] public partial string ConfirmText { get; set; } = "";

    // Progress
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string? Stage { get; set; }
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial string? ProgressDetail { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool? LastSucceeded { get; set; }

    public bool NeedsElevation => !main.IsElevated;

    /// <summary>Pre-selects the home-screen card as the backup source. The restore target is always chosen explicitly.</summary>
    public override void OnNavigatedTo()
    {
        if (SelectedSource is null && main.CurrentCard is { } card) SelectedSource = card;
    }

    partial void OnSelectedTargetChanged(DeviceItem? value)
    {
        _targetIdentity = value?.Disk.Identity;
        OnPropertyChanged(nameof(RestoreSummary));
        OnPropertyChanged(nameof(CanRestore));
    }

    partial void OnConfirmTextChanged(string value) => OnPropertyChanged(nameof(CanRestore));
    partial void OnRestoreImageChanged(ImageInfo? value)
    {
        OnPropertyChanged(nameof(RestoreSummary));
        OnPropertyChanged(nameof(CanRestore));
    }

    public string? RestoreSummary =>
        RestoreImage is null || SelectedTarget is null ? null :
        $"Write {Format.Bytes(RestoreImage.SizeBytes)} from {Path.GetFileName(RestoreImage.Path)} to {SelectedTarget.Title} ({SelectedTarget.Disk.LocationText}, {Format.Bytes(SelectedTarget.Disk.SizeBytes)}). " +
        (RestoreImage.SizeBytes > SelectedTarget.Disk.SizeBytes ? "The image is LARGER than this card and cannot be written." : "Everything currently on the card will be replaced.");

    public bool CanRestore =>
        !IsRunning && RestoreImage is { ManifestMatches: true } && SelectedTarget is { IsWritable: true } && !NeedsElevation
        && RestoreImage.SizeBytes <= SelectedTarget.Disk.SizeBytes
        && string.Equals(ConfirmText.Trim(), SafetyPolicy.ConfirmationWord, StringComparison.Ordinal);

    partial void OnIsRunningChanged(bool value)
    {
        main.IsBusy = value;
        OnPropertyChanged(nameof(CanRestore));
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        Message = null;
        if (SelectedSource is null) { Message = "Choose the card to back up."; return; }
        if (NeedsElevation) { Message = $"Reading a card requires {AppServices.Platform.AdminRightsText}. Use '{AppServices.Platform.RestartElevatedText}'."; return; }
        string ext = Compress ? ".img.gz" : ".img";
        var path = await Dialogs.SaveFile(Compress ? "Compressed image (*.img.gz)|*.img.gz" : "Raw image (*.img)|*.img", "Save backup image",
            $"amiga-sd-{DateTime.Now:yyyyMMdd-HHmm}{ext}");
        if (path is null) return;

        var disk = SelectedSource.Disk;
        await RunAsync("Backup", disk.LogId, async (progress, ct) =>
        {
            var manifest = await AppServices.Current.Operations.BackupAsync(disk, path, progress, ct);
            AppServices.Platform.ClaimFile(path);
            AppServices.Platform.ClaimFile(DiskImager.ManifestPath(path));
            return $"Backup complete and verified: {Format.Bytes(manifest.SizeBytes)}, SHA-256 {manifest.Sha256[..16]}... A manifest was saved next to the image.";
        });
    }

    [RelayCommand]
    private async Task ChooseRestoreImageAsync()
    {
        var path = await Dialogs.OpenFile(Dialogs.ImageFilter, "Choose an image to restore");
        if (path is null) return;
        RestoreImagePath = path;
        RestoreImage = null;
        RestoreImageContents = null;
        await RunAsync("Check image", "image file", async (progress, ct) =>
        {
            var info = await DiskImager.InspectImageAsync(path, progress, ct);
            RestoreImage = info;
            RestoreImageContents = await Task.Run(() => DescribeImage(info), ct);
            if (!info.ManifestMatches)
                throw new ImagingException("The image does not match its manifest (size or checksum differ). It may be damaged and will not be written.");
            return info.Manifest is null
                ? $"Image read successfully ({Format.Bytes(info.SizeBytes)}). No manifest was found, so its integrity cannot be confirmed against the original."
                : $"Image integrity confirmed against its manifest ({Format.Bytes(info.SizeBytes)}).";
        });
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!CanRestore || _targetIdentity is null || RestoreImage is null) return;
        var identity = _targetIdentity;
        var image = RestoreImage;
        var confirm = ConfirmText;
        await RunAsync("Restore", SelectedTarget!.Disk.LogId, async (progress, ct) =>
        {
            await AppServices.Current.Operations.RestoreAsync(identity, confirm, image, progress, ct);
            return "The image was written and verified by reading it back from the card.";
        });
        ConfirmText = "";
        await main.Devices.RefreshAsync();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(string kind, string target, Func<IProgress<ImagingProgress>, CancellationToken, Task<string>> work)
    {
        IsRunning = true;
        LastSucceeded = null;
        Message = null;
        ProgressPercent = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<ImagingProgress>(p =>
        {
            Stage = p.Stage;
            ProgressPercent = p.Fraction * 100;
            ProgressDetail = $"{Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}  •  {Format.Bytes((long)p.BytesPerSecond)}/s";
        });
        try
        {
            Message = await Task.Run(() => work(progress, _cts.Token));
            LastSucceeded = true;
            ProgressPercent = 100;
            AppServices.Current.AddRecent(kind, target, true, Message);
        }
        catch (OperationCanceledException)
        {
            Message = kind == "Restore"
                ? "Cancelled before writing started. The card was not changed."
                : "Cancelled. No backup file was kept.";
            LastSucceeded = false;
        }
        catch (ImagingException ex)
        {
            Message = ex.Message;
            LastSucceeded = false;
            AppServices.Current.AddRecent(kind, target, false, ex.Message);
        }
        catch (SafetyCheckFailedException ex)
        {
            Message = "Nothing was written. " + string.Join(" ", ex.Checks.Where(c => !c.Passed).Select(c => c.Detail));
            LastSucceeded = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"{kind} failed: {ex.Message}";
            LastSucceeded = false;
            AppServices.Current.Log.Error($"{kind} failed", ex);
            AppServices.Current.AddRecent(kind, target, false, ex.Message);
        }
        finally
        {
            IsRunning = false;
            Stage = null;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static string DescribeImage(ImageInfo info)
    {
        if (info.Compressed) return "Compressed image: its layout is shown after it has been written.";
        using var dev = FileBlockDevice.OpenRead(info.Path);
        var d = DiskInspector.Inspect(dev);
        var parts = d.Partitions.Select(p => $"{p.Name} {Format.Bytes(p.LengthBytes)} {p.Filesystem.DisplayName}");
        return $"{d.SchemeDescription}: {string.Join("; ", parts)}";
    }
}
