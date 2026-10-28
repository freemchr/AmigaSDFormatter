using System.Collections.ObjectModel;
using System.Windows;
using AmigaSdCreator.App.Services;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;
using AmigaSdCreator.Core.Safety;
using AmigaSdCreator.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual void OnNavigatedTo() { }
}

/// <summary>
/// A disk shown in device lists. Protected disks are listed but cannot be selected for writing.
/// The instance is kept across refreshes while the disk's identity is unchanged, so selections survive.
/// </summary>
public sealed partial class DeviceItem(DiskDescriptor disk, string? protection) : ObservableObject
{
    public DiskDescriptor Disk { get; private set; } = disk;
    public string? ProtectionReason { get; private set; } = protection;

    public string Title => Disk.DisplayName;
    public string Detail
    {
        get
        {
            var parts = new List<string> { Format.Bytes(Disk.SizeBytes), $"Disk {Disk.Number}", Disk.Bus.ToString(), Disk.PartitionStyle };
            if (Disk.LogicalSectorSize != 512) parts.Add($"{Disk.LogicalSectorSize}-byte sectors");
            if (Disk.DriveLetters.Count > 0) parts.Add(string.Join(" ", Disk.DriveLetters.Select(l => l + ":")));
            return string.Join("  \u2022  ", parts);
        }
    }
    public bool IsWritable => ProtectionReason is null;
    public bool IsSystem => Disk.IsSystem || Disk.IsBoot || Disk.HoldsPageFile;

    /// <summary>Refreshes details (drive letters, partition style) of the same physical card.</summary>
    internal void Update(DiskDescriptor disk, string? protection)
    {
        string before = Detail + Title;
        var oldProtection = ProtectionReason;
        Disk = disk;
        if (before == Detail + Title && oldProtection == protection) return;
        ProtectionReason = protection;
        OnPropertyChanged(string.Empty);
    }

    public override string ToString() => $"{Title} ({Format.Bytes(Disk.SizeBytes)})";
}

/// <summary>Shared, auto-refreshing list of physical disks.</summary>
public sealed partial class DeviceList : ObservableObject, IDisposable
{
    private readonly DeviceChangeWatcher _watcher = new();
    private readonly System.Windows.Threading.DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _loadedOnce;

    public ObservableCollection<DeviceItem> All { get; } = [];
    public ObservableCollection<DeviceItem> Cards { get; } = [];

    [ObservableProperty] public partial bool IsRefreshing { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial DateTime? LastRefresh { get; set; }

    /// <summary>Set when a card appears after start-up, e.g. "New card: Generic SD (29.7 GiB)".</summary>
    [ObservableProperty] public partial string? NewCardMessage { get; set; }

    public event EventHandler? Refreshed;

    public DeviceList()
    {
        _watcher.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(() => RefreshCommand.Execute(null));
        // Inserting a card into a reader that is already connected often raises no device event, so also
        // poll quietly. Polling is skipped while a disk operation runs.
        _poll.Tick += async (_, _) =>
        {
            if (MainViewModel.Instance is { IsBusy: true }) return;
            await RefreshAsync();
        };
        _poll.Start();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var policy = AppServices.Current.Policy;
            var disks = await AppServices.Current.Disks.EnumerateAsync();
            var added = new List<DeviceItem>();

            // Merge rather than rebuild: an item whose identity (path, model, serial, size) is unchanged
            // keeps its instance, so any selection bound to it survives the refresh.
            var fresh = disks.Select(d =>
            {
                var existing = All.FirstOrDefault(x => x.Disk.Identity == d.Identity);
                if (existing is not null) existing.Update(d, WriteGuard.ProtectionReason(d, policy));
                else added.Add(existing = new DeviceItem(d, WriteGuard.ProtectionReason(d, policy)));
                return existing;
            }).ToList();
            Sync(All, fresh);
            // Empty reader slots are not offered as targets (they are still listed in All).
            Sync(Cards, fresh.Where(i => !i.IsSystem && i.Disk.HasMedia && (i.Disk.IsCardReaderBus || policy.AllowNonRemovableBus)).ToList());

            var newCard = added.FirstOrDefault(i => Cards.Contains(i) && i.Disk.HasMedia);
            if (_loadedOnce && newCard is not null) NewCardMessage = $"New card detected: {newCard.Title} ({Format.Bytes(newCard.Disk.SizeBytes)}). Select it above.";
            else if (Cards.All(c => !c.Disk.HasMedia)) NewCardMessage = null;
            _loadedOnce = true;
            Error = null;
            LastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            Error = $"Could not list disks: {ex.Message}";
            AppServices.Current.Log.Error("Disk enumeration failed", ex);
        }
        finally
        {
            IsRefreshing = false;
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Sync(ObservableCollection<DeviceItem> target, IReadOnlyList<DeviceItem> wanted)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = target.IndexOf(wanted[i]);
            if (at < 0) target.Insert(i, wanted[i]);
            else if (at != i) target.Move(at, i);
        }
    }

    public DeviceItem? Find(string devicePath) => All.FirstOrDefault(d => d.Disk.DevicePath == devicePath);

    public void Dispose()
    {
        _poll.Stop();
        _watcher.Dispose();
    }
}

/// <summary>Model / connection / use selection shared by the wizard and diagnostics.</summary>
public sealed partial class HardwareSelection : ObservableObject
{
    public HardwareSelection()
    {
        Profiles = AppServices.Current.Profiles.Profiles;
        SelectedProfile = AppServices.Current.Profiles.Find(AppServices.Current.Settings.DefaultProfileId) ?? Profiles.FirstOrDefault();
    }

    public IReadOnlyList<HardwareProfile> Profiles { get; }

    [ObservableProperty] public partial HardwareProfile? SelectedProfile { get; set; }
    [ObservableProperty] public partial StorageInterfaceProfile? SelectedInterface { get; set; }
    [ObservableProperty] public partial IntendedUse? SelectedUse { get; set; }
    [ObservableProperty] public partial CompatibilityMode? SelectedMode { get; set; }

    public IReadOnlyList<StorageInterfaceProfile> Interfaces => SelectedProfile?.Interfaces ?? [];
    public IReadOnlyList<IntendedUse> Uses => SelectedInterface?.Uses ?? [];
    public IReadOnlyList<CompatibilityMode> Modes => SelectedInterface?.CompatibilityModes ?? [];
    public bool HasModes => Modes.Count > 0;

    partial void OnSelectedProfileChanged(HardwareProfile? value)
    {
        OnPropertyChanged(nameof(Interfaces));
        SelectedInterface = value?.Interfaces.FirstOrDefault(i => i.Status != VerificationStatus.Planned);
    }

    partial void OnSelectedInterfaceChanged(StorageInterfaceProfile? value)
    {
        OnPropertyChanged(nameof(Uses));
        OnPropertyChanged(nameof(Modes));
        OnPropertyChanged(nameof(HasModes));
        SelectedUse = value?.Uses.FirstOrDefault();
        SelectedMode = value?.CompatibilityModes.FirstOrDefault();
    }

    public void Select(string interfaceId, IntendedUse? use = null)
    {
        if (AppServices.Current.Profiles.FindInterface(interfaceId) is not { } found) return;
        SelectedProfile = Profiles.First(p => p.Id == found.Profile.Id);
        SelectedInterface = found.Interface;
        if (use is not null && found.Interface.Uses.Contains(use.Value)) SelectedUse = use;
    }

    public static string UseText(IntendedUse u) => u switch
    {
        IntendedUse.DataExchange => "Data transfer card (share files with Windows)",
        IntendedUse.HardDrive => "Amiga hard drive (extra storage)",
        IntendedUse.BootableSystem => "Bootable system drive",
        _ => u.ToString(),
    };
}
