using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Partitioning;

public sealed record RdbPartitionSpec(
    string DriveName,
    uint LowCyl,
    uint HighCyl,
    DosType DosType,
    bool Bootable = false,
    int BootPri = 0,
    bool NoMount = false,
    uint NumBuffers = 30,
    uint MaxTransfer = 0x1FE00,
    uint Mask = 0x7FFFFFFE,
    uint Reserved = 2);

/// <summary>A filesystem handler binary (AmigaDOS hunk executable) to store in the RDB.</summary>
public sealed record RdbFileSystemSpec(DosType DosType, uint Version, byte[] Binary, uint StackSize = 0, int Priority = 0);

public sealed record RdbLayoutSpec(
    RdbGeometry Geometry,
    uint RdbCylinders,
    IReadOnlyList<RdbPartitionSpec> Partitions,
    IReadOnlyList<RdbFileSystemSpec> FileSystems,
    string DiskVendor = "ASDC",
    string DiskProduct = "SD Card",
    string DiskRevision = "1.0");

/// <summary>
/// Writes a Rigid Disk Block the same way HDToolBox does: RDSK at block 0, PART blocks
/// following it, then FSHD/LSEG blocks for any embedded filesystems. Partitions start at
/// <see cref="RdbLayoutSpec.RdbCylinders"/> and are cylinder aligned.
/// </summary>
public static class RdbWriter
{
    private const int LsegDataBytes = 123 * 4;
    private const uint RdskFlags = 0x17; // LAST | LASTLUN | LASTTID | DISKID

    public static void Validate(RdbLayoutSpec spec, long deviceSectors)
    {
        var g = spec.Geometry;
        if (g.TotalBlocks > deviceSectors) throw new InvalidOperationException("RDB geometry is larger than the device.");
        if (spec.RdbCylinders < 1) throw new InvalidOperationException("At least one cylinder must be reserved for the RDB.");
        if (spec.Partitions.Count == 0) throw new InvalidOperationException("At least one partition is required.");

        uint expectedLow = spec.RdbCylinders;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in spec.Partitions.OrderBy(p => p.LowCyl))
        {
            if (!IsValidDriveName(p.DriveName)) throw new InvalidOperationException($"'{p.DriveName}' is not a valid AmigaDOS device name.");
            if (!names.Add(p.DriveName)) throw new InvalidOperationException($"Device name '{p.DriveName}' is used twice.");
            if (p.LowCyl < spec.RdbCylinders) throw new InvalidOperationException($"Partition {p.DriveName} overlaps the RDB area.");
            if (p.LowCyl < expectedLow) throw new InvalidOperationException($"Partition {p.DriveName} overlaps the previous partition.");
            if (p.HighCyl < p.LowCyl || p.HighCyl >= g.Cylinders) throw new InvalidOperationException($"Partition {p.DriveName} has an invalid cylinder range.");
            expectedLow = p.HighCyl + 1;
        }

        long needed = 1 + spec.Partitions.Count + spec.FileSystems.Sum(f => 1 + LoadSegBlocksFor(f.Binary.Length));
        if (needed > (long)spec.RdbCylinders * g.CylinderBlocks)
            throw new InvalidOperationException("The reserved RDB area is too small for the partition and filesystem blocks.");
        foreach (var fs in spec.FileSystems)
        {
            if (!HunkFile.IsHunkExecutable(fs.Binary)) throw new InvalidOperationException($"Filesystem for {fs.DosType} is not an AmigaDOS executable.");
        }
    }

    /// <summary>AmigaDOS device names: 1-30 printable characters without ':' or '/'.</summary>
    public static bool IsValidDriveName(string name) =>
        name.Length is >= 1 and <= 30 && name.All(c => c > ' ' && c < 0x7F && c != ':' && c != '/');

    public static int LoadSegBlocksFor(int binaryLength) => (binaryLength + LsegDataBytes - 1) / LsegDataBytes;

    /// <summary>Writes the RDB. The reserved RDB cylinders are zeroed first.</summary>
    public static RigidDiskBlock Write(IBlockDevice device, RdbLayoutSpec spec)
    {
        if (device.SectorSize != 512) throw new NotSupportedException("RDB layouts are only supported on 512-byte sector media.");
        Validate(spec, device.SectorCount());
        var g = spec.Geometry;
        int bs = device.SectorSize;
        uint rdbAreaBlocks = spec.RdbCylinders * g.CylinderBlocks;
        device.Zero(0, (long)rdbAreaBlocks * bs);

        var parts = spec.Partitions.OrderBy(p => p.LowCyl).ToList();
        uint nextBlock = 1;
        var partBlocks = parts.Select(_ => nextBlock++).ToList();
        var fsBlocks = new List<(uint header, List<uint> segs)>();
        foreach (var fs in spec.FileSystems)
        {
            uint header = nextBlock++;
            var segs = Enumerable.Range(0, LoadSegBlocksFor(fs.Binary.Length)).Select(_ => nextBlock++).ToList();
            fsBlocks.Add((header, segs));
        }

        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var block = new RdbPartition
            {
                DriveName = p.DriveName,
                Next = i + 1 < parts.Count ? partBlocks[i + 1] : RdbIds.EndOfList,
                Flags = (p.Bootable ? RdbPartition.FlagBootable : 0) | (p.NoMount ? RdbPartition.FlagNoMount : 0),
                Surfaces = g.Heads,
                BlocksPerTrack = g.Sectors,
                Reserved = p.Reserved,
                LowCyl = p.LowCyl,
                HighCyl = p.HighCyl,
                NumBuffers = p.NumBuffers,
                MaxTransfer = p.MaxTransfer,
                Mask = p.Mask,
                BootPri = p.BootPri,
                DosType = p.DosType,
            }.ToBlock(bs);
            device.WriteSector(partBlocks[i], block);
        }

        for (int i = 0; i < spec.FileSystems.Count; i++)
        {
            var fs = spec.FileSystems[i];
            var (header, segs) = fsBlocks[i];
            var fshd = new RdbFileSystem
            {
                DosType = fs.DosType,
                Version = fs.Version,
                PatchFlags = RdbFileSystem.PatchSegList | RdbFileSystem.PatchGlobalVec,
                StackSize = fs.StackSize,
                Priority = fs.Priority,
                SegListBlock = segs[0],
                GlobalVec = 0xFFFFFFFF,
                Next = i + 1 < fsBlocks.Count ? fsBlocks[i + 1].header : RdbIds.EndOfList,
            }.ToBlock(bs);
            device.WriteSector(header, fshd);

            for (int s = 0; s < segs.Count; s++)
            {
                var b = new byte[bs];
                Endian.PutU32BE(b, 0, RdbIds.Lseg);
                Endian.PutU32BE(b, 4, 128);
                Endian.PutU32BE(b, 12, 7);
                Endian.PutU32BE(b, 16, s + 1 < segs.Count ? segs[s + 1] : RdbIds.EndOfList);
                int offset = s * LsegDataBytes;
                int len = Math.Min(LsegDataBytes, fs.Binary.Length - offset);
                fs.Binary.AsSpan(offset, len).CopyTo(b.AsSpan(20));
                AmigaChecksum.SetSumToZero(b, 128, 8);
                device.WriteSector(segs[s], b);
            }
        }

        var rdsk = new RigidDiskBlock
        {
            Location = 0,
            Flags = RdskFlags,
            PartitionList = partBlocks.Count > 0 ? partBlocks[0] : RdbIds.EndOfList,
            FileSysHeaderList = fsBlocks.Count > 0 ? fsBlocks[0].header : RdbIds.EndOfList,
            Geometry = g,
            RdbBlocksLo = 0,
            RdbBlocksHi = rdbAreaBlocks - 1,
            LoCylinder = spec.RdbCylinders,
            HiCylinder = g.Cylinders - 1,
            HighRdskBlock = nextBlock - 1,
            DiskVendor = spec.DiskVendor,
            DiskProduct = spec.DiskProduct,
            DiskRevision = spec.DiskRevision,
        };
        device.WriteSector(0, rdsk.ToBlock());
        return rdsk;
    }
}
