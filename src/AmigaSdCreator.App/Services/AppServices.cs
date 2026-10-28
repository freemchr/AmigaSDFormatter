using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using AmigaSdCreator.Core.Profiles;
using AmigaSdCreator.Core.Safety;
using AmigaSdCreator.Windows;

namespace AmigaSdCreator.App.Services;

public sealed record RecentOperation(DateTime When, string Kind, string Target, bool Succeeded, string Summary);

public sealed record AppSettings
{
    public string Theme { get; init; } = "Dark";
    public string DefaultProfileId { get; init; } = "apollo-a6000";
    public bool FileLogging { get; init; } = true;
    public bool AllowNonRemovableBus { get; init; }
    public bool AllowLargeDisks { get; init; }
    public List<RecentOperation> Recent { get; init; } = [];
}

/// <summary>Application-wide services, created once at start-up.</summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmigaSdCreator");
    public static string ProfileDirectory => Path.Combine(DataDirectory, "Profiles");
    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public ProfileCatalog Profiles { get; }
    public WindowsDiskService Disks { get; } = new();
    public Log Log { get; }
    public AppSettings Settings { get; private set; }
    public ObservableCollection<RecentOperation> Recent { get; }

    public SafetyPolicy Policy => new() { AllowNonRemovableBus = Settings.AllowNonRemovableBus, AllowLargeDisks = Settings.AllowLargeDisks };
    public CardOperations Operations => new(Disks, Policy);

    private AppServices()
    {
        Directory.CreateDirectory(DataDirectory);
        Settings = LoadSettings();
        Log = new Log(LogDirectory) { Enabled = Settings.FileLogging };
        Profiles = ProfileCatalog.Load(ProfileDirectory);
        foreach (var e in Profiles.LoadErrors) Log.Warn($"Profile file ignored: {e}");
        Recent = new ObservableCollection<RecentOperation>(Settings.Recent.OrderByDescending(r => r.When).Take(20));
    }

    public static void Initialise() => Current = new AppServices();

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        Log.Enabled = Settings.FileLogging;
        Save();
    }

    public void AddRecent(string kind, string target, bool ok, string summary)
    {
        var op = new RecentOperation(DateTime.Now, kind, target, ok, summary);
        Application.Current.Dispatcher.Invoke(() =>
        {
            Recent.Insert(0, op);
            while (Recent.Count > 20) Recent.RemoveAt(Recent.Count - 1);
        });
        Log.Info($"{kind} on {target}: {(ok ? "succeeded" : "failed")} - {summary}");
        Save();
    }

    private void Save()
    {
        try
        {
            var s = Settings with { Recent = Recent.ToList() };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, ProfileCatalog.JsonOptions));
        }
        catch (IOException ex) { Log.Warn($"Settings not saved: {ex.Message}"); }
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), ProfileCatalog.JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new AppSettings();
    }

    public static void ApplyTheme(string theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var name = theme == "Light" ? "Light" : "Dark";
        var uri = new Uri($"Themes/{name}.xaml", UriKind.Relative);
        var existing = dicts.FirstOrDefault(d => d.Source?.OriginalString is "Themes/Dark.xaml" or "Themes/Light.xaml");
        var replacement = new ResourceDictionary { Source = uri };
        if (existing is null) dicts.Insert(0, replacement);
        else dicts[dicts.IndexOf(existing)] = replacement;
    }
}

/// <summary>
/// Plain-text operation log. Messages never contain device serial numbers (DiskDescriptor.LogId is
/// used instead), and the user's profile path is masked.
/// </summary>
public sealed class Log(string directory)
{
    private readonly object _gate = new();
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public bool Enabled { get; set; } = true;
    public string Directory => directory;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string message)
    {
        if (!Enabled) return;
        if (Profile.Length > 0) message = message.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:HH:mm:ss} {level,-5} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
