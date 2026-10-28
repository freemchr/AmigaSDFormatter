using System.Text;
using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.FileSystems;

public sealed record Fat32FormatOptions
{
    /// <summary>Volume label (max 11 characters, upper-case). Apollo's FAQ recommends "SD0".</summary>
    public string Label { get; init; } = "NO NAME";
    public uint? VolumeId { get; init; }
    /// <summary>Override the cluster size in bytes; null uses the standard size table.</summary>
    public int? ClusterBytes { get; init; }
    /// <summary>Value stored in BPB_HiddSec: the partition's starting LBA on the disk.</summary>
    public uint HiddenSectors { get; init; }
}

/// <summary>
/// Creates a FAT32 filesystem per the Microsoft FAT specification (the same layout Rufus /
/// fat32format produce: 32 reserved sectors, 2 FATs, FSInfo at 1, backup boot at 6).
/// This is used instead of the Windows format API so that the result is deterministic and
/// identical whether written to an image file or a card.
/// </summary>
public static class Fat32Formatter
{
    public const int ReservedSectors = 32;
    public const int MinClusters = 65525;
    private const uint MaxClusters = 0x0FFFFFF5;

    /// <summary>Default cluster size table used by fat32format / Rufus for 512-byte sectors.</summary>
    public static int DefaultClusterBytes(long volumeBytes) => volumeBytes switch
    {
        < 64L << 20 => 512,
        < 128L << 20 => 1024,
        < 256L << 20 => 2048,
        < 8L << 30 => 4096,
        < 16L << 30 => 8192,
        < 32L << 30 => 16384,
        _ => 32768,
    };

    /// <summary>
    /// The default cluster size, reduced where needed so the volume still has the 65,525 clusters
    /// FAT32 requires (volumes just above a table boundary would otherwise fall short).
    /// </summary>
    public static int ChooseClusterBytes(long volumeBytes)
    {
        int c = DefaultClusterBytes(volumeBytes);
        while (c > 512 && Geometry(volumeBytes / 512, c / 512).Clusters < MinClusters) c /= 2;
        return c;
    }

    /// <summary>FAT size calculation from the Microsoft specification (fatgen103, "FAT Type Determination").</summary>
    private static (long FatSectors, long Clusters) Geometry(long totalSectors, int spc)
    {
        long tmp1 = totalSectors - ReservedSectors;
        long tmp2 = (256L * spc + 2) / 2;
        long fatSectors = (tmp1 + tmp2 - 1) / tmp2;
        return (fatSectors, (totalSectors - ReservedSectors - 2 * fatSectors) / spc);
    }

    public static void Format(IBlockDevice volume, Fat32FormatOptions options, CancellationToken cancellationToken = default)
    {
        int bps = volume.SectorSize;
        if (bps != 512) throw new NotSupportedException("Only 512-byte sectors are supported.");
        long totalSectors = volume.SectorCount();
        if (totalSectors > uint.MaxValue) throw new NotSupportedException("FAT32 volumes are limited to 2 TiB with 512-byte sectors.");

        int clusterBytes = options.ClusterBytes ?? ChooseClusterBytes(volume.Length);
        if (clusterBytes < bps || clusterBytes > 65536 || (clusterBytes & (clusterBytes - 1)) != 0)
            throw new ArgumentException("Cluster size must be a power of two between 512 bytes and 64 KiB.");
        int spc = clusterBytes / bps;

        var (fatSectors, clusters) = Geometry(totalSectors, spc);
        if (clusters < MinClusters)
            throw new InvalidOperationException($"Volume too small for FAT32 with {clusterBytes}-byte clusters ({clusters} clusters).");
        if (clusters > MaxClusters) throw new InvalidOperationException("Too many clusters for FAT32; use a larger cluster size.");

        string label = NormaliseLabel(options.Label);
        uint volumeId = options.VolumeId ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);

        var boot = BuildBootSector(totalSectors, spc, fatSectors, label, volumeId, options.HiddenSectors);
        var fsinfo = BuildFsInfo((uint)(clusters - 1), 3);
        var third = new byte[bps];
        third[510] = 0x55;
        third[511] = 0xAA;

        // Clear the reserved area, both FATs and the root directory cluster.
        long systemSectors = ReservedSectors + 2 * fatSectors;
        volume.Zero(0, (systemSectors + spc) * bps, cancellationToken);

        volume.WriteSector(0, boot);
        volume.WriteSector(1, fsinfo);
        volume.WriteSector(2, third);
        volume.WriteSector(6, boot);
        volume.WriteSector(7, fsinfo);
        volume.WriteSector(8, third);

        var fatStart = new byte[bps];
        Endian.PutU32LE(fatStart, 0, 0x0FFFFFF8); // media descriptor 0xF8
        Endian.PutU32LE(fatStart, 4, 0x0FFFFFFF);
        Endian.PutU32LE(fatStart, 8, 0x0FFFFFFF); // root directory cluster: end of chain
        volume.WriteSector(ReservedSectors, fatStart);
        volume.WriteSector(ReservedSectors + fatSectors, fatStart);

        // Root directory with a volume-label entry, so the label is visible to all FAT drivers.
        var root = new byte[bps];
        Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(root, 0);
        root[11] = 0x08;
        volume.WriteSector(systemSectors, root);
        volume.Flush();
    }

    public static string NormaliseLabel(string label)
    {
        var s = (label ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0) return "NO NAME";
        if (s.Length > 11) throw new ArgumentException("FAT volume labels are limited to 11 characters.");
        foreach (char c in s)
        {
            if (c < 0x20 || c > 0x7E || "\"*+,./:;<=>?[\\]|".Contains(c))
                throw new ArgumentException($"Character '{c}' is not allowed in a FAT volume label.");
        }
        return s;
    }

    private static byte[] BuildBootSector(long totalSectors, int spc, long fatSectors, string label, uint volumeId, uint hidden)
    {
        var b = new byte[512];
        b[0] = 0xEB; b[1] = 0x58; b[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(b, 3);
        Endian.PutU16LE(b, 11, 512);
        b[13] = (byte)spc;
        Endian.PutU16LE(b, 14, ReservedSectors);
        b[16] = 2;                             // number of FATs
        Endian.PutU16LE(b, 17, 0);             // root entries (FAT32: 0)
        Endian.PutU16LE(b, 19, 0);             // total sectors 16
        b[21] = 0xF8;                          // media: fixed disk
        Endian.PutU16LE(b, 22, 0);             // FAT size 16
        Endian.PutU16LE(b, 24, 63);            // sectors per track
        Endian.PutU16LE(b, 26, 255);           // heads
        Endian.PutU32LE(b, 28, hidden);
        Endian.PutU32LE(b, 32, (uint)totalSectors);
        Endian.PutU32LE(b, 36, (uint)fatSectors);
        Endian.PutU16LE(b, 40, 0);             // ext flags: mirroring on
        Endian.PutU16LE(b, 42, 0);             // version 0.0
        Endian.PutU32LE(b, 44, 2);             // root cluster
        Endian.PutU16LE(b, 48, 1);             // FSInfo sector
        Endian.PutU16LE(b, 50, 6);             // backup boot sector
        b[64] = 0x80;                          // drive number
        b[66] = 0x29;                          // extended boot signature
        Endian.PutU32LE(b, 67, volumeId);
        Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(b, 71);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(b, 82);
        // Non-bootable stub: "int 18h" (boot failure -> BIOS tries the next device).
        b[90] = 0xCD; b[91] = 0x18;
        b[510] = 0x55; b[511] = 0xAA;
        return b;
    }

    private static byte[] BuildFsInfo(uint freeClusters, uint nextFree)
    {
        var b = new byte[512];
        Endian.PutU32LE(b, 0, 0x41615252);
        Endian.PutU32LE(b, 484, 0x61417272);
        Endian.PutU32LE(b, 488, freeClusters);
        Endian.PutU32LE(b, 492, nextFree);
        Endian.PutU32LE(b, 508, 0xAA550000);
        return b;
    }
}
