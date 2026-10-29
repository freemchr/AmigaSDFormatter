using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using AmigaSdCreator.Core.Safety;
using AmigaSdCreator.Linux;
using AmigaSdCreator.Ui.Services;

namespace AmigaSdCreator.Desktop.Services;

/// <summary>Linux / Avalonia implementation of the shared view models' platform services.</summary>
public sealed class AvaloniaPlatform : IPlatform
{
    private readonly LinuxDiskService _disks = new();

    public string OsName => "Linux";
    public string FileManagerName => "your file manager";
    public string AdminRightsText => "root (administrator) rights";
    public string RestartElevatedText => "Restart as root";

    public IDiskService Disks => _disks;
    public IDialogService Dialogs { get; } = new AvaloniaDialogs();
    public string DataDirectory { get; } = Path.Combine(DesktopUser.Current.DataHome, "AmigaSdCreator");
    public string DefaultBrowseFolder => DesktopUser.Current.Home;

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

    public void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public bool TryRestartElevated() => LinuxDesktop.TryRestartElevated();

    public void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    public void ApplyTheme(string theme)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public void Open(string pathOrUrl) => LinuxDesktop.Open(pathOrUrl);

    public void ClaimFile(string path) => DesktopUser.Current.Claim(path);

    public IReadOnlyList<LocalPlace> ListPlaces() => LinuxDesktop.ListPlaces().Select(p => new LocalPlace(p.Name, p.Path)).ToList();

    public async Task<MountedVolume> MountBootPartitionAsync(DeviceIdentity identity, CancellationToken ct = default)
    {
        var (root, release) = await LinuxDesktop.MountFirstPartitionAsync(_disks, identity, ct);
        return new MountedVolume(root, release);
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>File pickers through Avalonia's storage provider (the desktop portal or GTK), and simple message boxes.</summary>
public sealed class AvaloniaDialogs : IDialogService
{
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> OpenFileAsync(string filter, string title)
    {
        if (Owner is not { } owner) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilter(filter),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string filter, string title, string defaultName)
    {
        if (Owner is not { } owner) return null;
        var types = ParseFilter(filter);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultName,
            FileTypeChoices = types,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is not { } owner) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string message, string title) => MessageBox.ShowAsync(Owner, title, message, "Yes", "No", warning: true);
    public Task ErrorAsync(string message, string title) => MessageBox.ShowAsync(Owner, title, message, "OK", null, warning: true);
    public Task InfoAsync(string message, string title) => MessageBox.ShowAsync(Owner, title, message, "OK", null, warning: false);

    /// <summary>"Images (*.img;*.hdf)|*.img;*.hdf|All files (*.*)|*.*" -> picker file types.</summary>
    internal static List<FilePickerFileType> ParseFilter(string filter)
    {
        var parts = filter.Split('|');
        var list = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p == "*.*" ? "*" : p).ToList();
            list.Add(new FilePickerFileType(parts[i]) { Patterns = patterns });
        }
        return list;
    }
}

/// <summary>A small themed modal dialog with one or two buttons.</summary>
internal static class MessageBox
{
    public static async Task<bool> ShowAsync(Window? owner, string title, string message, string ok, string? cancel, bool warning)
    {
        bool result = false;
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("WindowBrush"));

        var okButton = new Button { Content = ok, IsDefault = true, MinWidth = 80 };
        if (warning && cancel is not null) okButton.Classes.Add("Primary");
        okButton.Click += (_, _) => { result = true; window.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 0 };
        buttons.Children.Add(okButton);
        if (cancel is not null)
        {
            var cancelButton = new Button { Content = cancel, IsCancel = true, MinWidth = 80, Margin = new Thickness(0) };
            cancelButton.Click += (_, _) => window.Close();
            buttons.Children.Add(cancelButton);
        }
        else okButton.Margin = new Thickness(0);

        var icon = new TextBlock { Text = warning ? "⚠" : "ℹ", FontSize = 26, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Bind(TextBlock.ForegroundProperty, window.GetResourceObservable(warning ? "WarnBrush" : "InfoBrush"));
        var body = new DockPanel { Margin = new Thickness(20, 18, 20, 16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(buttons);
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        window.Content = body;

        if (owner is { IsVisible: true }) await window.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
        return result;
    }
}
