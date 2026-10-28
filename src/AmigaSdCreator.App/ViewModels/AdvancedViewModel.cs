using System.IO;
using AmigaSdCreator.App.Services;
using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.App.ViewModels;

public sealed record FactRow(string Text, string SourceTitle, string SourceUrl, string? SourceNote);

/// <summary>Reference view of hardware profiles (with sources) and a raw RDB / partition inspector.</summary>
public sealed partial class AdvancedViewModel(MainViewModel main) : PageViewModel
{
    public override string Title => "Advanced";
    public MainViewModel Main => main;
    public DeviceList Devices => main.Devices;
    public HardwareSelection Hardware { get; } = new();

    public IReadOnlyList<FactRow> Facts => Hardware.SelectedInterface?.Facts.Select(f =>
    {
        var src = AppServices.Current.Profiles.Sources.GetValueOrDefault(f.Source);
        return new FactRow(f.Text, src?.Title ?? f.Source, src?.Url ?? "", src?.Note);
    }).ToList() ?? [];

    public string ProfileFolder => AppServices.ProfileDirectory;

    [ObservableProperty] public partial bool UseImageFile { get; set; }
    [ObservableProperty] public partial DeviceItem? SelectedDevice { get; set; }
    [ObservableProperty] public partial string? ImagePath { get; set; }
    [ObservableProperty] public partial string? RawReport { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    public override void OnNavigatedTo()
    {
        Hardware.PropertyChanged -= HardwareChanged;
        Hardware.PropertyChanged += HardwareChanged;
        OnPropertyChanged(nameof(Facts));
    }

    private void HardwareChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Facts));

    [RelayCommand]
    private void BrowseImage()
    {
        var p = Dialogs.OpenFile(Dialogs.ImageFilter, "Choose an image to inspect");
        if (p is null) return;
        ImagePath = p;
        UseImageFile = true;
    }

    [RelayCommand]
    private async Task InspectAsync()
    {
        Error = null;
        RawReport = null;
        if (!UseImageFile && !main.IsElevated) { Error = "Reading a card directly requires administrator rights."; return; }
        if (UseImageFile ? string.IsNullOrEmpty(ImagePath) : SelectedDevice is null) { Error = "Choose a card or image."; return; }
        var disk = SelectedDevice?.Disk;
        var image = ImagePath;
        bool useImage = UseImageFile;
        try
        {
            RawReport = await Task.Run(() =>
            {
                using IBlockDevice dev = useImage ? FileBlockDevice.OpenRead(image!) : AppServices.Current.Disks.OpenRead(disk!);
                var report = DiagnosticEngine.Analyse(DiskInspector.Inspect(dev));
                return ReportExporter.ToMarkdown(report);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenProfileFolder()
    {
        Directory.CreateDirectory(AppServices.ProfileDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppServices.ProfileDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    public static string LimitText(long? bytes) => bytes is long b ? Format.Bytes(b) : "no limit";
}
