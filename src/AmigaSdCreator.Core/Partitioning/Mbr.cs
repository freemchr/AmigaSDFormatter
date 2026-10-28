using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Partitioning;

public sealed record MbrPartitionEntry(int Index, byte Status, byte Type, uint FirstLba, uint SectorCount)
{
    public bool IsActive => Status == 0x80;
    public bool IsEmpty => Type == 0 || SectorCount == 0;
    public long EndLbaExclusive => (long)FirstLba + SectorCount;
    public string TypeName => MbrPartitionTypes.Describe(Type);
}

public static class MbrPartitionTypes
{
    public const byte Fat12 = 0x01;
    public const byte Fat16Small = 0x04;
    public const byte Extended = 0x05;
    public const byte Fat16 = 0x06;
    public const byte NtfsExfat = 0x07;
    public const byte Fat32Chs = 0x0B;
    public const byte Fat32Lba = 0x0C;
    public const byte Fat16Lba = 0x0E;
    public const byte ExtendedLba = 0x0F;
    public const byte AmigaRdb = 0x76; // Used by some tools to wrap an RDB inside an MBR partition.
    public const byte Linux = 0x83;
    public const byte GptProtective = 0xEE;

    public static string Describe(byte type) => type switch
    {
        0x00 => "Empty",
        Fat12 => "FAT12",
        Fat16Small or Fat16 or Fat16Lba => "FAT16",
        Extended or ExtendedLba => "Extended partition",
        NtfsExfat => "NTFS / exFAT",
        Fat32Chs => "FAT32 (CHS)",
        Fat32Lba => "FAT32 (LBA)",
        AmigaRdb => "Amiga RDB (embedded)",
        Linux => "Linux",
        GptProtective => "GPT protective",
        _ => $"Type 0x{type:X2}",
    };

    public static bool IsFat(byte type) => type is Fat12 or Fat16Small or Fat16 or Fat32Chs or Fat32Lba or Fat16Lba;
}

/// <summary>Classic PC master boot record at LBA 0.</summary>
public sealed class MbrPartitionTable
{
    public const int SignatureOffset = 510;
    public const int EntriesOffset = 446;

    public uint DiskSignature { get; init; }
    public IReadOnlyList<MbrPartitionEntry> Entries { get; init; } = [];

    public IEnumerable<MbrPartitionEntry> UsedEntries => Entries.Where(e => !e.IsEmpty);
    public bool IsGptProtective => UsedEntries.Any(e => e.Type == MbrPartitionTypes.GptProtective);

    public static bool HasBootSignature(ReadOnlySpan<byte> sector0) =>
        sector0.Length >= 512 && sector0[SignatureOffset] == 0x55 && sector0[SignatureOffset + 1] == 0xAA;

    /// <summary>
    /// Parses an MBR. Returns null when the sector has no 0x55AA signature or when the entries are
    /// not self-consistent (a FAT "superfloppy" boot sector also carries 0x55AA, so it is rejected here).
    /// </summary>
    public static MbrPartitionTable? TryParse(ReadOnlySpan<byte> sector0, long deviceSectors)
    {
        if (!HasBootSignature(sector0)) return null;
        var entries = new List<MbrPartitionEntry>(4);
        for (int i = 0; i < 4; i++)
        {
            var e = sector0.Slice(EntriesOffset + i * 16, 16);
            byte status = e[0];
            if (status != 0x00 && status != 0x80) return null;
            byte type = e[4];
            uint first = Endian.U32LE(e, 8);
            uint count = Endian.U32LE(e, 12);
            if (type != 0 && count != 0)
            {
                if (first == 0) return null;
                // Protective GPT entries commonly claim 0xFFFFFFFF sectors; allow overrun for those only.
                if (type != MbrPartitionTypes.GptProtective && deviceSectors > 0 && (long)first + count > deviceSectors) return null;
            }
            entries.Add(new MbrPartitionEntry(i + 1, status, type, first, count));
        }
        if (entries.All(x => x.IsEmpty)) return null;
        return new MbrPartitionTable { DiskSignature = Endian.U32LE(sector0, 440), Entries = entries };
    }

    /// <summary>Builds a sector-0 image containing this partition table and no boot code.</summary>
    public byte[] ToSector()
    {
        var s = new byte[512];
        Endian.PutU32LE(s, 440, DiskSignature);
        foreach (var e in Entries)
        {
            if (e.Index is < 1 or > 4) throw new InvalidOperationException("MBR entry index must be 1-4.");
            var span = s.AsSpan(EntriesOffset + (e.Index - 1) * 16, 16);
            span[0] = e.Status;
            WriteChs(span[1..4], e.FirstLba);
            span[4] = e.Type;
            WriteChs(span[5..8], e.SectorCount == 0 ? 0 : e.FirstLba + e.SectorCount - 1);
            Endian.PutU32LE(span, 8, e.FirstLba);
            Endian.PutU32LE(span, 12, e.SectorCount);
        }
        s[SignatureOffset] = 0x55;
        s[SignatureOffset + 1] = 0xAA;
        return s;
    }

    /// <summary>Encodes an LBA as CHS using the conventional 255 heads / 63 sectors translation.</summary>
    private static void WriteChs(Span<byte> chs, uint lba)
    {
        const uint heads = 255, sectors = 63;
        uint c = lba / (heads * sectors);
        if (c > 1023) { chs[0] = 0xFE; chs[1] = 0xFF; chs[2] = 0xFF; return; }
        uint h = lba / sectors % heads;
        uint sct = lba % sectors + 1;
        chs[0] = (byte)h;
        chs[1] = (byte)(sct | (c >> 2 & 0xC0));
        chs[2] = (byte)(c & 0xFF);
    }
}
