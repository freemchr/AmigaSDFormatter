using System.Collections.ObjectModel;
using System.IO;
using AmigaSdCreator.Ui.Services;
using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Safety;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmigaSdCreator.Ui.ViewModels;

public sealed record WindowsEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified, bool IsDrive = false)
{
    public string SizeText => IsDirectory ? "" : Format.Bytes(Size);
    public string Glyph => IsDrive ? "\U0001F5B4" : IsDirectory ? "\U0001F4C1" : "\U0001F4C4";
}

/// <summary>A partition offered in the right pane. FAT partitions mounted by Windows carry their drive root.</summary>
public sealed record AmigaPartitionChoice(string Name, string Description, long Start, long Length, bool Supported, string? Reason, string? DriveRoot = null)
{
    public override string ToString() => Description;
}

/// <summary>
/// Two-pane file manager: Windows on the left, an OFS/FFS partition (on a card or image file) on the
/// right. Writing requires "Allow changes"; for physical cards this passes the file-access safety checks.
/// </summary>
public sealed partial class FilesViewModel(MainViewModel main) : PageViewModel, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AmigaSession? _session;

    public override string Title => "Amiga Files";
    public MainViewModel Main => main;
    public DeviceList Devices => main.Devices;

    // ----- Windows pane -----
    public ObservableCollection<WindowsEntry> WindowsEntries { get; } = [];
    [ObservableProperty] public partial string WindowsPath { get; set; } = AppServices.Platform.DefaultBrowseFolder;

    // ----- Amiga pane -----
    [ObservableProperty] public partial bool SourceIsImage { get; set; }
    [ObservableProperty] public partial DeviceItem? SelectedDevice { get; set; }
    [ObservableProperty] public partial string? ImagePath { get; set; }
    [ObservableProperty] public partial bool AllowChanges { get; set; }
    public ObservableCollection<AmigaPartitionChoice> PartitionChoices { get; } = [];
    [ObservableProperty] public partial AmigaPartitionChoice? SelectedPartition { get; set; }
    public ObservableCollection<AmigaEntry> AmigaEntries { get; } = [];
    [ObservableProperty] public partial string AmigaPath { get; set; } = "";
    [ObservableProperty] public partial string? VolumeInfo { get; set; }
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsWritable { get; set; }
    [ObservableProperty] public partial bool Overwrite { get; set; }
    [ObservableProperty] public partial string? NewFolderName { get; set; }

    // ----- Status -----
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    public ObservableCollection<string> Log { get; } = [];

    public string AmigaLocation => SelectedPartition is null ? "" : $"{SelectedPartition.Name}:{AmigaPath}";

    /// <summary>Drives and places for the left-pane drop-down, including mapped network drives.</summary>
    public ObservableCollection<WindowsEntry> Drives { get; } = [];
    [ObservableProperty] public partial WindowsEntry? SelectedDrive { get; set; }

    public override void OnNavigatedTo()
    {
        LoadDrives();
        if (WindowsEntries.Count == 0) LoadWindows(WindowsPath);

        // Start with the card selected on the home screen and show its contents (read-only).
        if (main.CurrentCard is { } card && (SelectedDevice != card || SourceIsImage) && !IsWorking)
        {
            _ = CloseAsync();
            SourceIsImage = false;
            SelectedDevice = card;
            AllowChanges = false;
            if (main.IsElevated) OpenSourceCommand.Execute(null);
        }
    }

    partial void OnSelectedDriveChanged(WindowsEntry? value)
    {
        if (value is not null && !IsUnder(WindowsPath, value.FullPath)) LoadWindows(value.FullPath);
    }

    [RelayCommand]
    private void LoadDrives()
    {
        Drives.Clear();
        foreach (var d in ListDrives()) Drives.Add(d);
    }

    private static List<WindowsEntry> ListDrives() =>
        AppServices.Platform.ListPlaces().Select(p => new WindowsEntry(p.Name, p.Path, true, 0, DateTime.MinValue, IsDrive: true)).ToList();

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root, PathComparison) &&
        (path.Length == root.Length || root.EndsWith(Path.DirectorySeparatorChar) || path[root.Length] == Path.DirectorySeparatorChar);

    /// <summary>Every status shown on the Files page is also logged, so problems can be diagnosed afterwards.</summary>
    partial void OnStatusChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !value.EndsWith("...", StringComparison.Ordinal)) AppServices.Current.Log.Info($"Files: {value}");
    }

    partial void OnAmigaPathChanged(string value) => OnPropertyChanged(nameof(AmigaLocation));

    // ===== Windows pane ========================================================================

    [RelayCommand]
    private void LoadWindows(string? path)
    {
        WindowsEntries.Clear();
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                WindowsPath = "";
                foreach (var d in ListDrives()) WindowsEntries.Add(d);
                return;
            }
            var dir = new DirectoryInfo(path);
            foreach (var d in dir.EnumerateDirectories().Where(Visible).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                WindowsEntries.Add(new WindowsEntry(d.Name, d.FullName, true, 0, d.LastWriteTime));
            foreach (var f in dir.EnumerateFiles().Where(Visible).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                WindowsEntries.Add(new WindowsEntry(f.Name, f.FullName, false, f.Length, f.LastWriteTime));
            WindowsPath = dir.FullName;
            var drive = Drives.Where(d => IsUnder(WindowsPath, d.FullPath)).MaxBy(d => d.FullPath.Length);
            if (drive is not null && SelectedDrive != drive) SelectedDrive = drive;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Cannot open folder: {ex.Message}";
        }

        static bool Visible(FileSystemInfo i) => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
    }

    [RelayCommand]
    private void WindowsUp()
    {
        var parent = string.IsNullOrEmpty(WindowsPath) ? null : Directory.GetParent(WindowsPath);
        LoadWindows(parent?.FullName ?? "");
    }

    [RelayCommand]
    private async Task BrowseWindowsFolderAsync()
    {
        var f = await Dialogs.PickFolder("Choose a folder on this computer");
        if (f is not null) LoadWindows(f);
    }

    public void OpenWindowsEntry(WindowsEntry e)
    {
        if (e.IsDirectory) LoadWindows(e.FullPath);
    }

    // ===== Amiga source ========================================================================

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var p = await Dialogs.OpenFile(Dialogs.ImageFilter.Replace(";*.img.gz", "").Replace(";*.gz", ""), "Open an Amiga disk image");
        if (p is null) return;
        ImagePath = p;
        SourceIsImage = true;
    }

    [RelayCommand]
    private async Task OpenSourceAsync()
    {
        await CloseAsync();
        PartitionChoices.Clear();
        if (!SourceIsImage && SelectedDevice is null) { Status = "Choose a card or an image file."; return; }
        if (SourceIsImage && string.IsNullOrEmpty(ImagePath)) { Status = "Choose an image file."; return; }
        if (!SourceIsImage && !main.IsElevated) { Status = $"Reading a card directly requires {AppServices.Platform.AdminRightsText}. Use '{AppServices.Platform.RestartElevatedText}'."; return; }

        await Work("Reading partition table", async () =>
        {
            var disk = SelectedDevice?.Disk;
            string? image = ImagePath;
            bool isImage = SourceIsImage;
            var inspection = await Task.Run(() =>
            {
                using IBlockDevice dev = isImage ? FileBlockDevice.OpenRead(image!) : AppServices.Current.Disks.OpenRead(disk!);
                return DiskInspector.Inspect(dev);
            });
            int fatIndex = 0;
            foreach (var p in inspection.Partitions)
            {
                if (p.Filesystem.IsFat)
                {
                    // FAT partitions are used through the drive letter or mount point the OS assigned to them.
                    var mounts = disk?.MountPoints ?? [];
                    string? root = !isImage && fatIndex < mounts.Count ? mounts[fatIndex] : null;
                    fatIndex++;
                    string label = p.Filesystem.Label ?? "FAT";
                    string? why = isImage ? "FAT image files are not supported" : root is null ? $"not mounted by {AppServices.Platform.OsName} - remove and reinsert the card" : null;
                    PartitionChoices.Add(new AmigaPartitionChoice(label,
                        $"{label} - {Format.Bytes(p.LengthBytes)} {p.Filesystem.DisplayName}{(root is null ? $" ({why})" : $" ({(OperatingSystem.IsWindows() ? "drive " + root.TrimEnd('\\') : root)})")}",
                        p.StartByte, p.LengthBytes, root is not null, why, root));
                    continue;
                }
                bool amiga = p.Filesystem.Kind == FilesystemKind.AmigaDos;
                bool supported = amiga && p.Filesystem.StructureValidated && p.Filesystem.DosType is { } dt && (dt.Value & 0xFF) <= 5;
                string? reason = supported ? null
                    : p.Filesystem.Kind == FilesystemKind.Blank ? "not formatted"
                    : p.Filesystem.IsAmiga ? $"{p.Filesystem.DisplayName} is not supported yet"
                    : amiga ? "volume needs validating on the Amiga"
                    : $"{p.Filesystem.DisplayName} is not supported";
                string name = p.Table == "RDB" ? p.Name : "DH0";
                PartitionChoices.Add(new AmigaPartitionChoice(name,
                    $"{name}: {p.Filesystem.Label ?? ""} - {Format.Bytes(p.LengthBytes)} {p.Filesystem.DisplayName}{(reason is null ? "" : $" ({reason})")}",
                    p.StartByte, p.LengthBytes, supported, reason));
            }
            SelectedPartition = PartitionChoices.FirstOrDefault(c => c.Supported);
            Status = PartitionChoices.Count == 0 ? $"No partitions found ({inspection.SchemeDescription})."
                : SelectedPartition is null ? "No partition that this tool can open was found." : null;
        });
        if (SelectedPartition is not null) await OpenPartitionAsync();
    }

    partial void OnSelectedPartitionChanged(AmigaPartitionChoice? value) => OnPropertyChanged(nameof(AmigaLocation));

    [RelayCommand]
    private async Task OpenPartitionAsync()
    {
        await CloseAsync();
        if (SelectedPartition is not { Supported: true } part) { Status = SelectedPartition?.Reason; return; }
        // An OS-mounted FAT card is ordinary file copying (as in Explorer), so it is always writable.
        bool writable = AllowChanges || part.DriveRoot is not null;
        if (writable && !SourceIsImage && part.DriveRoot is null &&
            !await Dialogs.Confirm($"Allow Amiga SD Creator to change files on {part.Name}: on {SelectedDevice!.Title}?\n\nThis does not erase the card, but changes are written directly to it. Keep a backup of anything important."))
            return;

        await Work("Opening volume", async () =>
        {
            var identity = SelectedDevice?.Disk.Identity;
            var disk = SelectedDevice?.Disk;
            string? image = ImagePath;
            bool isImage = SourceIsImage;
            _session = await Task.Run(async () =>
            {
                if (part.DriveRoot is { } root)
                    return AmigaSession.ForFolder(new FolderTargetVolume(root, part.Name, readOnly: false));
                if (isImage)
                {
                    IBlockDevice dev = writable ? FileBlockDevice.OpenReadWrite(image!) : FileBlockDevice.OpenRead(image!);
                    return AmigaSession.Open(dev, null, part, writable);
                }
                if (writable)
                {
                    var handle = await AppServices.Current.Operations.OpenForFileAccessAsync(identity!);
                    return AmigaSession.Open(handle.Device, handle, part, writable);
                }
                return AmigaSession.Open(AppServices.Current.Disks.OpenRead(disk!), null, part, false);
            });
            IsOpen = true;
            IsWritable = !_session.Volume.IsReadOnly;
            AmigaPath = "";
            await RefreshAmigaAsync();
        });
    }

    partial void OnAllowChangesChanged(bool value)
    {
        if (IsOpen) _ = OpenPartitionAsync();
    }

    private async Task RefreshAmigaAsync()
    {
        if (_session is null) return;
        var path = AmigaPath;
        var (entries, info) = await Task.Run(() =>
        {
            var v = _session.Volume;
            return (v.List(path), $"{v.VolumeName}  •  {v.KindText}  •  {Format.Bytes(v.FreeBytes)} free of {Format.Bytes(v.TotalBytes)}{(v.IsReadOnly ? "  •  read-only" : "")}");
        });
        AmigaEntries.Clear();
        foreach (var e in entries) AmigaEntries.Add(e);
        VolumeInfo = info;
    }

    public async Task OpenAmigaEntryAsync(AmigaEntry e)
    {
        if (!e.IsDirectory) return;
        AmigaPath = e.Path;
        await Work(null, RefreshAmigaAsync);
    }

    [RelayCommand]
    private async Task AmigaUpAsync()
    {
        if (AmigaPath.Length == 0) return;
        int i = AmigaPath.LastIndexOf('/');
        AmigaPath = i < 0 ? "" : AmigaPath[..i];
        await Work(null, RefreshAmigaAsync);
    }

    // ===== Copy / edit =========================================================================

    // Progress for long copies.
    [ObservableProperty] public partial bool IsCopying { get; set; }
    [ObservableProperty] public partial double CopyPercent { get; set; }
    [ObservableProperty] public partial string? CopyProgress { get; set; }
    private CancellationTokenSource? _copyCts;

    [RelayCommand]
    private void CancelCopy() => _copyCts?.Cancel();

    /// <summary>Shared counters for one copy operation (updated on the worker thread).</summary>
    private sealed class CopyJob(int total, IProgress<CopyJob> progress, CancellationToken ct, bool overwrite)
    {
        public int Total { get; } = total;
        public int Done, Copied, Skipped;
        public long Bytes;
        public string Current = "";
        public CancellationToken Ct { get; } = ct;
        public bool Overwrite { get; } = overwrite;
        private DateTime _lastReport;

        public void Step(string name, long bytes = 0)
        {
            Done++;
            Bytes += bytes;
            Current = name;
            if ((DateTime.UtcNow - _lastReport).TotalMilliseconds > 150 || Done == Total)
            {
                _lastReport = DateTime.UtcNow;
                progress.Report(this);
            }
        }
    }

    private async Task<(int Copied, int Skipped, bool Cancelled)> RunCopy(Func<int> count, Action<CopyJob> work)
    {
        _copyCts = new CancellationTokenSource();
        IsCopying = true;
        CopyPercent = 0;
        CopyProgress = "Counting files...";
        var started = DateTime.UtcNow;
        var progress = new Progress<CopyJob>(j =>
        {
            CopyPercent = j.Total == 0 ? 100 : 100.0 * j.Done / j.Total;
            double secs = Math.Max((DateTime.UtcNow - started).TotalSeconds, 0.1);
            CopyProgress = $"{j.Done:N0} of {j.Total:N0}  \u2022  {Format.Bytes(j.Bytes)} ({Format.Bytes((long)(j.Bytes / secs))}/s)  \u2022  {j.Current}";
        });
        var ct = _copyCts.Token;
        bool overwrite = Overwrite;
        try
        {
            return await Task.Run(() =>
            {
                var job = new CopyJob(count(), progress, ct, overwrite);
                try { work(job); }
                catch (OperationCanceledException) { return (job.Copied, job.Skipped, true); }
                return (job.Copied, job.Skipped, false);
            });
        }
        finally
        {
            IsCopying = false;
            _copyCts.Dispose();
            _copyCts = null;
        }
    }

    private static int CountWindows(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths)
        {
            n++;
            if (Directory.Exists(p))
            {
                try { n += Directory.EnumerateFileSystemEntries(p, "*", SearchOption.AllDirectories).Count(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return n;
    }

    public async Task CopyToAmigaAsync(IReadOnlyList<string> windowsPaths)
    {
        AppServices.Current.Log.Info($"Files: copy of {windowsPaths.Count} item(s) requested (open: {_session is not null}, writable: {IsWritable}).");
        if (_session is null || !IsWritable) { Status = "Open a partition (for Amiga partitions tick 'Allow changes') to copy files onto it."; return; }
        var target = AmigaPath;
        string where = AmigaLocation;
        await Work("Copying", async () =>
        {
            var (ok, skipped, cancelled) = await RunCopy(() => CountWindows(windowsPaths), job =>
            {
                try { foreach (var p in windowsPaths) CopyIn(p, target, job); }
                finally { _session.Volume.Flush(); }
            });
            Status = (cancelled ? "Cancelled. " : "") + $"Copied {ok:N0} item(s) to {where}" + (skipped > 0 ? $", skipped {skipped:N0} (see log below)." : ".");
            AppServices.Current.Log.Info($"Copied {ok} item(s) to a card volume, skipped {skipped}{(cancelled ? ", cancelled" : "")}.");
            await RefreshAmigaAsync();
        });
    }

    private void CopyIn(string path, string targetDir, CopyJob job)
    {
        job.Ct.ThrowIfCancellationRequested();
        var vol = _session!.Volume;
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        string dest = targetDir.Length == 0 ? name : $"{targetDir}/{name}";
        try
        {
            vol.ValidateName(name);
            if (Directory.Exists(path))
            {
                vol.CreateDirectory(dest, Directory.GetLastWriteTime(path));
                job.Copied++;
                job.Step(name);
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) CopyIn(child, dest, job);
            }
            else
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                vol.WriteFile(dest, fs, fs.Length, File.GetLastWriteTime(path), job.Overwrite, job.Ct);
                job.Copied++;
                job.Step(name, fs.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or AmigaFsException or UnauthorizedAccessException)
        {
            job.Skipped++;
            job.Step(name);
            AddLog($"Skipped {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task CopyToWindowsAsync(IReadOnlyList<AmigaEntry> entries, string? windowsFolder = null)
    {
        if (_session is null) return;
        string folder = windowsFolder ?? WindowsPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { Status = "Choose a folder on the left first."; return; }
        await Work("Copying to this computer", async () =>
        {
            var (ok, skipped, cancelled) = await RunCopy(() => entries.Count, job =>
            {
                foreach (var e in entries) CopyOut(e, folder, job);
            });
            Status = (cancelled ? "Cancelled. " : "") + $"Copied {ok:N0} item(s) to {folder}" + (skipped > 0 ? $", skipped {skipped:N0} (see log below)." : ".");
            LoadWindows(WindowsPath);
        });
    }

    private void CopyOut(AmigaEntry e, string folder, CopyJob job)
    {
        job.Ct.ThrowIfCancellationRequested();
        var vol = _session!.Volume;
        string safe = WindowsSafeName(e.Name);
        if (safe != e.Name) AddLog($"Renamed '{e.Name}' to '{safe}' (characters not allowed on {AppServices.Platform.OsName}).");
        string dest = Path.Combine(folder, safe);
        try
        {
            if (e.IsDirectory)
            {
                Directory.CreateDirectory(dest);
                AppServices.Platform.ClaimFile(dest);
                job.Copied++;
                job.Step(e.Name);
                foreach (var child in vol.List(e.Path)) CopyOut(child, dest, job);
                Directory.SetLastWriteTime(dest, e.Modified);
            }
            else
            {
                if (File.Exists(dest) && !job.Overwrite) { job.Skipped++; job.Step(e.Name); AddLog($"Skipped {e.Name}: already exists in {AppServices.Platform.OsName}."); return; }
                using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) vol.ReadFile(e.Path, fs, job.Ct);
                File.SetLastWriteTime(dest, e.Modified);
                AppServices.Platform.ClaimFile(dest);
                job.Copied++;
                job.Step(e.Name, e.Size);
            }
        }
        catch (Exception ex) when (ex is IOException or AmigaFsException or UnauthorizedAccessException)
        {
            job.Skipped++;
            job.Step(e.Name);
            AddLog($"Skipped {e.Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (_session is null || !IsWritable || string.IsNullOrWhiteSpace(NewFolderName)) return;
        string name = NewFolderName.Trim();
        string path = AmigaPath.Length == 0 ? name : $"{AmigaPath}/{name}";
        await Work("Creating folder", async () =>
        {
            await Task.Run(() => { _session.Volume.ValidateName(name); _session.Volume.CreateDirectory(path, null); _session.Volume.Flush(); });
            NewFolderName = null;
            await RefreshAmigaAsync();
        });
    }

    public async Task DeleteAsync(IReadOnlyList<AmigaEntry> entries)
    {
        if (_session is null || !IsWritable || entries.Count == 0) return;
        if (!await Dialogs.Confirm($"Delete {entries.Count} item(s) from {AmigaLocation}? Folders are deleted with their contents. This cannot be undone.")) return;
        await Work("Deleting", async () =>
        {
            await Task.Run(() =>
            {
                foreach (var e in entries) DeleteRecursive(e);
                _session.Volume.Flush();
            });
            await RefreshAmigaAsync();
        });
    }

    private void DeleteRecursive(AmigaEntry e)
    {
        var vol = _session!.Volume;
        try
        {
            if (e.IsDirectory) foreach (var c in vol.List(e.Path)) DeleteRecursive(c);
            vol.Delete(e.Path);
        }
        catch (Exception ex) when (ex is IOException or AmigaFsException)
        {
            AddLog($"Could not delete {e.Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (_session is null) return;
        var s = _session;
        _session = null;
        IsOpen = false;
        IsWritable = false;
        AmigaEntries.Clear();
        VolumeInfo = null;
        try { await Task.Run(s.Dispose); }
        catch (IOException ex) { Status = $"The volume could not be closed cleanly: {ex.Message}. Let the Amiga validate it."; }
    }

    private async Task Work(string? what, Func<Task> action)
    {
        await _gate.WaitAsync();
        IsWorking = true;
        main.IsBusy = true;
        if (what is not null) Status = what + "...";
        try
        {
            await action();
            if (what is not null && Status == what + "...") Status = null;
        }
        catch (SafetyCheckFailedException ex)
        {
            Status = "Not allowed: " + string.Join(" ", ex.Checks.Where(c => !c.Passed).Select(c => c.Detail));
        }
        catch (Exception ex) when (ex is IOException or AmigaFsException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = ex.Message;
            AppServices.Current.Log.Error("File manager operation failed", ex);
        }
        finally
        {
            IsWorking = false;
            main.IsBusy = false;
            _gate.Release();
        }
    }

    private void AddLog(string line)
    {
        AppServices.Current.Log.Warn(line); // also in the log file, so reasons survive after the window is closed
        AppServices.Platform.Invoke(() => Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}"));
    }

    private static string WindowsSafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    public void Dispose() => _session?.Dispose();

    /// <summary>Owns the volume and, for direct partition access, the device / exclusive handle behind it.</summary>
    private sealed class AmigaSession : IDisposable
    {
        private readonly IDisposable? _device;
        private readonly SubBlockDevice? _view;
        public ITargetVolume Volume { get; }

        private AmigaSession(ITargetVolume volume, SubBlockDevice? view, IDisposable? device)
        {
            Volume = volume;
            _view = view;
            _device = device;
        }

        public static AmigaSession ForFolder(ITargetVolume volume) => new(volume, null, null);

        public static AmigaSession Open(IBlockDevice device, IExclusiveDiskHandle? handle, AmigaPartitionChoice part, bool writable)
        {
            IDisposable owner = handle is not null ? handle : device;
            try
            {
                var view = new SubBlockDevice(device, part.Start, part.Length);
                return new AmigaSession(new FfsTargetVolume(AmigaFfsVolume.Open(view, writable)), view, owner);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { Volume.Flush(); }
            finally
            {
                Volume.Dispose();
                _view?.Dispose();
                _device?.Dispose();
            }
        }
    }
}
