using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AmigaSdCreator.Core.Imaging;

public sealed record Emu68Release(string Tag, string AssetName, string DownloadUrl);

/// <summary>
/// Video settings for a Framethrower (Denise-socket board that feeds native video to the PiStorm camera
/// connector; needs Emu68 1.0.5 or later). Values from the Framethrower wiki
/// (ausvantage.online/wiki/framethrower.html). <see cref="CmdLine"/> null means no Framethrower.
/// </summary>
public sealed record FramethrowerPreset(string Name, string? CmdLine, int HdmiMode)
{
    private const string Integer = "unicam.boot unicam.integer";
    private const string Smooth = "unicam.boot unicam.smooth unicam.b=20 unicam.c=0 unicam.phase=60";

    public static readonly FramethrowerPreset None = new("Not fitted", null, 0);
    public static readonly IReadOnlyList<FramethrowerPreset> All =
    [
        None,
        new("Pixel-perfect, 720p 50 Hz", Integer, 19),
        new("Pixel-perfect, 1080p 50 Hz", Integer, 31),
        new("Smooth 4:3, 720p 50 Hz", Smooth, 19),
        new("Smooth 4:3, 1080p 50 Hz", Smooth, 31),
    ];

    public override string ToString() => Name;
}

/// <summary>
/// Downloads the latest Emu68 release (MPL-2.0, github.com/michalsc/Emu68) and installs it onto a
/// PiStorm boot partition. Nothing is bundled with this app; downloads are cached per version.
/// </summary>
public sealed class Emu68Installer(string cacheDirectory)
{
    public const string Repository = "michalsc/Emu68";
    public static readonly IReadOnlyDictionary<string, string> Variants = new Dictionary<string, string>
    {
        ["PiStorm"] = "Emu68-pistorm.zip",
        ["PiStorm32 lite"] = "Emu68-pistorm32lite.zip",
    };

    private static readonly HttpClient Http = CreateClient();

    public async Task<Emu68Release> GetLatestAsync(string variant, CancellationToken ct = default)
    {
        string asset = Variants.TryGetValue(variant, out var a) ? a : throw new ArgumentException($"Unknown Emu68 variant '{variant}'.");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct));
        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
        foreach (var x in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (x.GetProperty("name").GetString() == asset)
                return new Emu68Release(tag, asset, x.GetProperty("browser_download_url").GetString()!);
        }
        throw new InvalidOperationException($"Emu68 {tag} has no '{asset}' download.");
    }

    /// <summary>Returns the path of the release zip, downloading it if it is not already cached.</summary>
    public async Task<string> DownloadAsync(Emu68Release release, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        string path = Path.Combine(cacheDirectory, $"{release.Tag}-{release.AssetName}");
        if (File.Exists(path) && IsValidZip(path)) return path;
        string temp = path + ".partial";
        await using (var src = await Http.GetStreamAsync(release.DownloadUrl, ct))
        await using (var dst = File.Create(temp))
            await src.CopyToAsync(dst, ct);
        if (!IsValidZip(temp)) { File.Delete(temp); throw new InvalidDataException("The downloaded Emu68 archive is damaged."); }
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Extracts the release into <paramref name="bootRoot"/> (the mounted FAT32 boot partition). With a
    /// Kickstart ROM it is copied as kick.rom; without one the config.txt "initramfs" line is disabled.
    /// With a Framethrower preset, the HDMI mode is set to CEA 50 Hz and the unicam options are added to
    /// cmdline.txt. Returns the files written.
    /// </summary>
    public static IReadOnlyList<string> Install(string zipPath, string bootRoot, string? kickstartRom, FramethrowerPreset? framethrower = null)
    {
        var written = new List<string>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var e in zip.Entries.Where(e => e.Length > 0 || e.Name.Length > 0))
            {
                string dest = Path.GetFullPath(Path.Combine(bootRoot, e.FullName));
                if (!dest.StartsWith(Path.GetFullPath(bootRoot), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe path in archive: {e.FullName}");
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
                written.Add(e.FullName);
            }
        }

        string config = Path.Combine(bootRoot, "config.txt");
        if (File.Exists(config))
        {
            var text = File.ReadAllText(config);
            text = kickstartRom is null
                ? System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^initramfs kick\.rom(?=\r?$)", "# initramfs kick.rom   (disabled by Amiga SD Creator: no Kickstart ROM file was supplied)")
                : text;
            if (framethrower?.CmdLine is not null)
            {
                text = SetConfigValue(text, "hdmi_group", "1");
                text = SetConfigValue(text, "hdmi_mode", framethrower.HdmiMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                text = SetConfigValue(text, "disable_overscan", "1");
            }
            File.WriteAllText(config, text, new UTF8Encoding(false));
        }
        if (framethrower?.CmdLine is not null && WriteCmdLine(bootRoot, framethrower.CmdLine) is var cmd && !written.Contains(cmd))
            written.Add(cmd);
        if (kickstartRom is not null)
        {
            File.Copy(kickstartRom, Path.Combine(bootRoot, "kick.rom"), overwrite: true);
            written.Add("kick.rom");
        }
        return written;
    }

    /// <summary>Replaces the first active "key=value" line, or appends one.</summary>
    internal static string SetConfigValue(string text, string key, string value)
    {
        var re = new System.Text.RegularExpressions.Regex($@"(?m)^{System.Text.RegularExpressions.Regex.Escape(key)}=[^\r\n]*");
        if (re.IsMatch(text)) return re.Replace(text, $"{key}={value}", 1);
        string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return text + (text.Length == 0 || text.EndsWith('\n') ? "" : nl) + $"{key}={value}{nl}";
    }

    /// <summary>
    /// Adds the Framethrower options to cmdline.txt (or the file named by a config.txt "cmdline=" line),
    /// replacing any earlier unicam options. Emu68 reads it as one line. Returns the file written.
    /// </summary>
    private static string WriteCmdLine(string bootRoot, string options)
    {
        string name = "cmdline.txt";
        string config = Path.Combine(bootRoot, "config.txt");
        if (File.Exists(config) && System.Text.RegularExpressions.Regex.Match(File.ReadAllText(config), @"(?m)^cmdline=\s*(\S+)") is { Success: true } m)
            name = m.Groups[1].Value.TrimStart('/', '\\');
        string path = Path.GetFullPath(Path.Combine(bootRoot, name));
        if (!path.StartsWith(Path.GetFullPath(bootRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe cmdline path in config.txt: {name}");
        var tokens = File.Exists(path)
            ? File.ReadAllText(path).Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(t => !t.StartsWith("unicam.", StringComparison.Ordinal)).ToList()
            : [];
        tokens.AddRange(options.Split(' '));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join(' ', tokens) + "\n", new UTF8Encoding(false));
        return name;
    }

    private static bool IsValidZip(string path)
    {
        try { using var z = ZipFile.OpenRead(path); return z.Entries.Count > 0; }
        catch (InvalidDataException) { return false; }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AmigaSdCreator", typeof(Emu68Installer).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return c;
    }
}
