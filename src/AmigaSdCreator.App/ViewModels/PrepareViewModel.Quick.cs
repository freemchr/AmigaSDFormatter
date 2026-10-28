using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.App.ViewModels;

/// <summary>A one-click configuration shown as a tile on the home screen, derived from a hardware profile.</summary>
public sealed record QuickPreset(string Title, string Subtitle, string Glyph, StorageInterfaceProfile Interface, IntendedUse Use, bool CanBoot)
{
    public string InterfaceId => Interface.Id;
    public VerificationStatus Status => Interface.Status;
    public bool ShowStatus => Status != VerificationStatus.Verified;

    // Segoe Fluent / MDL2 glyphs: Switch (two arrows) for data cards, HardDrive for RDB cards.
    public static IEnumerable<QuickPreset> For(HardwareProfile profile)
    {
        foreach (var i in profile.Interfaces.Where(i => i.Status is not (VerificationStatus.Planned or VerificationStatus.Protected)))
        {
            bool fat = i.Layouts.Contains(LayoutKind.MbrFat32);
            var use = fat ? IntendedUse.DataExchange : i.Uses.Contains(IntendedUse.HardDrive) ? IntendedUse.HardDrive : i.Uses.FirstOrDefault();
            yield return new QuickPreset(i.TileTitle ?? i.Name, i.TileSubtitle ?? "", fat ? "\uE8AB" : "\uEDA2", i, use,
                CanBoot: i.Uses.Contains(IntendedUse.BootableSystem));
        }
    }
}

/// <summary>
/// Home-screen ("quick") front end over the same plan, safety checks, writing and verification
/// used by the full wizard: pick a card, pick a tile, press Prepare.
/// </summary>
public sealed partial class PrepareViewModel
{
    public IReadOnlyList<HardwareProfile> Models => Hardware.Profiles;
    public System.Collections.ObjectModel.ObservableCollection<QuickPreset> Presets { get; } = [];

    /// <summary>The Amiga chosen on the home screen; its profile supplies the configuration tiles.</summary>
    [ObservableProperty] public partial HardwareProfile? HomeModel { get; set; }

    public string? ModelNote => HomeModel is null ? null
        : Presets.Count == 0 ? $"{HomeModel.Name} is not supported yet. {HomeModel.Summary}"
        : HomeModel.Status == VerificationStatus.Verified ? null : HomeModel.Summary;

    partial void OnHomeModelChanged(HardwareProfile? value)
    {
        Presets.Clear();
        if (value is not null) foreach (var p in QuickPreset.For(value)) Presets.Add(p);
        SelectedPreset = Presets.FirstOrDefault();
        if (SelectedPreset is null) { Plan = null; Messages.Clear(); }
        OnPropertyChanged(nameof(ModelNote));
        OnQuickChanged();
    }

    [ObservableProperty] public partial QuickPreset? SelectedPreset { get; set; }
    [ObservableProperty] public partial bool BootEnabled { get; set; }
    [ObservableProperty] public partial bool ShowDetails { get; set; }

    public bool IsHome => _main.CurrentPage == this && !IsWizard;
    [ObservableProperty] public partial bool IsWizard { get; set; }

    partial void OnSelectedPresetChanged(QuickPreset? value)
    {
        if (value is null) return;
        Step = StepTarget;
        TargetIsImage = false;
        _bootFromPreset = true;
        BootEnabled = value.CanBoot && HomeModel?.Family == "Classic";
        _bootFromPreset = false;
        Hardware.Select(value.InterfaceId, BootEnabled ? IntendedUse.BootableSystem : value.Use);
        OnQuickChanged();
    }

    private bool _bootFromPreset;

    partial void OnBootEnabledChanged(bool value)
    {
        if (_bootFromPreset || SelectedPreset is not { CanBoot: true } p) return;
        var use = value ? IntendedUse.BootableSystem : IntendedUse.HardDrive;
        if (Hardware.SelectedInterface?.Uses.Contains(use) == true) Hardware.SelectedUse = use;
    }

    // ----- Summary rows ------------------------------------------------------------------------

    public string SchemeText => Plan?.Kind switch
    {
        LayoutKind.MbrFat32 => "MBR (PC)",
        LayoutKind.Rdb => "Amiga RDB",
        LayoutKind.ImageRestore => "Restore image",
        _ => "—",
    };

    public string FilesystemText => Plan?.Kind switch
    {
        LayoutKind.MbrFat32 => $"FAT32 • label {Plan.FatLabel}",
        LayoutKind.Rdb => string.Join(", ", Plan.Partitions.Select(p => p.DosType.FriendlyName).Distinct()),
        _ => "—",
    };

    public string PartitionsText => Plan?.Kind switch
    {
        LayoutKind.MbrFat32 => $"1 × {Format.Bytes((long)Plan.FatSectorCount * Plan.SectorSize)}",
        LayoutKind.Rdb when Plan.Partitions.Count > 0 => Plan.Partitions.Count == 1
            ? $"{Plan.Partitions[0].DeviceName}: {Format.Bytes(Plan.PartitionBytes(Plan.Partitions[0]))}"
            : $"{Plan.Partitions.Count} × up to {Format.Bytes(Plan.Partitions.Max(Plan.PartitionBytes))} ({Plan.Partitions[0].DeviceName}–{Plan.Partitions[^1].DeviceName})",
        _ => "—",
    };

    public string CardPlaceholder => Devices.Cards.Count switch
    {
        0 => "No SD card found",
        1 => "1 card found \u2014 click to select it",
        var n => $"{n} cards found \u2014 click to select one",
    };

    /// <summary>Called when the card list changes: a removed card can no longer stay selected.</summary>
    private void OnCardsChanged()
    {
        if (SelectedDevice is not null && !Devices.Cards.Contains(SelectedDevice) && Step <= StepPreview) SelectedDevice = null;
        OnPropertyChanged(nameof(CardPlaceholder));
        OnQuickChanged();
    }

    public bool HasUnused => Plan is not null && Plan.UnallocatedBytes >= 64L * 1024 * 1024;
    public string UnusedText => Plan is null ? "" : $"{Format.Bytes(Plan.UnallocatedBytes)} not used";

    public string QuickHint => SelectedDevice is null
        ? "Select an SD card to see the layout."
        : SelectedPreset is null ? (Presets.Count == 0 ? "" : "Choose how the card will be used.")
        : SelectedDevice.ProtectionReason is { } r ? $"This disk is protected ({r})."
        : NeedsElevation ? "Administrator rights are needed to write cards."
        : HasErrors ? Messages.First(m => m.Severity == MessageSeverity.Error).Text
        : "";

    public bool CanQuickPrepare => Plan is not null && !HasErrors && SelectedDevice is { IsWritable: true } && !NeedsElevation && Step <= StepPreview;

    private void OnQuickChanged()
    {
        OnPropertyChanged(nameof(SchemeText));
        OnPropertyChanged(nameof(FilesystemText));
        OnPropertyChanged(nameof(PartitionsText));
        OnPropertyChanged(nameof(HasUnused));
        OnPropertyChanged(nameof(UnusedText));
        OnPropertyChanged(nameof(QuickHint));
        OnPropertyChanged(nameof(CanQuickPrepare));
        OnPropertyChanged(nameof(IsHome));
    }

    // ----- Commands ----------------------------------------------------------------------------

    /// <summary>Reads the card's current contents, then shows the confirmation panel.</summary>
    [RelayCommand]
    private async Task QuickPrepareAsync()
    {
        if (!CanQuickPrepare) return;
        ConfirmText = "";
        UnderstandErase = true; // the home screen asks only for the typed confirmation
        AcceptExperimental = false;
        await InspectAsync();
        Step = StepConfirm;
        await PreflightAsync();
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        Step = StepTarget;
        TryBuildPlan();
    }

    [RelayCommand]
    private void Done()
    {
        Step = StepTarget;
        ProgressLines.Clear();
        Checks.Clear();
        Succeeded = null;
        TryBuildPlan();
    }

    /// <summary>Opens the full step-by-step wizard (custom partitions, image files, other models).</summary>
    [RelayCommand]
    private void Customise()
    {
        IsWizard = true;
        if (Plan is not null && Step <= StepPreview)
        {
            Step = StepConfigure;
            Rebuild();
        }
        else Step = StepTarget;
        OnQuickChanged();
    }

    [RelayCommand]
    private void CreateImage()
    {
        IsWizard = true;
        Step = StepTarget;
        TargetIsImage = true;
        OnQuickChanged();
    }

    [RelayCommand]
    private void BackToHome()
    {
        IsWizard = false;
        if (Step is StepWorking) return;
        if (Step != StepResult) { Step = StepTarget; TargetIsImage = false; TryBuildPlan(); }
        OnQuickChanged();
    }
}
