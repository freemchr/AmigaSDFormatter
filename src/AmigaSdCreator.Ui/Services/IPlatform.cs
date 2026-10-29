using AmigaSdCreator.Core.Safety;

namespace AmigaSdCreator.Ui.Services;

/// <summary>A drive or place offered in the file manager's left-hand pane.</summary>
public sealed record LocalPlace(string Name, string Path);

/// <summary>
/// Everything the shared view models need from the host OS and UI toolkit. Implemented once by the
/// WPF (Windows) app and once by the Avalonia (Linux) app.
/// </summary>
public interface IPlatform
{
    /// <summary>"Windows" or "Linux", for messages such as "Windows cannot read Amiga partitions".</summary>
    string OsName { get; }
    /// <summary>"Explorer" or "your file manager".</summary>
    string FileManagerName { get; }
    /// <summary>"administrator rights" or "root (administrator) rights".</summary>
    string AdminRightsText { get; }
    /// <summary>The label of the restart button, e.g. "Restart as administrator".</summary>
    string RestartElevatedText { get; }

    IDiskService Disks { get; }
    IDialogService Dialogs { get; }

    /// <summary>Per-user folder for settings, logs, profiles and downloads.</summary>
    string DataDirectory { get; }
    /// <summary>Where the file manager starts browsing (the user's documents or home folder).</summary>
    string DefaultBrowseFolder { get; }

    /// <summary>Raises <paramref name="changed"/> (on any thread) when a disk is attached or removed.</summary>
    IDisposable WatchDevices(Action changed);
    /// <summary>Runs <paramref name="tick"/> on the UI thread at the given interval until disposed.</summary>
    IDisposable StartTimer(TimeSpan interval, Func<Task> tick);
    /// <summary>Runs an action on the UI thread and waits for it.</summary>
    void Invoke(Action action);
    /// <summary>Queues an action on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Starts an elevated copy of the app. Returns false if the user declined.</summary>
    bool TryRestartElevated();
    void Shutdown();
    void ApplyTheme(string theme);
    /// <summary>Opens a folder or web page with the desktop's default handler.</summary>
    void Open(string pathOrUrl);
    /// <summary>
    /// Gives a file the app created back to the desktop user (the app may be running as root on Linux).
    /// </summary>
    void ClaimFile(string path);

    /// <summary>Local drives, home folders and network locations for the file manager.</summary>
    IReadOnlyList<LocalPlace> ListPlaces();

    /// <summary>
    /// Waits until the first (FAT) partition of a card that was just written is mounted and returns
    /// its root folder. Disposing the result releases any temporary mount.
    /// </summary>
    Task<MountedVolume> MountBootPartitionAsync(DeviceIdentity identity, CancellationToken ct = default);
}

public sealed class MountedVolume(string root, Action? release = null) : IDisposable
{
    public string Root { get; } = root;
    public void Dispose() => release?.Invoke();
}

/// <summary>File and message dialogs. Filters use the WPF form: "Images (*.img;*.hdf)|*.img;*.hdf|All files (*.*)|*.*".</summary>
public interface IDialogService
{
    Task<string?> OpenFileAsync(string filter, string title);
    Task<string?> SaveFileAsync(string filter, string title, string defaultName);
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string message, string title);
    Task ErrorAsync(string message, string title);
    Task InfoAsync(string message, string title);
}
