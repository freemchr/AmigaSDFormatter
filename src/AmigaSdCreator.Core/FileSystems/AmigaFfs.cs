using System.Text;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.FileSystems;

/// <summary>
/// AmigaDOS date stamp: days since 1978-01-01, minutes since midnight, 1/50 s ticks. AmigaDOS has no
/// time zone: stamps are local wall-clock time, so callers pass and receive local times.
/// </summary>
public readonly record struct AmigaDate(uint Days, uint Minutes, uint Ticks)
{
    private static readonly DateTime Epoch = new(1978, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static AmigaDate From(DateTime local)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (t < Epoch) t = Epoch;
        var span = t - Epoch;
        uint days = (uint)span.TotalDays;
        var rem = span - TimeSpan.FromDays(days);
        uint minutes = (uint)rem.TotalMinutes;
        uint ticks = (uint)((rem - TimeSpan.FromMinutes(minutes)).TotalMilliseconds / 20);
        return new AmigaDate(days, minutes, ticks);
    }

    public DateTime ToDateTime() => Epoch.AddDays(Days).AddMinutes(Minutes).AddMilliseconds(Ticks * 20.0);
}

/// <summary>
/// Quick-format ("Format QUICK") implementation of the Commodore OFS/FFS on-disk layout, as
/// documented in the ADF FAQ and the AmigaDOS technical reference:
/// boot block (DosType), root block in the middle of the partition, and a free-block bitmap.
/// </summary>
public static class AmigaFfsFormatter
{
    public const int BlockSize = 512;
    public const int ReservedBlocks = 2;
    public const int HashTableSize = 72;           // BSIZE/4 - 56
    public const int BitsPerBitmapBlock = 127 * 32; // 127 longs after the checksum
    public const int MaxNameLength = 30;
    private const int RootBitmapPointers = 25;
    private const int ExtensionPointers = 127;

    /// <summary>DosTypes this formatter can create. DirCache and long-name variants need extra structures.</summary>
    public static bool CanFormat(DosType dosType) =>
        dosType == DosType.Ofs || dosType == DosType.Ffs || dosType == DosType.OfsIntl || dosType == DosType.FfsIntl;

    public static long RootBlockFor(long blocks) => (blocks - 1 + ReservedBlocks) / 2;

    public static void Format(IBlockDevice volume, DosType dosType, string volumeName, DateTime? when = null, CancellationToken cancellationToken = default)
    {
        if (!CanFormat(dosType)) throw new NotSupportedException($"Formatting {dosType.FriendlyName} is not supported; format it on the Amiga.");
        if (volume.SectorSize != BlockSize) throw new NotSupportedException("Only 512-byte blocks are supported.");
        ValidateVolumeName(volumeName);
        long blocks = volume.SectorCount();
        if (blocks < 16) throw new ArgumentException("Partition too small.");
        if (blocks > uint.MaxValue) throw new NotSupportedException("Partition too large for 32-bit block numbers.");

        var date = AmigaDate.From(when ?? DateTime.Now);
        long root = RootBlockFor(blocks);
        long bitmapBits = blocks - ReservedBlocks;
        int bitmapCount = (int)((bitmapBits + BitsPerBitmapBlock - 1) / BitsPerBitmapBlock);
        int extCount = bitmapCount > RootBitmapPointers
            ? (bitmapCount - RootBitmapPointers + ExtensionPointers - 1) / ExtensionPointers
            : 0;

        // Bitmap blocks follow the root block, then any bitmap extension blocks (as Format does).
        var bitmapBlocks = Enumerable.Range(0, bitmapCount).Select(i => root + 1 + i).ToArray();
        var extBlocks = Enumerable.Range(0, extCount).Select(i => root + 1 + bitmapCount + i).ToArray();
        long lastMeta = root + bitmapCount + extCount;
        if (lastMeta >= blocks) throw new InvalidOperationException("Partition too small for its bitmap.");

        // Boot blocks: DosType followed by zeros (not bootable, as for any formatted HD partition).
        volume.Zero(0, ReservedBlocks * BlockSize, cancellationToken);
        var boot = new byte[BlockSize];
        Endian.PutU32BE(boot, 0, dosType.Value);
        volume.WriteSector(0, boot);

        // Root block.
        var r = new byte[BlockSize];
        Endian.PutU32BE(r, 0, 2);                      // T_HEADER
        Endian.PutU32BE(r, 12, HashTableSize);
        Endian.PutU32BE(r, BlockSize - 200, 0xFFFFFFFF); // bm_flag: bitmap valid
        for (int i = 0; i < Math.Min(bitmapCount, RootBitmapPointers); i++)
            Endian.PutU32BE(r, BlockSize - 196 + i * 4, (uint)bitmapBlocks[i]);
        Endian.PutU32BE(r, BlockSize - 96, extCount > 0 ? (uint)extBlocks[0] : 0);
        PutDate(r, BlockSize - 92, date);              // root alteration date
        Endian.PutBcplString(r, BlockSize - 80, volumeName, MaxNameLength);
        PutDate(r, BlockSize - 40, date);              // volume alteration date
        PutDate(r, BlockSize - 28, date);              // creation date
        Endian.PutU32BE(r, BlockSize - 4, 1);          // ST_ROOT
        SetHeaderChecksum(r);
        volume.WriteSector(root, r);

        // Bitmap: bit set = block free. Bit 0 of the first long represents block 2.
        var used = new HashSet<long>(bitmapBlocks.Concat(extBlocks)) { root };
        for (int bm = 0; bm < bitmapCount; bm++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var b = new byte[BlockSize];
            long firstBit = (long)bm * BitsPerBitmapBlock;
            for (int l = 0; l < 127; l++)
            {
                uint word = 0;
                for (int bit = 0; bit < 32; bit++)
                {
                    long n = firstBit + l * 32L + bit;
                    if (n >= bitmapBits) break;
                    if (!used.Contains(n + ReservedBlocks)) word |= 1u << bit;
                }
                Endian.PutU32BE(b, 4 + l * 4, word);
            }
            AmigaChecksum.SetSumToZero(b, 128, 0);
            volume.WriteSector(bitmapBlocks[bm], b);
        }

        for (int e = 0; e < extCount; e++)
        {
            var b = new byte[BlockSize];
            int start = RootBitmapPointers + e * ExtensionPointers;
            for (int i = 0; i < ExtensionPointers && start + i < bitmapCount; i++)
                Endian.PutU32BE(b, i * 4, (uint)bitmapBlocks[start + i]);
            Endian.PutU32BE(b, BlockSize - 4, e + 1 < extCount ? (uint)extBlocks[e + 1] : 0);
            volume.WriteSector(extBlocks[e], b);
        }
        volume.Flush();
    }

    public static void ValidateVolumeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            throw new ArgumentException($"Volume names must be 1-{MaxNameLength} characters.");
        if (name.Any(c => c is ':' or '/' || c < ' ' || c > 0xFF))
            throw new ArgumentException("Volume names cannot contain ':' or '/'.");
    }

    internal static void SetHeaderChecksum(Span<byte> block) => AmigaChecksum.SetSumToZero(block, 128, 20);

    private static void PutDate(Span<byte> b, int offset, AmigaDate d)
    {
        Endian.PutU32BE(b, offset, d.Days);
        Endian.PutU32BE(b, offset + 4, d.Minutes);
        Endian.PutU32BE(b, offset + 8, d.Ticks);
    }
}

/// <summary>Read-only validation of an OFS/FFS volume's boot block, root block and bitmap.</summary>
public static class AmigaFfsInspector
{
    public static FilesystemInfo Inspect(IBlockDevice volume)
    {
        var notes = new List<string>();
        var boot = volume.ReadSector(0);
        var dosType = new DosType(Endian.U32BE(boot, 0));
        long blocks = volume.SectorCount();
        long rootBlock = AmigaFfsFormatter.RootBlockFor(blocks);
        if (rootBlock >= blocks)
            return new FilesystemInfo { Kind = FilesystemKind.AmigaDos, DosType = dosType, Notes = ["Partition too small to hold a root block."] };

        var r = volume.ReadSector(rootBlock);
        bool typeOk = Endian.U32BE(r, 0) == 2 && Endian.U32BE(r, 508) == 1;
        bool sumOk = AmigaChecksum.IsSumZero(r, 128);
        if (!typeOk || !sumOk)
        {
            notes.Add(typeOk
                ? "Root block checksum is invalid."
                : $"No root block at the expected location (block {rootBlock}). The partition may use a non-default block size or reserved-block count.");
            return new FilesystemInfo { Kind = FilesystemKind.AmigaDos, DosType = dosType, BytesPerSector = 512, TotalBytes = volume.Length, Notes = notes };
        }

        string name = Endian.BcplString(r, 432, 30);
        bool bitmapValid = Endian.U32BE(r, 312) == 0xFFFFFFFF;
        if (!bitmapValid) notes.Add("Bitmap is flagged invalid; the Amiga will validate the disk when it is next mounted.");

        long free = 0;
        int bitmapErrors = 0;
        var bitmapBlocks = new List<uint>();
        for (int i = 0; i < 25; i++)
        {
            uint p = Endian.U32BE(r, 316 + i * 4);
            if (p != 0) bitmapBlocks.Add(p);
        }
        uint ext = Endian.U32BE(r, 416);
        var seen = new HashSet<uint>();
        while (ext != 0 && ext < blocks && seen.Add(ext))
        {
            var e = volume.ReadSector(ext);
            for (int i = 0; i < 127; i++)
            {
                uint p = Endian.U32BE(e, i * 4);
                if (p != 0) bitmapBlocks.Add(p);
            }
            ext = Endian.U32BE(e, 508);
        }

        long bits = blocks - AmigaFfsFormatter.ReservedBlocks;
        long expectedBitmaps = (bits + AmigaFfsFormatter.BitsPerBitmapBlock - 1) / AmigaFfsFormatter.BitsPerBitmapBlock;
        if (bitmapBlocks.Count != expectedBitmaps)
            notes.Add($"Expected {expectedBitmaps} bitmap blocks but found {bitmapBlocks.Count}.");
        for (int i = 0; i < bitmapBlocks.Count; i++)
        {
            if (bitmapBlocks[i] >= blocks) { bitmapErrors++; continue; }
            var b = volume.ReadSector(bitmapBlocks[i]);
            if (!AmigaChecksum.IsSumZero(b, 128)) bitmapErrors++;
            for (int l = 0; l < 127; l++)
            {
                uint w = Endian.U32BE(b, 4 + l * 4);
                for (int bit = 0; bit < 32; bit++)
                {
                    long n = (long)i * AmigaFfsFormatter.BitsPerBitmapBlock + l * 32L + bit;
                    if (n >= bits) break;
                    if ((w >> bit & 1) != 0) free++;
                }
            }
        }
        if (bitmapErrors > 0) notes.Add($"{bitmapErrors} bitmap block(s) have invalid checksums.");

        return new FilesystemInfo
        {
            Kind = FilesystemKind.AmigaDos,
            DosType = dosType,
            Label = name,
            BytesPerSector = 512,
            TotalBytes = volume.Length,
            FreeBytes = bitmapValid && bitmapErrors == 0 ? free * 512 : null,
            StructureValidated = bitmapValid && bitmapErrors == 0 && bitmapBlocks.Count == expectedBitmaps,
            Notes = notes,
        };
    }

    /// <summary>Lists names in the root directory hash table (used for verification and reports).</summary>
    public static IReadOnlyList<string> ListRoot(IBlockDevice volume)
    {
        long blocks = volume.SectorCount();
        var r = volume.ReadSector(AmigaFfsFormatter.RootBlockFor(blocks));
        var names = new List<string>();
        for (int i = 0; i < AmigaFfsFormatter.HashTableSize; i++)
        {
            uint blk = Endian.U32BE(r, 24 + i * 4);
            var seen = new HashSet<uint>();
            while (blk != 0 && blk < blocks && seen.Add(blk))
            {
                var h = volume.ReadSector(blk);
                names.Add(Encoding.Latin1.GetString(h, 433, Math.Min((int)h[432], 30)));
                blk = Endian.U32BE(h, 496);
            }
        }
        return names;
    }
}
