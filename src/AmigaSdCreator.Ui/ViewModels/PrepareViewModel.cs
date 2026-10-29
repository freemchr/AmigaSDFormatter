using System.Collections.ObjectModel;
using System.IO;
using AmigaSdCreator.Ui.Services;
using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;
using AmigaSdCreator.Core.Safety;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.Ui.ViewModels;

/// <summary>
/// Guided preparation: target, model, connection, use, inspect, configure, preview, confirm,
/// write, verify. Nothing is written before step 8, and only after the safety checks pass.
/// </summary>
public sealed partial class PrepareViewModel : PageViewModel
{
    public const int StepTarget = 0, StepModel = 1, StepConnection = 2, StepUse = 3, StepInspect = 4,
        StepConfigure = 5, StepPreview = 6, StepConfirm = 7, StepWorking = 8, StepResult = 9;

    public static readonly string[] StepNames =
        ["Target", "Amiga model", "Connection", "Intended use", "Inspect", "Configure", "Preview", "Confirm", "Prepare", "Verify"];

    private readonly MainViewModel _main;
    private LayoutPlan? _basePlan;
    private bool _rebuilding;

    public PrepareViewModel(MainViewModel main)
    {
        _main = main;
        HomeModel = Hardware.SelectedProfile;
        main.Devices.Cards.CollectionChanged += (_, _) => OnCardsChanged();
        Hardware.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HardwareSelection.SelectedInterface) or nameof(HardwareSelection.SelectedUse) or nameof(HardwareSelection.SelectedMode))
                TryBuildPlan();
            Changed();
        };
    }

    public override string Title => "Prepare SD Card";
    public MainViewModel Main => _main;
    public DeviceList Devices => _main.Devices;
    public HardwareSelection Hardware { get; } = new();
    public IReadOnlyList<string> Steps => StepNames;

    [ObservableProperty] public partial int Step { get; set; }

    // Target
    [ObservableProperty] public partial bool TargetIsImage { get; set; }
    [ObservableProperty] public partial DeviceItem? SelectedDevice { get; set; }
    [ObservableProperty] public partial string? ImagePath { get; set; }
    [ObservableProperty] public partial string ImageSizeText { get; set; } = "8 GB";
    private DeviceIdentity? _selectedIdentity;

    // Inspect
    [ObservableProperty] public partial DiagnosticReport? Existing { get; set; }
    [ObservableProperty] public partial string? InspectError { get; set; }
    [ObservableProperty] public partial bool IsInspecting { get; set; }

    // Configure
    [ObservableProperty] public partial LayoutPlan? Plan { get; set; }
    public ObservableCollection<PartitionRow> Partitions { get; } = [];
    public ObservableCollection<FileSystemDriverPlan> Handlers { get; } = [];
    public ObservableCollection<PlanMessage> Messages { get; } = [];
    [ObservableProperty] public partial string FatLabel { get; set; } = "SD0";
    [ObservableProperty] public partial int FatClusterBytes { get; set; }
    public IReadOnlyList<int> ClusterChoices { get; } = [512, 1024, 2048, 4096, 8192, 16384, 32768];
    public IReadOnlyList<string> DosTypeChoices { get; } = ["DOS\\3", "DOS\\1", "DOS\\7", "PFS\\3", "SFS\\0", "SFS\\2"];

    // Confirm
    [ObservableProperty] public partial string ConfirmText { get; set; } = "";
    [ObservableProperty] public partial bool UnderstandErase { get; set; }
    public ObservableCollection<SafetyCheck> SafetyChecks { get; } = [];

    // Run / result
    public ObservableCollection<string> ProgressLines { get; } = [];
    public ObservableCollection<VerificationCheck> Checks { get; } = [];
    [ObservableProperty] public partial bool? Succeeded { get; set; }
    [ObservableProperty] public partial string? ResultText { get; set; }
    [ObservableProperty] public partial string? NextStepsText { get; set; }

    public long DeviceBytes => TargetIsImage ? ParseSize(ImageSizeText) ?? 0 : SelectedDevice?.Disk.SizeBytes ?? 0;
    public int SectorSize => TargetIsImage ? 512 : SelectedDevice?.Disk.LogicalSectorSize ?? 512;
    public bool IsFatPlan => Plan?.Kind == LayoutKind.MbrFat32;
    public bool IsRdbPlan => Plan?.Kind is LayoutKind.Rdb or LayoutKind.Emu68;
    public bool IsProtectedInterface => Hardware.SelectedInterface?.Status == VerificationStatus.Protected;
    public bool NeedsElevation => !TargetIsImage && !_main.IsElevated;
    public bool HasErrors => LayoutValidator.HasErrors(Messages);
    public string TargetDescription => TargetIsImage
        ? $"New image file {Path.GetFileName(ImagePath ?? "")} ({Format.Bytes(DeviceBytes)})"
        : SelectedDevice is null ? "" : $"{SelectedDevice.Title} - {SelectedDevice.Detail}";
    public string UnallocatedText => Plan is null ? "" : Format.Bytes(Math.Max(0, Plan.UnallocatedBytes));

    public bool CanGoBack => Step is > StepTarget and < StepWorking;

    public bool CanGoNext => Step switch
    {
        StepTarget => TargetIsImage
            ? !string.IsNullOrWhiteSpace(ImagePath) && DeviceBytes >= 16 * 1024 * 1024
            : SelectedDevice is { IsWritable: true } && !NeedsElevation,
        StepModel => Hardware.SelectedProfile is { Interfaces.Count: > 0 },
        StepConnection => Hardware.SelectedInterface is { Status: not VerificationStatus.Planned },
        StepUse => Hardware.SelectedUse is not null && !IsProtectedInterface,
        StepInspect => !IsInspecting,
        StepConfigure or StepPreview => Plan is not null && !HasErrors,
        StepConfirm => ConfirmReady,
        _ => false,
    };

    private bool ConfirmReady =>
        UnderstandErase &&
        (TargetIsImage || string.Equals(ConfirmText.Trim(), SafetyPolicy.ConfirmationWord, StringComparison.Ordinal));

    /// <summary>Starts the wizard preset for an interface and use (from the dashboard or diagnostics).</summary>
    public void Start(string interfaceId, IntendedUse use, DeviceItem? card = null)
    {
        Reset();
        TargetIsImage = false;
        var found = AppServices.Current.Profiles.FindInterface(interfaceId);
        if (found is { } f) HomeModel = Models.FirstOrDefault(m => m.Id == f.Profile.Id);
        if (Presets.FirstOrDefault(p => p.InterfaceId == interfaceId) is { } preset)
        {
            IsWizard = false;
            SelectedPreset = preset;
            if (preset.CanBoot) BootEnabled = use == IntendedUse.BootableSystem;
        }
        else
        {
            IsWizard = true;
            Hardware.Select(interfaceId, use);
        }
        if (card is not null) SelectedDevice = card;
    }

    public override void OnNavigatedTo() => Changed();

    partial void OnSelectedDeviceChanged(DeviceItem? value)
    {
        _selectedIdentity = value?.Disk.Identity;
        TryBuildPlan();
        Changed();
    }

    partial void OnTargetIsImageChanged(bool value) => Changed();
    partial void OnImagePathChanged(string? value) => Changed();
    partial void OnImageSizeTextChanged(string value) => Changed();
    partial void OnConfirmTextChanged(string value) => Changed();
    partial void OnUnderstandEraseChanged(bool value) => Changed();
    partial void OnFatLabelChanged(string value) => Rebuild();
    partial void OnFatClusterBytesChanged(int value) => Rebuild();
    partial void OnStepChanged(int value) => Changed();

    private void Changed()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(NeedsElevation));
        OnPropertyChanged(nameof(DeviceBytes));
        OnPropertyChanged(nameof(TargetDescription));
        OnPropertyChanged(nameof(IsProtectedInterface));
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var path = await Dialogs.SaveFile("Disk image (*.img)|*.img|Amiga hardfile (*.hdf)|*.hdf", "Create disk image", "amiga-sd.img");
        if (path is not null) { ImagePath = path; TargetIsImage = true; }
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack) Step--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!CanGoNext) return;
        switch (Step)
        {
            case StepUse:
                Step = StepInspect;
                await InspectAsync();
                return;
            case StepInspect:
                BuildPlan();
                Step = StepConfigure;
                return;
            case StepPreview:
                ConfirmText = "";
                UnderstandErase = false;
                Step = StepConfirm;
                await PreflightAsync();
                return;
            case StepConfirm:
                await RunAsync();
                return;
            default:
                Step++;
                return;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        Step = StepTarget;
        Existing = null;
        Plan = null;
        _basePlan = null;
        Partitions.Clear();
        Handlers.Clear();
        Messages.Clear();
        ProgressLines.Clear();
        Checks.Clear();
        SafetyChecks.Clear();
        Succeeded = null;
        ResultText = NextStepsText = InspectError = null;
        ConfirmText = "";
        UnderstandErase = false;
    }

    [RelayCommand]
    private void GoToRestore()
    {
        _main.Backup.SelectedTarget = SelectedDevice;
        _main.CurrentPage = _main.Backup;
    }

    // ----- Inspect -----------------------------------------------------------------------------

    private async Task InspectAsync()
    {
        Existing = null;
        InspectError = null;
        if (TargetIsImage) return;
        IsInspecting = true;
        Changed();
        try
        {
            var disk = SelectedDevice!.Disk;
            var (p, i, u) = (Hardware.SelectedProfile, Hardware.SelectedInterface, Hardware.SelectedUse);
            Existing = await Task.Run(() =>
            {
                using var dev = AppServices.Current.Disks.OpenRead(disk);
                return DiagnosticEngine.Analyse(DiskInspector.Inspect(dev), p, i, u, disk.DisplayName);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InspectError = $"The card could not be read ({ex.Message}). You can still continue, but its current contents are unknown.";
        }
        finally
        {
            IsInspecting = false;
            Changed();
        }
    }

    // ----- Configure ---------------------------------------------------------------------------

    /// <summary>Rebuilds the recommended plan whenever the target or hardware changes before confirmation.</summary>
    private void TryBuildPlan()
    {
        if (Step > StepPreview) return;
        _basePlan = null;
        if (DeviceBytes <= 0 || Hardware.SelectedProfile is null || Hardware.SelectedInterface is null || Hardware.SelectedUse is null)
        {
            Plan = null;
            Messages.Clear(); // warnings about a previously selected card no longer apply
            OnQuickChanged();
            return;
        }
        try { BuildPlan(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Plan = null;
            Messages.Clear();
            Messages.Add(PlanMessage.Error(ex.Message));
        }
        OnQuickChanged();
    }

    private void BuildPlan()
    {
        var iface = Hardware.SelectedInterface!;
        _basePlan = LayoutPlanner.Recommend(Hardware.SelectedProfile!, iface, Hardware.SelectedUse!.Value, DeviceBytes, SectorSize, Hardware.SelectedMode?.Id);
        _rebuilding = true;
        Partitions.Clear();
        foreach (var p in _basePlan.Partitions) Partitions.Add(new PartitionRow(this, p));
        Handlers.Clear();
        FatLabel = _basePlan.FatLabel;
        FatClusterBytes = _basePlan.FatClusterBytes ?? 4096;
        _rebuilding = false;
        Rebuild();
    }

    internal void Rebuild()
    {
        if (_rebuilding || _basePlan is null || Hardware.SelectedInterface is not { } iface) return;
        _rebuilding = true;
        try
        {
            var limits = iface.EffectiveLimits(Hardware.SelectedMode?.Id);
            var plan = _basePlan with
            {
                CompatibilityModeId = Hardware.SelectedMode?.Id,
                FatLabel = FatLabel,
                FatClusterBytes = FatClusterBytes,
                Partitions = Partitions.Select(r => r.ToPlan()).ToList(),
                FileSystems = Handlers.ToList(),
            };
            plan = LayoutPlanner.Recalculate(plan, limits.MaxEnd);
            for (int i = 0; i < Partitions.Count; i++) Partitions[i].Update(plan, plan.Partitions[i]);
            Plan = plan;
            Messages.Clear();
            foreach (var m in LayoutValidator.Validate(plan, iface).OrderByDescending(m => m.Severity)) Messages.Add(m);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            Messages.Clear();
            Messages.Add(PlanMessage.Error(ex.Message));
        }
        finally
        {
            _rebuilding = false;
        }
        OnPropertyChanged(nameof(IsFatPlan));
        OnPropertyChanged(nameof(IsRdbPlan));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(UnallocatedText));
        OnPropertyChanged(nameof(PreviewLines));
        OnQuickChanged();
        Changed();
    }

    [RelayCommand]
    private void AddPartition()
    {
        if (_basePlan is null || Hardware.SelectedInterface is not { } iface) return;
        string prefix = iface.Defaults.DeviceNamePrefix ?? "DH";
        int n = 0;
        while (Partitions.Any(p => p.DeviceName.Equals($"{prefix}{n}", StringComparison.OrdinalIgnoreCase))) n++;
        Partitions.Add(new PartitionRow(this, new PartitionPlan
        {
            DeviceName = $"{prefix}{n}",
            VolumeName = $"{iface.Defaults.VolumeNamePrefix ?? "Work"}{n}",
            RequestedBytes = 0,
            DosType = iface.Defaults.DefaultDosType,
            BootPriority = -128,
            MaxTransfer = iface.Defaults.DefaultMaxTransfer,
            Mask = iface.Defaults.DefaultMask,
        }));
        Rebuild();
    }

    [RelayCommand]
    private void RemovePartition(PartitionRow? row)
    {
        if (row is null) return;
        Partitions.Remove(row);
        Rebuild();
    }

    [RelayCommand]
    private async Task AddHandlerAsync()
    {
        var path = await Dialogs.OpenFile("Amiga filesystem handler|*.*", "Choose a filesystem handler (e.g. pfs3aio)");
        if (path is null) return;
        try
        {
            var bin = File.ReadAllBytes(path);
            if (!HunkFile.IsHunkExecutable(bin)) { await Dialogs.Error("That file is not an AmigaDOS executable (hunk file)."); return; }
            var name = Path.GetFileName(path);
            var dt = name.Contains("pfs", StringComparison.OrdinalIgnoreCase) ? DosType.Pfs3
                : name.Contains("sfs", StringComparison.OrdinalIgnoreCase) || name.Contains("smartfilesystem", StringComparison.OrdinalIgnoreCase) ? DosType.Sfs0
                : DosType.FfsIntl;
            var ver = HunkFile.ReadVersion(bin);
            Handlers.Add(new FileSystemDriverPlan(dt, name, bin, (ver?.Major ?? 0) << 16 | (ver?.Minor ?? 0), ver?.Text ?? "version unknown"));
            Rebuild();
        }
        catch (IOException ex) { await Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private void RemoveHandler(FileSystemDriverPlan? h)
    {
        if (h is null) return;
        Handlers.Remove(h);
        Rebuild();
    }

    public IReadOnlyList<string> PreviewLines
    {
        get
        {
            if (Plan is null) return [];
            var lines = new List<string>
            {
                $"Target: {TargetDescription}",
                $"Hardware: {Hardware.SelectedProfile?.Name} - {Hardware.SelectedInterface?.Name}",
                $"Use: {(Hardware.SelectedUse is { } u ? HardwareSelection.UseText(u) : "")}",
            };
            if (Hardware.SelectedMode is { } m) lines.Add($"Compatibility: {m.Name}");
            if (Plan.Kind == LayoutKind.MbrFat32)
            {
                lines.Add("Partition table: MBR (PC), one primary partition, type 0x0C (FAT32 LBA)");
                lines.Add($"Partition: starts at 1 MiB, {Format.Bytes((long)Plan.FatSectorCount * Plan.SectorSize)}");
                lines.Add($"Filesystem: FAT32, label '{Plan.FatLabel}', {Format.Bytes(Plan.FatClusterBytes ?? 0)} clusters");
            }
            else if (Plan.Kind is LayoutKind.Rdb or LayoutKind.Emu68 && Plan.Geometry is { } g)
            {
                if (Plan.Kind == LayoutKind.Emu68)
                    lines.Add($"MBR: partition 1 FAT32 '{Plan.FatLabel}' {Format.Bytes((long)Plan.FatSectorCount * Plan.SectorSize)} (Emu68 boot), partition 2 type 0x76 {Format.Bytes(Plan.AmigaRegionBytes)} holding the RDB below");
                lines.Add($"Partition table: Amiga Rigid Disk Block at block 0; geometry {g.Cylinders} cyl x {g.Heads} heads x {g.Sectors} sectors");
                lines.Add($"Reserved for RDB: {Plan.RdbCylinders} cylinders ({Format.Bytes(Plan.RdbCylinders * Plan.CylinderBytes)})");
                foreach (var p in Plan.Partitions)
                    lines.Add($"{p.DeviceName}: \"{p.VolumeName}\"  {Format.Bytes(Plan.PartitionBytes(p))}  {p.DosType.FriendlyName}  {(p.Bootable ? $"bootable (priority {p.BootPriority})" : "not bootable")}  " +
                              $"{(p.WillBeFormatted ? "formatted now" : "format on Amiga")}  MaxTransfer {Format.Hex(p.MaxTransfer)}");
                foreach (var h in Plan.FileSystems) lines.Add($"Embedded filesystem: {h.DosType.FriendlyName} from {h.SourceName} ({h.VersionText})");
            }
            lines.Add("The first and last 1 MiB of the card are cleared to remove old partition tables.");
            return lines;
        }
    }

    // ----- Confirm and run ---------------------------------------------------------------------

    private async Task PreflightAsync()
    {
        SafetyChecks.Clear();
        if (TargetIsImage || _selectedIdentity is null) return;
        try
        {
            var checks = await AppServices.Current.Operations.PreflightAsync(_selectedIdentity, DeviceBytes, SafetyPolicy.ConfirmationWord);
            foreach (var c in checks.Where(c => c.Name != "Erase confirmed")) SafetyChecks.Add(c);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafetyChecks.Add(new SafetyCheck("Device check", false, ex.Message));
        }
    }

    private async Task RunAsync()
    {
        var plan = Plan!;
        var iface = Hardware.SelectedInterface!;
        Step = StepWorking;
        ProgressLines.Clear();
        Checks.Clear();
        Succeeded = null;
        _main.IsBusy = true;
        var progress = new Progress<string>(s => ProgressLines.Add($"{DateTime.Now:HH:mm:ss}  {s}"));
        string target = TargetIsImage ? "image file" : SelectedDevice!.Disk.LogId;
        try
        {
            VerificationResult result;
            if (TargetIsImage)
            {
                var path = ImagePath!;
                result = await Task.Run(() =>
                {
                    using var dev = FileBlockDevice.Create(path, plan.DeviceBytes, plan.SectorSize);
                    DiskImageBuilder.Apply(dev, plan, iface, progress);
                    ((IProgress<string>)progress).Report("Verifying");
                    return LayoutVerifier.Verify(dev, plan);
                });
                AppServices.Platform.ClaimFile(path);
            }
            else
            {
                var identity = _selectedIdentity!;
                var confirm = ConfirmText;
                result = await Task.Run(() => AppServices.Current.Operations.PrepareAsync(identity, confirm, plan, iface, progress));
            }
            foreach (var c in result.Checks) Checks.Add(c);
            Succeeded = result.Passed;
            ResultText = result.Passed
                ? "The card was prepared and every structure was read back and verified."
                : "The card was written, but verification found differences. Do not use it until the problem is understood; see the failed checks below.";
            NextStepsText = NextSteps(plan, iface);
            if (result.Passed && plan.Kind == LayoutKind.Emu68 && !TargetIsImage && InstallEmu68)
                await InstallEmu68Async(_selectedIdentity!, ((IProgress<string>)progress));
            AppServices.Current.AddRecent("Prepare", target, result.Passed, $"{plan.Kind} for {iface.Name}");
        }
        catch (SafetyCheckFailedException ex)
        {
            Succeeded = false;
            ResultText = "Nothing was written. " + string.Join(" ", ex.Checks.Where(c => !c.Passed).Select(c => c.Detail));
            AppServices.Current.AddRecent("Prepare", target, false, "blocked by safety checks");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Succeeded = false;
            ResultText = $"Preparation failed: {ex.Message} If the failure happened after writing started, the card is in an incomplete state; run the preparation again or restore a backup.";
            AppServices.Current.Log.Error("Preparation failed", ex);
            AppServices.Current.AddRecent("Prepare", target, false, ex.Message);
        }
        finally
        {
            _main.IsBusy = false;
            Step = StepResult;
            await _main.Devices.RefreshAsync();
        }
    }

    private string NextSteps(LayoutPlan plan, StorageInterfaceProfile iface)
    {
        var lines = new List<string>();
        string os = AppServices.Platform.OsName;
        if (TargetIsImage)
        {
            lines.Add("Write the image to a card with Backup & Restore > Restore, or use it as a hardfile in WinUAE.");
        }
        if (plan.Kind == LayoutKind.MbrFat32)
        {
            lines.Add($"{os} shows the card as a normal drive labelled {plan.FatLabel}: copy files onto it with {AppServices.Platform.FileManagerName} or the Files page, then eject it safely.");
            lines.Add(iface.Id switch
            {
                var id when id.StartsWith("a6000", StringComparison.Ordinal) => "On the A6000, mount it from the ApolloOS Start Menu: SD Card > Mount.",
                "gotek-usb" => "Copy your ADF floppy images onto the stick, then plug it into the Gotek.",
                _ => "On the Amiga, mount it with your FAT filesystem handler (e.g. fat95, or CrossDOS on AmigaOS 3.1.4/3.2).",
            });
        }
        if (plan.Kind == LayoutKind.Emu68)
        {
            lines.Add($"Copy the files from an Emu68 release (and your Kickstart ROM, if your config uses one) onto the {plan.FatLabel} drive that {os} now shows, then eject the card.");
            lines.Add("Under AmigaOS the Amiga partitions are on brcm-sdhc.device (Pi 3 / Zero 2) or brcm-emmc.device (Pi 4 / CM4).");
        }
        if (plan.Kind is LayoutKind.Rdb or LayoutKind.Emu68)
        {
            lines.Add($"{os} cannot read Amiga partitions. If {os} offers to format or initialise this card, choose Cancel - doing so would erase it.");
            lines.Add("Use 'Amiga Files' in this app to copy files onto the FFS partitions.");
            var unformatted = plan.Partitions.Where(p => !p.WillBeFormatted).Select(p => p.DeviceName + ":").ToList();
            if (unformatted.Count > 0)
                lines.Add($"Format {string.Join(", ", unformatted)} on the Amiga before use (they show as 'Uninitialized' until then - that is expected).");
            if (plan.Partitions.Any(p => p.Bootable))
                lines.Add("The boot partition is empty: install Workbench / AmigaOS onto it from your own licensed installation media.");
        }
        foreach (var u in iface.Unverified) lines.Add("Unverified: " + u);
        return string.Join(Environment.NewLine, lines.Select(l => "• " + l));
    }

    internal static long? ParseSize(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace(" ", "");
        long mult = 1L << 30;
        if (t.EndsWith("GB") || t.EndsWith("G")) t = t.TrimEnd('B').TrimEnd('G');
        else if (t.EndsWith("MB") || t.EndsWith("M")) { t = t.TrimEnd('B').TrimEnd('M'); mult = 1L << 20; }
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0 || v > 2048L * 1024) return null;
        long bytes = (long)(v * mult);
        return bytes / 512 * 512;
    }
}

/// <summary>An editable partition row in the configuration step.</summary>
public sealed partial class PartitionRow : ObservableObject
{
    private readonly PrepareViewModel _owner;
    private PartitionPlan _plan;

    public PartitionRow(PrepareViewModel owner, PartitionPlan plan)
    {
        _owner = owner;
        _plan = plan;
        DeviceName = plan.DeviceName;
        VolumeName = plan.VolumeName;
        SizeMiB = plan.RequestedBytes <= 0 ? "rest" : (plan.RequestedBytes / (1024 * 1024)).ToString();
        DosTypeText = plan.DosType.ToString();
        Bootable = plan.Bootable;
        BootPriority = plan.BootPriority;
        FormatNow = plan.FormatNow;
        MaxTransferText = Format.Hex(plan.MaxTransfer);
    }

    [ObservableProperty] public partial string DeviceName { get; set; }
    [ObservableProperty] public partial string VolumeName { get; set; }
    [ObservableProperty] public partial string SizeMiB { get; set; }
    [ObservableProperty] public partial string DosTypeText { get; set; }
    [ObservableProperty] public partial bool Bootable { get; set; }
    [ObservableProperty] public partial int BootPriority { get; set; }
    [ObservableProperty] public partial bool FormatNow { get; set; }
    [ObservableProperty] public partial string MaxTransferText { get; set; }
    [ObservableProperty] public partial string ActualSize { get; set; } = "";
    [ObservableProperty] public partial string Cylinders { get; set; } = "";
    [ObservableProperty] public partial bool CanFormatNow { get; set; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(ActualSize) or nameof(Cylinders) or nameof(CanFormatNow))) _owner.Rebuild();
    }

    public PartitionPlan ToPlan()
    {
        long requested = SizeMiB.Trim().Equals("rest", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(SizeMiB)
            ? 0
            : long.TryParse(SizeMiB.Trim(), out var mib) && mib > 0 ? mib * 1024 * 1024 : throw new FormatException($"{DeviceName}: size must be a number of MiB or 'rest'.");
        var mt = MaxTransferText.Trim();
        uint maxTransfer = Convert.ToUInt32(mt.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? mt[2..] : mt, 16);
        return _plan with
        {
            DeviceName = DeviceName.Trim(),
            VolumeName = VolumeName.Trim(),
            RequestedBytes = requested,
            DosType = DosType.Parse(DosTypeText),
            Bootable = Bootable,
            BootPriority = BootPriority,
            FormatNow = FormatNow,
            MaxTransfer = maxTransfer,
        };
    }

    internal void Update(LayoutPlan plan, PartitionPlan p)
    {
        _plan = p;
        ActualSize = p.HighCyl >= p.LowCyl ? Format.Bytes(plan.PartitionBytes(p)) : "no space";
        Cylinders = $"{p.LowCyl}-{p.HighCyl}";
        CanFormatNow = Core.FileSystems.AmigaFfsFormatter.CanFormat(p.DosType);
    }
}
