using System.IO;
using AmigaSdCreator.App.Services;
using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.App.ViewModels;

/// <summary>Read-only analysis of a card or image file. Never opens anything for writing.</summary>
public sealed partial class DiagnoseViewModel(MainViewModel main) : PageViewModel
{
    public override string Title => "Diagnose Card";
    public MainViewModel Main => main;
    public DeviceList Devices => main.Devices;
    public HardwareSelection Hardware { get; } = new();

    [ObservableProperty] public partial bool UseImageFile { get; set; }
    [ObservableProperty] public partial DeviceItem? SelectedDevice { get; set; }
    [ObservableProperty] public partial string? ImagePath { get; set; }
    [ObservableProperty] public partial DiagnosticReport? Report { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }

    public bool NeedsElevation => !UseImageFile && !main.IsElevated;

    public override void OnNavigatedTo()
    {
        if (SelectedDevice is null && !UseImageFile && main.CurrentCard is { } card) SelectedDevice = card;
    }

    partial void OnUseImageFileChanged(bool value) => OnPropertyChanged(nameof(NeedsElevation));

    public void UseCard(DeviceItem? item)
    {
        UseImageFile = false;
        SelectedDevice = item;
        Report = null;
    }

    [RelayCommand]
    private void BrowseImage()
    {
        var path = Dialogs.OpenFile(Dialogs.ImageFilter, "Choose a disk image to diagnose");
        if (path is null) return;
        ImagePath = path;
        UseImageFile = true;
    }

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        Error = null;
        Report = null;
        if (UseImageFile && string.IsNullOrEmpty(ImagePath)) { Error = "Choose an image file first."; return; }
        if (!UseImageFile && SelectedDevice is null) { Error = "Choose a card first."; return; }
        if (NeedsElevation) { Error = "Reading a card directly requires administrator rights. Use 'Restart as administrator' at the bottom left."; return; }

        IsRunning = true;
        try
        {
            var profile = Hardware.SelectedProfile;
            var iface = Hardware.SelectedInterface;
            var use = Hardware.SelectedUse;
            string name = UseImageFile ? Path.GetFileName(ImagePath!) : $"{SelectedDevice!.Title} ({Format(SelectedDevice.Disk.SizeBytes)})";
            var disk = SelectedDevice?.Disk;
            string? image = ImagePath;
            bool useImage = UseImageFile;

            Report = await Task.Run(() =>
            {
                using IBlockDevice dev = useImage ? FileBlockDevice.OpenRead(image!) : AppServices.Current.Disks.OpenRead(disk!);
                var inspection = DiskInspector.Inspect(dev);
                return DiagnosticEngine.Analyse(inspection, profile, iface, use, name);
            });
            AppServices.Current.AddRecent("Diagnose", useImage ? "image file" : SelectedDevice!.Disk.LogId, true, ReportExporter.VerdictText(Report.Verdict));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"The card could not be read: {ex.Message}";
            AppServices.Current.Log.Error("Diagnosis failed", ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Export()
    {
        if (Report is null) return;
        var path = Dialogs.SaveFile("Markdown report (*.md)|*.md|JSON report (*.json)|*.json", "Export diagnostic report", $"amiga-sd-report-{DateTime.Now:yyyyMMdd-HHmm}.md");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ReportExporter.ToJson(Report) : ReportExporter.ToMarkdown(Report));
        }
        catch (IOException ex) { Dialogs.Error($"The report could not be saved: {ex.Message}"); }
    }

    [RelayCommand]
    private void ActOnRecommendation(Recommendation? r)
    {
        if (r is null) return;
        var card = UseImageFile ? null : SelectedDevice;
        switch (r.Action)
        {
            case RecommendedAction.PrepareDataCard:
                main.Prepare.Start(DataInterfaceFor(), Core.Profiles.IntendedUse.DataExchange, card);
                main.CurrentPage = main.Prepare;
                break;
            case RecommendedAction.PrepareHardDriveCard:
                main.Prepare.Start(HardDriveInterfaceFor(), Hardware.SelectedUse == Core.Profiles.IntendedUse.BootableSystem ? Core.Profiles.IntendedUse.BootableSystem : Core.Profiles.IntendedUse.HardDrive, card);
                main.CurrentPage = main.Prepare;
                break;
            case RecommendedAction.BackupFirst:
            case RecommendedAction.RestoreAuthorisedImage:
                main.Backup.SelectedSource = card;
                main.CurrentPage = main.Backup;
                break;
        }
    }

    private string DataInterfaceFor() =>
        Hardware.SelectedProfile?.Interfaces.FirstOrDefault(i => i.Layouts.Contains(Core.Profiles.LayoutKind.MbrFat32))?.Id ?? "a6000-sd-data";

    private string HardDriveInterfaceFor() =>
        Hardware.SelectedInterface is { } i && i.Layouts.Contains(Core.Profiles.LayoutKind.Rdb) ? i.Id
        : Hardware.SelectedProfile?.Interfaces.FirstOrDefault(x => x.Layouts.Contains(Core.Profiles.LayoutKind.Rdb))?.Id ?? "a6000-sd-hdd";

    private static string Format(long b) => Core.Layout.Format.Bytes(b);
}
