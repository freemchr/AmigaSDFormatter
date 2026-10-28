namespace AmigaSdCreator.Core.Partitioning;

/// <summary>AmigaDOS filesystem identifiers (DosType), e.g. 0x444F5303 = "DOS\3".</summary>
public readonly record struct DosType(uint Value)
{
    public static readonly DosType Ofs = new(0x444F5300);
    public static readonly DosType Ffs = new(0x444F5301);
    public static readonly DosType OfsIntl = new(0x444F5302);
    public static readonly DosType FfsIntl = new(0x444F5303);
    public static readonly DosType OfsDirCache = new(0x444F5304);
    public static readonly DosType FfsDirCache = new(0x444F5305);
    public static readonly DosType OfsLongNames = new(0x444F5306);
    public static readonly DosType FfsLongNames = new(0x444F5307);
    public static readonly DosType Pfs3 = new(0x50465303);   // "PFS\3"
    public static readonly DosType Pds3 = new(0x50445303);   // "PDS\3" (PFS3 direct SCSI)
    public static readonly DosType Sfs0 = new(0x53465300);   // "SFS\0"
    public static readonly DosType Sfs2 = new(0x53465302);   // "SFS\2"
    public static readonly DosType Fat95 = new(0x46415401);  // "FAT\1" (fat95)

    /// <summary>True for the Commodore DOS\0 - DOS\7 family handled by OFS/FFS.</summary>
    public bool IsDosFamily => (Value & 0xFFFFFF00) == 0x444F5300 && (Value & 0xFF) <= 7;
    public bool IsFastFileSystem => IsDosFamily && (Value & 1) == 1;
    public bool IsInternational => IsDosFamily && (Value & 0xFF) >= 2;
    public bool IsPfs => (Value & 0xFFFFFF00) is 0x50465300 or 0x50445300;
    public bool IsSfs => (Value & 0xFFFFFF00) == 0x53465300;

    public static DosType Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return new DosType(Convert.ToUInt32(text[2..], 16));
        // Accept "DOS\3", "DOS3", "PFS\3".
        var cleaned = text.Replace("\\", "");
        if (cleaned.Length == 4 && char.IsDigit(cleaned[3]))
            return new DosType((uint)(cleaned[0] << 24 | cleaned[1] << 16 | cleaned[2] << 8 | (cleaned[3] - '0')));
        throw new FormatException($"Unrecognised DosType '{text}'.");
    }

    public override string ToString()
    {
        Span<char> c = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            char ch = (char)(Value >> (24 - 8 * i) & 0xFF);
            c[i] = ch is >= ' ' and <= '~' ? ch : '?';
        }
        return $"{c}\\{Value & 0xFF}";
    }

    public string FriendlyName => Value switch
    {
        0x444F5300 => "OFS",
        0x444F5301 => "FFS",
        0x444F5302 => "OFS International",
        0x444F5303 => "FFS International",
        0x444F5304 => "OFS DirCache",
        0x444F5305 => "FFS DirCache",
        0x444F5306 => "OFS Long Names",
        0x444F5307 => "FFS Long Names",
        0x50465303 => "PFS3",
        0x50445303 => "PFS3 (direct SCSI)",
        0x53465300 => "SFS",
        0x53465302 => "SFS (v2)",
        0x46415401 => "FAT (fat95)",
        _ => ToString(),
    };
}
