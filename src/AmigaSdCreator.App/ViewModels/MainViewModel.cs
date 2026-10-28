using System.Windows;
using AmigaSdCreator.App.Services;
using AmigaSdCreator.Core.Profiles;
using AmigaSdCreator.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public static MainViewModel Instance { get; private set; } = null!;

    public DeviceList Devices { get; } = new();
    public PrepareViewModel Prepare { get; }
    public DiagnoseViewModel Diagnose { get; }
    public BackupRestoreViewModel Backup { get; }
    public FilesViewModel Files { get; }
    public AdvancedViewModel Advanced { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] public partial PageViewModel CurrentPage { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool IsElevated { get; } = AppServices.Current.Disks.IsElevated;
    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public MainViewModel()
    {
        Instance = this;
        Prepare = new PrepareViewModel(this);
        Diagnose = new DiagnoseViewModel(this);
        Backup = new BackupRestoreViewModel(this);
        Files = new FilesViewModel(this);
        Advanced = new AdvancedViewModel(this);
        Settings = new SettingsViewModel();
        CurrentPage = Prepare;
        _ = Devices.RefreshAsync();
    }

    public bool IsHome => CurrentPage == Prepare;

    /// <summary>The card chosen on the home screen, if it is still connected; other pages start with it.</summary>
    public DeviceItem? CurrentCard => Prepare.SelectedDevice is { } d && Devices.Cards.Contains(d) ? d : null;

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        OnPropertyChanged(nameof(IsHome));
        value.OnNavigatedTo();
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "Diagnose" => Diagnose,
            "Backup" => Backup,
            "Files" => Files,
            "Advanced" => Advanced,
            "Settings" => Settings,
            _ => Prepare,
        };
        if (CurrentPage == Prepare) Prepare.BackToHomeCommand.Execute(null);
    }

    [RelayCommand]
    private void RestartElevated()
    {
        if (BlockIfBusy()) return;
        if (Elevation.TryRestartElevated()) Application.Current.Shutdown();
    }

    /// <summary>Prevents navigation-triggered restarts while a disk operation runs.</summary>
    public bool BlockIfBusy()
    {
        if (!IsBusy) return false;
        Dialogs.Info("An operation is in progress. Wait for it to finish first.");
        return true;
    }

    public static string StatusText(VerificationStatus s) => s switch
    {
        VerificationStatus.Verified or VerificationStatus.Partial or VerificationStatus.Experimental => "",
        VerificationStatus.Planned => "Planned",
        VerificationStatus.Protected => "System drive",
        _ => s.ToString(),
    };
}
