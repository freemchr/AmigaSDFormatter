using System.Text;
using System.Text.RegularExpressions;
using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Partitioning;

/// <summary>Minimal inspection of AmigaDOS hunk executables (filesystem handlers such as pfs3aio).</summary>
public static partial class HunkFile
{
    public const uint HunkHeader = 0x000003F3;

    public static bool IsHunkExecutable(ReadOnlySpan<byte> data) =>
        data.Length >= 24 && data.Length % 4 == 0 && Endian.U32BE(data, 0) == HunkHeader;

    /// <summary>
    /// Extracts the standard "$VER: name major.minor" string. Returns null when absent.
    /// </summary>
    public static (string Text, uint Major, uint Minor)? ReadVersion(ReadOnlySpan<byte> data)
    {
        var text = Encoding.Latin1.GetString(data);
        int idx = text.IndexOf("$VER:", StringComparison.Ordinal);
        if (idx < 0) return null;
        int end = text.IndexOfAny(['\0', '\n', '\r'], idx);
        var ver = text[(idx + 5)..(end < 0 ? Math.Min(text.Length, idx + 120) : end)].Trim();
        var m = VersionRegex().Match(ver);
        if (!m.Success) return (ver, 0, 0);
        return (ver, uint.Parse(m.Groups[1].Value), uint.Parse(m.Groups[2].Value));
    }

    [GeneratedRegex(@"\b(\d{1,5})\.(\d{1,5})\b")]
    private static partial Regex VersionRegex();
}
