using System.IO;
using AmigaSdCreator.App.Services;
using AmigaSdCreator.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private bool _loading = true;

    public SettingsViewModel()
    {
        var s = AppServices.Current.Settings;
        Theme = s.Theme;
        DefaultProfile = AppServices.Current.Profiles.Find(s.DefaultProfileId);
        FileLogging = s.FileLogging;
        AllowNonRemovableBus = s.AllowNonRemovableBus;
        AllowLargeDisks = s.AllowLargeDisks;
        _loading = false;
    }

    public override string Title => "Settings";
    public IReadOnlyList<string> Themes { get; } = ["Dark", "Light"];
    public IReadOnlyList<HardwareProfile> Profiles => AppServices.Current.Profiles.Profiles;
    public string LogFolder => AppServices.LogDirectory;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    [ObservableProperty] public partial string Theme { get; set; }
    [ObservableProperty] public partial HardwareProfile? DefaultProfile { get; set; }
    [ObservableProperty] public partial bool FileLogging { get; set; }
    [ObservableProperty] public partial bool AllowNonRemovableBus { get; set; }
    [ObservableProperty] public partial bool AllowLargeDisks { get; set; }

    partial void OnThemeChanged(string value)
    {
        if (_loading) return;
        AppServices.ApplyTheme(value);
        AppServices.Current.UpdateSettings(s => s with { Theme = value });
    }

    partial void OnDefaultProfileChanged(HardwareProfile? value)
    {
        if (!_loading && value is not null) AppServices.Current.UpdateSettings(s => s with { DefaultProfileId = value.Id });
    }

    partial void OnFileLoggingChanged(bool value)
    {
        if (!_loading) AppServices.Current.UpdateSettings(s => s with { FileLogging = value });
    }

    partial void OnAllowNonRemovableBusChanged(bool value)
    {
        if (_loading) return;
        if (value && !Dialogs.Confirm("Allow internal SATA/NVMe/SCSI disks as targets?\n\nOnly enable this for a dedicated card reader that Windows reports incorrectly. Windows system disks always remain protected."))
        {
            _loading = true; AllowNonRemovableBus = false; _loading = false;
            return;
        }
        AppServices.Current.UpdateSettings(s => s with { AllowNonRemovableBus = value });
        _ = MainViewModel.Instance.Devices.RefreshAsync();
    }

    partial void OnAllowLargeDisksChanged(bool value)
    {
        if (_loading) return;
        AppServices.Current.UpdateSettings(s => s with { AllowLargeDisks = value });
        _ = MainViewModel.Instance.Devices.RefreshAsync();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(LogFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogFolder) { UseShellExecute = true });
    }
}
