using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Partitioning;

/// <summary>
/// Amiga Rigid Disk Block structures (devices/hardblocks.h). All fields are big-endian
/// and every block carries a "sum to zero" checksum over SummedLongs longs.
/// </summary>
public static class RdbIds
{
    public static readonly uint Rdsk = Endian.FourCC("RDSK");
    public static readonly uint Part = Endian.FourCC("PART");
    public static readonly uint Fshd = Endian.FourCC("FSHD");
    public static readonly uint Lseg = Endian.FourCC("LSEG");
    public static readonly uint Badb = Endian.FourCC("BADB");
    public const uint EndOfList = 0xFFFFFFFF;

    /// <summary>The RDSK block must be located in the first 16 blocks of the disk.</summary>
    public const int SearchLimit = 16;
}

public sealed record RdbGeometry(uint Cylinders, uint Heads, uint Sectors)
{
    public uint CylinderBlocks => Heads * Sectors;
    public long TotalBlocks => (long)Cylinders * CylinderBlocks;

    /// <summary>
    /// Geometry used for SD/CF media: these are LBA devices so CHS values are purely a
    /// bookkeeping unit. 16 heads x 63 sectors (504 KiB cylinders) is what HDToolBox-style
    /// tools commonly pick for flash media and keeps partition sizes fine grained.
    /// </summary>
    public static RdbGeometry ForBlockCount(long blocks, uint heads = 16, uint sectors = 63)
    {
        long cyl = blocks / (heads * sectors);
        if (cyl < 4) throw new ArgumentOutOfRangeException(nameof(blocks), "Device too small for an RDB layout.");
        return new RdbGeometry((uint)Math.Min(cyl, uint.MaxValue), heads, sectors);
    }
}

public sealed class RigidDiskBlock
{
    public int Location { get; init; }
    public uint BlockBytes { get; init; } = 512;
    public uint Flags { get; init; }
    public uint HostId { get; init; } = 7;
    public uint BadBlockList { get; init; } = RdbIds.EndOfList;
    public uint PartitionList { get; init; } = RdbIds.EndOfList;
    public uint FileSysHeaderList { get; init; } = RdbIds.EndOfList;
    public uint DriveInit { get; init; } = RdbIds.EndOfList;
    public required RdbGeometry Geometry { get; init; }
    public uint RdbBlocksLo { get; init; }
    public uint RdbBlocksHi { get; init; }
    public uint LoCylinder { get; init; }
    public uint HiCylinder { get; init; }
    public uint HighRdskBlock { get; init; }
    public string DiskVendor { get; init; } = "";
    public string DiskProduct { get; init; } = "";
    public string DiskRevision { get; init; } = "";
    public bool ChecksumValid { get; init; } = true;

    internal byte[] ToBlock()
    {
        var b = new byte[BlockBytes];
        b.AsSpan(0, 256).Fill(0);
        Endian.PutU32BE(b, 0, RdbIds.Rdsk);
        Endian.PutU32BE(b, 4, 64);
        Endian.PutU32BE(b, 12, HostId);
        Endian.PutU32BE(b, 16, BlockBytes);
        Endian.PutU32BE(b, 20, Flags);
        Endian.PutU32BE(b, 24, BadBlockList);
        Endian.PutU32BE(b, 28, PartitionList);
        Endian.PutU32BE(b, 32, FileSysHeaderList);
        Endian.PutU32BE(b, 36, DriveInit);
        for (int o = 40; o < 64; o += 4) Endian.PutU32BE(b, o, 0xFFFFFFFF); // rdb_Reserved1[6]
        Endian.PutU32BE(b, 64, Geometry.Cylinders);
        Endian.PutU32BE(b, 68, Geometry.Sectors);
        Endian.PutU32BE(b, 72, Geometry.Heads);
        Endian.PutU32BE(b, 76, 1);                     // interleave
        Endian.PutU32BE(b, 80, Geometry.Cylinders);    // park
        Endian.PutU32BE(b, 96, Geometry.Cylinders);    // write precomp
        Endian.PutU32BE(b, 100, Geometry.Cylinders);   // reduced write
        Endian.PutU32BE(b, 104, 3);                    // step rate
        Endian.PutU32BE(b, 128, RdbBlocksLo);
        Endian.PutU32BE(b, 132, RdbBlocksHi);
        Endian.PutU32BE(b, 136, LoCylinder);
        Endian.PutU32BE(b, 140, HiCylinder);
        Endian.PutU32BE(b, 144, Geometry.CylinderBlocks);
        Endian.PutU32BE(b, 152, HighRdskBlock);
        PutFixed(b, 160, 8, DiskVendor);
        PutFixed(b, 168, 16, DiskProduct);
        PutFixed(b, 184, 4, DiskRevision);
        AmigaChecksum.SetSumToZero(b, 64, 8);
        return b;
    }

    internal static RigidDiskBlock Parse(ReadOnlySpan<byte> b, int location) => new()
    {
        Location = location,
        HostId = Endian.U32BE(b, 12),
        BlockBytes = Endian.U32BE(b, 16),
        Flags = Endian.U32BE(b, 20),
        BadBlockList = Endian.U32BE(b, 24),
        PartitionList = Endian.U32BE(b, 28),
        FileSysHeaderList = Endian.U32BE(b, 32),
        DriveInit = Endian.U32BE(b, 36),
        Geometry = new RdbGeometry(Endian.U32BE(b, 64), Endian.U32BE(b, 72), Endian.U32BE(b, 68)),
        RdbBlocksLo = Endian.U32BE(b, 128),
        RdbBlocksHi = Endian.U32BE(b, 132),
        LoCylinder = Endian.U32BE(b, 136),
        HiCylinder = Endian.U32BE(b, 140),
        HighRdskBlock = Endian.U32BE(b, 152),
        DiskVendor = Endian.Ascii(b, 160, 8),
        DiskProduct = Endian.Ascii(b, 168, 16),
        DiskRevision = Endian.Ascii(b, 184, 4),
        ChecksumValid = AmigaChecksum.IsSumZero(b, (int)Math.Min(Endian.U32BE(b, 4), 128)),
    };

    private static void PutFixed(Span<byte> b, int offset, int len, string s)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        b.Slice(offset, len).Fill((byte)' ');
        bytes.AsSpan(0, Math.Min(len, bytes.Length)).CopyTo(b[offset..]);
    }
}

public sealed class RdbPartition
{
    public const uint FlagBootable = 1;
    public const uint FlagNoMount = 2;

    public int Block { get; init; }
    public required string DriveName { get; init; }
    public uint Flags { get; init; }
    public uint Next { get; init; } = RdbIds.EndOfList;
    public uint HostId { get; init; } = 7;
    public uint SizeBlockLongs { get; init; } = 128;
    public uint Surfaces { get; init; }
    public uint SectorsPerBlock { get; init; } = 1;
    public uint BlocksPerTrack { get; init; }
    public uint Reserved { get; init; } = 2;
    public uint PreAlloc { get; init; }
    public uint Interleave { get; init; }
    public uint LowCyl { get; init; }
    public uint HighCyl { get; init; }
    public uint NumBuffers { get; init; } = 30;
    public uint BufMemType { get; init; }
    public uint MaxTransfer { get; init; } = 0x1FE00;
    public uint Mask { get; init; } = 0x7FFFFFFE;
    public int BootPri { get; init; }
    public DosType DosType { get; init; }
    public bool ChecksumValid { get; init; } = true;

    public bool IsBootable => (Flags & FlagBootable) != 0;
    public bool IsNoMount => (Flags & FlagNoMount) != 0;
    public long FileSystemBlockBytes => SizeBlockLongs * 4L * SectorsPerBlock;
    public long CylinderBytes => (long)Surfaces * BlocksPerTrack * SizeBlockLongs * 4;
    public long StartByte => LowCyl * CylinderBytes;
    public long EndByteExclusive => (HighCyl + 1L) * CylinderBytes;
    public long ByteLength => EndByteExclusive - StartByte;

    /// <summary>Number of filesystem blocks in the partition (512-byte sectors when SectorsPerBlock = 1).</summary>
    public long FileSystemBlocks => ByteLength / FileSystemBlockBytes;

    internal byte[] ToBlock(int blockBytes)
    {
        var b = new byte[blockBytes];
        Endian.PutU32BE(b, 0, RdbIds.Part);
        Endian.PutU32BE(b, 4, 64);
        Endian.PutU32BE(b, 12, HostId);
        Endian.PutU32BE(b, 16, Next);
        Endian.PutU32BE(b, 20, Flags);
        Endian.PutU32BE(b, 24, 0xFFFFFFFF);
        Endian.PutU32BE(b, 28, 0xFFFFFFFF);
        Endian.PutBcplString(b, 36, DriveName, 30);
        for (int o = 68; o < 128; o += 4) Endian.PutU32BE(b, o, 0xFFFFFFFF); // pb_Reserved2[15]
        int e = 128;
        uint[] env =
        [
            16, SizeBlockLongs, 0, Surfaces, SectorsPerBlock, BlocksPerTrack, Reserved, PreAlloc,
            Interleave, LowCyl, HighCyl, NumBuffers, BufMemType, MaxTransfer, Mask, unchecked((uint)BootPri), DosType.Value,
        ];
        for (int i = 0; i < env.Length; i++) Endian.PutU32BE(b, e + i * 4, env[i]);
        AmigaChecksum.SetSumToZero(b, 64, 8);
        return b;
    }

    internal static RdbPartition Parse(ReadOnlySpan<byte> b, int block)
    {
        uint table = Math.Min(Endian.U32BE(b, 128), 19);
        var env = new uint[20];
        for (int i = 0; i <= table; i++) env[i] = Endian.U32BE(b, 128 + i * 4);
        uint Env(int i) => env[i];
        return new RdbPartition
        {
            Block = block,
            HostId = Endian.U32BE(b, 12),
            Next = Endian.U32BE(b, 16),
            Flags = Endian.U32BE(b, 20),
            DriveName = Endian.BcplString(b, 36, 31),
            SizeBlockLongs = Env(1),
            Surfaces = Env(3),
            SectorsPerBlock = Math.Max(1, Env(4)),
            BlocksPerTrack = Env(5),
            Reserved = Env(6),
            PreAlloc = Env(7),
            Interleave = Env(8),
            LowCyl = Env(9),
            HighCyl = Env(10),
            NumBuffers = Env(11),
            BufMemType = Env(12),
            MaxTransfer = Env(13),
            Mask = Env(14),
            BootPri = unchecked((int)Env(15)),
            DosType = new DosType(table >= 16 ? Env(16) : DosType.Ofs.Value),
            ChecksumValid = AmigaChecksum.IsSumZero(b, (int)Math.Min(Endian.U32BE(b, 4), 128)),
        };
    }
}

public sealed class RdbFileSystem
{
    public const uint PatchSegList = 0x80;
    public const uint PatchGlobalVec = 0x100;

    public int Block { get; init; }
    public DosType DosType { get; init; }
    public uint Version { get; init; }
    public uint PatchFlags { get; init; }
    public uint StackSize { get; init; }
    public int Priority { get; init; }
    public uint SegListBlock { get; init; } = RdbIds.EndOfList;
    public uint GlobalVec { get; init; } = 0xFFFFFFFF;
    public uint Next { get; init; } = RdbIds.EndOfList;
    public int LoadSegBlockCount { get; init; }
    public bool ChecksumValid { get; init; } = true;
    public string VersionText => $"{Version >> 16}.{Version & 0xFFFF}";

    internal byte[] ToBlock(int blockBytes)
    {
        var b = new byte[blockBytes];
        Endian.PutU32BE(b, 0, RdbIds.Fshd);
        Endian.PutU32BE(b, 4, 64);
        Endian.PutU32BE(b, 12, 7);
        Endian.PutU32BE(b, 16, Next);
        Endian.PutU32BE(b, 32, DosType.Value);
        Endian.PutU32BE(b, 36, Version);
        Endian.PutU32BE(b, 40, PatchFlags);
        Endian.PutU32BE(b, 60, StackSize);
        Endian.PutI32BE(b, 64, Priority);
        Endian.PutU32BE(b, 72, SegListBlock);
        Endian.PutU32BE(b, 76, GlobalVec);
        AmigaChecksum.SetSumToZero(b, 64, 8);
        return b;
    }

    internal static RdbFileSystem Parse(ReadOnlySpan<byte> b, int block, int lsegCount) => new()
    {
        Block = block,
        Next = Endian.U32BE(b, 16),
        DosType = new DosType(Endian.U32BE(b, 32)),
        Version = Endian.U32BE(b, 36),
        PatchFlags = Endian.U32BE(b, 40),
        StackSize = Endian.U32BE(b, 60),
        Priority = Endian.I32BE(b, 64),
        SegListBlock = Endian.U32BE(b, 72),
        GlobalVec = Endian.U32BE(b, 76),
        LoadSegBlockCount = lsegCount,
        ChecksumValid = AmigaChecksum.IsSumZero(b, (int)Math.Min(Endian.U32BE(b, 4), 128)),
    };
}

/// <summary>Result of reading an RDB from a device.</summary>
public sealed class RdbDisk
{
    public required RigidDiskBlock Rdsk { get; init; }
    public IReadOnlyList<RdbPartition> Partitions { get; init; } = [];
    public IReadOnlyList<RdbFileSystem> FileSystems { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
}
