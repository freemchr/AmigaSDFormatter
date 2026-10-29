using System.IO;
using System.Windows;
using System.Windows.Threading;
using AmigaSdCreator.Core.Safety;
using AmigaSdCreator.Ui.Services;
using AmigaSdCreator.Windows;
using Microsoft.Win32;

namespace AmigaSdCreator.App.Services;

/// <summary>Windows / WPF implementation of the shared view models' platform services.</summary>
public sealed class WpfPlatform : IPlatform
{
    public string OsName => "Windows";
    public string FileManagerName => "Explorer";
    public string AdminRightsText => "administrator rights";
    public string RestartElevatedText => "Restart as administrator";

    public IDiskService Disks { get; } = new WindowsDiskService();
    public IDialogService Dialogs { get; } = new WpfDialogs();
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmigaSdCreator");
    public string DefaultBrowseFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public IDisposable WatchDevices(Action changed)
    {
        var watcher = new DeviceChangeWatcher();
        watcher.Changed += (_, _) => changed();
        return watcher;
    }

    public IDisposable StartTimer(TimeSpan interval, Func<Task> tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += async (_, _) => await tick();
        timer.Start();
        return new Disposer(timer.Stop);
    }

    public void Invoke(Action action) => Application.Current.Dispatcher.Invoke(action);
    public void Post(Action action) => Application.Current?.Dispatcher.InvokeAsync(action);

    public bool TryRestartElevated() => Elevation.TryRestartElevated();
    public void Shutdown() => Application.Current.Shutdown();

    public void ApplyTheme(string theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var name = theme == "Light" ? "Light" : "Dark";
        var uri = new Uri($"Themes/{name}.xaml", UriKind.Relative);
        var existing = dicts.FirstOrDefault(d => d.Source?.OriginalString is "Themes/Dark.xaml" or "Themes/Light.xaml");
        var replacement = new ResourceDictionary { Source = uri };
        if (existing is null) dicts.Insert(0, replacement);
        else dicts[dicts.IndexOf(existing)] = replacement;
    }

    public void Open(string pathOrUrl) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true });

    public void ClaimFile(string path) { } // the elevated app runs as the same user

    /// <summary>
    /// Local and mapped drives. A program running as administrator does not see drive letters mapped in
    /// the user's normal (non-elevated) logon session, so persistent mappings are also read from
    /// HKCU\Network and opened by their UNC path.
    /// </summary>
    public IReadOnlyList<LocalPlace> ListPlaces()
    {
        var list = new List<LocalPlace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string label = "";
            try { if (d.DriveType != DriveType.Network && d.IsReady) label = d.VolumeLabel; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            string kind = d.DriveType switch { DriveType.Network => "network", DriveType.Removable => "removable", DriveType.CDRom => "CD/DVD", _ => "" };
            list.Add(new LocalPlace($"{d.Name.TrimEnd('\\')} {label} {(kind.Length > 0 ? $"({kind})" : "")}".Replace("  ", " ").Trim(), d.RootDirectory.FullName));
        }
        try
        {
            using var network = Registry.CurrentUser.OpenSubKey("Network");
            foreach (var letter in network?.GetSubKeyNames() ?? [])
            {
                if (letter.Length != 1 || list.Any(x => x.Path.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase))) continue;
                using var key = network!.OpenSubKey(letter);
                if (key?.GetValue("RemotePath") is string unc && unc.StartsWith(@"\\", StringComparison.Ordinal))
                    list.Add(new LocalPlace($"{letter.ToUpperInvariant()}: {unc} (network)", unc));
            }
        }
        catch (System.Security.SecurityException) { }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Waits for Windows to give the new boot partition a drive letter.</summary>
    public async Task<MountedVolume> MountBootPartitionAsync(DeviceIdentity identity, CancellationToken ct = default)
    {
        for (int i = 0; i < 30; i++)
        {
            var disk = (await Disks.EnumerateAsync(ct)).FirstOrDefault(d => d.DevicePath == identity.DevicePath && d.SerialNumber.Trim() == identity.SerialNumber);
            if (disk?.MountPoints.Count > 0) return new MountedVolume(disk.MountPoints[0]);
            await Task.Delay(1000, ct);
        }
        throw new IOException("Windows did not mount the boot partition. Remove and reinsert the card, then copy the Emu68 files manually.");
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed class WpfDialogs : IDialogService
{
    public Task<string?> OpenFileAsync(string filter, string title)
    {
        var d = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true };
        return Task.FromResult(d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null);
    }

    public Task<string?> SaveFileAsync(string filter, string title, string defaultName)
    {
        var d = new SaveFileDialog { Filter = filter, Title = title, FileName = defaultName, OverwritePrompt = true };
        return Task.FromResult(d.ShowDialog(Application.Current.MainWindow) == true ? d.FileName : null);
    }

    public Task<string?> PickFolderAsync(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return Task.FromResult(d.ShowDialog(Application.Current.MainWindow) == true ? d.FolderName : null);
    }

    public Task<bool> ConfirmAsync(string message, string title) =>
        Task.FromResult(MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

    public Task ErrorAsync(string message, string title)
    {
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public Task InfoAsync(string message, string title)
    {
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }
}
