using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AmigaSdCreator.Core.Imaging;

public sealed record Emu68Release(string Tag, string AssetName, string DownloadUrl);

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
    /// Returns the files written.
    /// </summary>
    public static IReadOnlyList<string> Install(string zipPath, string bootRoot, string? kickstartRom)
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
            File.WriteAllText(config, text, new UTF8Encoding(false));
        }
        if (kickstartRom is not null)
        {
            File.Copy(kickstartRom, Path.Combine(bootRoot, "kick.rom"), overwrite: true);
            written.Add("kick.rom");
        }
        return written;
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
