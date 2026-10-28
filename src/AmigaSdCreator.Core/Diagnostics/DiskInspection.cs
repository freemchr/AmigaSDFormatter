using System.Text;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.Diagnostics;

public enum PartitionScheme
{
    Blank,
    Mbr,
    Gpt,
    Rdb,
    /// <summary>A filesystem starting at sector 0 without any partition table ("superfloppy" / RDB-less HDF).</summary>
    Unpartitioned,
    Unknown,
}

public sealed record InspectedPartition
{
    public required int Index { get; init; }
    public required string Table { get; init; }
    public required string Name { get; init; }
    public required long StartByte { get; init; }
    public required long LengthBytes { get; init; }
    public required string TypeDescription { get; init; }
    public required FilesystemInfo Filesystem { get; init; }
    public bool Bootable { get; init; }
    public int? BootPriority { get; init; }
    public DosType? DosType { get; init; }
    public uint? MaxTransfer { get; init; }
    public uint? Mask { get; init; }
    public bool NoMount { get; init; }
    public long EndByteExclusive => StartByte + LengthBytes;
}

public sealed record DiskInspection
{
    public required string DeviceDescription { get; init; }
    public required long LengthBytes { get; init; }
    public required int SectorSize { get; init; }
    public required PartitionScheme Scheme { get; init; }
    public MbrPartitionTable? Mbr { get; init; }
    public GptPartitionTable? Gpt { get; init; }
    public RdbDisk? Rdb { get; init; }
    public bool HasBackupGptHeader { get; init; }
    public IReadOnlyList<InspectedPartition> Partitions { get; init; } = [];

    /// <summary>Set when both an MBR (or FAT boot sector) and an RDSK block are present in the first 16 sectors.</summary>
    public bool HasConflictingRdbAndMbr { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    public DateTime InspectedUtc { get; init; } = DateTime.UtcNow;

    public string SchemeDescription => Scheme switch
    {
        PartitionScheme.Blank => "None (blank)",
        PartitionScheme.Mbr => "MBR (PC)",
        PartitionScheme.Gpt => "GPT",
        PartitionScheme.Rdb => "Amiga RDB",
        PartitionScheme.Unpartitioned => "None (filesystem starts at sector 0)",
        _ => "Unknown",
    };
}

/// <summary>Reads partition tables and probes each partition's filesystem. Never writes.</summary>
public static class DiskInspector
{
    public static DiskInspection Inspect(IBlockDevice device)
    {
        var problems = new List<string>();
        long sectors = device.SectorCount();
        if (sectors < 16)
        {
            return new DiskInspection
            {
                DeviceDescription = device.Description, LengthBytes = device.Length, SectorSize = device.SectorSize,
                Scheme = PartitionScheme.Unknown, Problems = ["Device is too small to contain a partition table."],
            };
        }

        var s0 = device.ReadSector(0);
        var partitions = new List<InspectedPartition>();
        var rdb = RdbReader.TryRead(device);
        var mbr = MbrPartitionTable.TryParse(s0, sectors);
        var fatAtZero = FatBootSector.TryParse(s0);
        GptPartitionTable? gpt = null;
        if (mbr?.IsGptProtective == true) gpt = GptPartitionTable.TryRead(device);
        bool backupGpt = HasBackupGpt(device);

        bool conflicting = rdb is not null && rdb.Rdsk.Location > 0 && (mbr is not null || fatAtZero is not null);
        if (rdb is not null) problems.AddRange(rdb.Problems);

        PartitionScheme scheme;
        if (rdb is not null && !conflicting)
        {
            scheme = PartitionScheme.Rdb;
        }
        else if (gpt is not null)
        {
            scheme = PartitionScheme.Gpt;
        }
        else if (mbr is not null)
        {
            scheme = PartitionScheme.Mbr;
        }
        else if (fatAtZero is not null || new DosType(Endian.U32BE(s0, 0)) is var d && (d.IsDosFamily || d.IsPfs || d.IsSfs)
                 || Encoding.ASCII.GetString(s0, 3, 8) is "EXFAT   " or "NTFS    ")
        {
            scheme = PartitionScheme.Unpartitioned;
        }
        else if (IsBlank(device))
        {
            scheme = PartitionScheme.Blank;
        }
        else if (rdb is not null)
        {
            scheme = PartitionScheme.Rdb;
        }
        else
        {
            scheme = PartitionScheme.Unknown;
        }

        // When the RDB conflicts with a PC structure we still report the PC view as primary,
        // since that is what Windows uses, and the RDB separately in Rdb.
        switch (scheme)
        {
            case PartitionScheme.Rdb:
                foreach (var p in rdb!.Partitions) partitions.Add(FromRdb(device, p, partitions.Count + 1, problems));
                break;
            case PartitionScheme.Gpt:
                foreach (var e in gpt!.Entries)
                {
                    long start = (long)e.FirstLba * device.SectorSize;
                    long len = ((long)e.LastLba - (long)e.FirstLba + 1) * device.SectorSize;
                    partitions.Add(new InspectedPartition
                    {
                        Index = e.Index, Table = "GPT", Name = string.IsNullOrEmpty(e.Name) ? $"Partition {e.Index}" : e.Name,
                        StartByte = start, LengthBytes = len, TypeDescription = e.TypeName,
                        Filesystem = ProbeRange(device, start, len, problems),
                    });
                }
                break;
            case PartitionScheme.Mbr:
                foreach (var e in mbr!.UsedEntries)
                {
                    long start = (long)e.FirstLba * device.SectorSize;
                    long len = (long)e.SectorCount * device.SectorSize;
                    if (e.Type == MbrPartitionTypes.AmigaRdb && start + len <= device.Length)
                    {
                        // PiStorm/Emu68 style: an RDB inside an MBR partition. List its Amiga partitions.
                        using var region = new SubBlockDevice(device, start, len);
                        var inner = Inspect(region);
                        problems.AddRange(inner.Problems.Select(p => $"0x76 partition: {p}"));
                        if (inner.Partitions.Count == 0)
                            partitions.Add(new InspectedPartition { Index = e.Index, Table = "MBR", Name = $"Partition {e.Index}", StartByte = start, LengthBytes = len,
                                TypeDescription = e.TypeName, Filesystem = new FileSystems.FilesystemInfo { Kind = FileSystems.FilesystemKind.Unknown, Notes = ["No RDB inside this Amiga partition."] } });
                        foreach (var ip in inner.Partitions)
                            partitions.Add(ip with { Index = partitions.Count + 1, Table = "RDB in 0x76", StartByte = start + ip.StartByte });
                        continue;
                    }
                    partitions.Add(new InspectedPartition
                    {
                        Index = e.Index, Table = "MBR", Name = $"Partition {e.Index}",
                        StartByte = start, LengthBytes = len, TypeDescription = e.TypeName,
                        Bootable = e.IsActive,
                        Filesystem = ProbeRange(device, start, len, problems),
                    });
                }
                if (mbr.UsedEntries.Any(e => e.Type is MbrPartitionTypes.Extended or MbrPartitionTypes.ExtendedLba))
                    problems.Add("Extended (logical) partitions are present; logical partitions are not inspected.");
                break;
            case PartitionScheme.Unpartitioned:
                partitions.Add(new InspectedPartition
                {
                    Index = 1, Table = "None", Name = "Whole device", StartByte = 0, LengthBytes = device.Length,
                    TypeDescription = "Filesystem at sector 0", Filesystem = FilesystemProbe.Probe(device),
                });
                break;
        }

        return new DiskInspection
        {
            DeviceDescription = device.Description,
            LengthBytes = device.Length,
            SectorSize = device.SectorSize,
            Scheme = scheme,
            Mbr = mbr,
            Gpt = gpt,
            Rdb = rdb,
            HasBackupGptHeader = backupGpt,
            HasConflictingRdbAndMbr = conflicting,
            Partitions = partitions,
            Problems = problems,
        };
    }

    private static InspectedPartition FromRdb(IBlockDevice device, RdbPartition p, int index, List<string> problems)
    {
        FilesystemInfo fs;
        if (p.CylinderBytes == 0 || p.ByteLength <= 0 || p.EndByteExclusive > device.Length)
        {
            fs = new FilesystemInfo { Kind = FilesystemKind.Unknown, Notes = ["Partition geometry is invalid; filesystem not probed."] };
        }
        else if (p.FileSystemBlockBytes != device.SectorSize)
        {
            // Large-block partitions (e.g. 4 KiB FFS/PFS blocks) are identified by signature only.
            var first = device.ReadSector(p.StartByte / device.SectorSize);
            var dt = new DosType(Endian.U32BE(first, 0));
            fs = new FilesystemInfo
            {
                Kind = dt.IsDosFamily ? FilesystemKind.AmigaDos : dt.IsPfs ? FilesystemKind.Pfs : dt.IsSfs ? FilesystemKind.Sfs : FilesystemKind.Unknown,
                DosType = dt.IsDosFamily || dt.IsPfs || dt.IsSfs ? dt : null,
                Notes = [$"Partition uses {p.FileSystemBlockBytes}-byte filesystem blocks; only the signature was checked."],
            };
        }
        else
        {
            fs = ProbeRange(device, p.StartByte, p.ByteLength, problems);
        }

        return new InspectedPartition
        {
            Index = index,
            Table = "RDB",
            Name = p.DriveName,
            StartByte = p.StartByte,
            LengthBytes = p.ByteLength,
            TypeDescription = p.DosType.FriendlyName,
            Filesystem = fs,
            Bootable = p.IsBootable,
            BootPriority = p.BootPri,
            DosType = p.DosType,
            MaxTransfer = p.MaxTransfer,
            Mask = p.Mask,
            NoMount = p.IsNoMount,
        };
    }

    private static FilesystemInfo ProbeRange(IBlockDevice device, long start, long len, List<string> problems)
    {
        if (start % device.SectorSize != 0 || len <= 0 || start + len > device.Length)
        {
            problems.Add($"A partition at byte {start} lies outside the device.");
            return new FilesystemInfo { Kind = FilesystemKind.Unknown };
        }
        try
        {
            using var view = new SubBlockDevice(device, start, len);
            return FilesystemProbe.Probe(view);
        }
        catch (IOException ex)
        {
            problems.Add($"Read error while probing partition at byte {start}: {ex.Message}");
            return new FilesystemInfo { Kind = FilesystemKind.Unknown, Notes = ["Could not be read."] };
        }
    }

    private static bool HasBackupGpt(IBlockDevice device)
    {
        var last = device.ReadSector(device.SectorCount() - 1);
        return Encoding.ASCII.GetString(last, 0, 8) == "EFI PART";
    }

    private static bool IsBlank(IBlockDevice device)
    {
        int len = (int)Math.Min(1024 * 1024, device.Length);
        var buf = new byte[len - len % device.SectorSize];
        device.Read(0, buf);
        return buf.AsSpan().IndexOfAnyExcept((byte)0) < 0 || buf.AsSpan().IndexOfAnyExcept((byte)0xFF) < 0;
    }
}
