using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Partitioning;

public static class RdbReader
{
    private const int MaxChain = 256;

    /// <summary>Returns the block number of the first RDSK signature in blocks 0-15, or -1.</summary>
    public static int FindRdsk(IBlockDevice device)
    {
        int limit = (int)Math.Min(RdbIds.SearchLimit, device.SectorCount());
        if (limit <= 0) return -1;
        var head = device.ReadSectors(0, limit);
        for (int i = 0; i < limit; i++)
        {
            if (Endian.U32BE(head, i * device.SectorSize) == RdbIds.Rdsk) return i;
        }
        return -1;
    }

    public static RdbDisk? TryRead(IBlockDevice device)
    {
        int loc = FindRdsk(device);
        if (loc < 0) return null;

        var problems = new List<string>();
        var rdskRaw = device.ReadSector(loc);
        var rdsk = RigidDiskBlock.Parse(rdskRaw, loc);
        if (!rdsk.ChecksumValid) problems.Add($"RDSK block at {loc} has an invalid checksum.");
        if (rdsk.BlockBytes != device.SectorSize)
        {
            problems.Add($"RDB block size {rdsk.BlockBytes} differs from device sector size {device.SectorSize}; partitions not read.");
            return new RdbDisk { Rdsk = rdsk, Problems = problems };
        }

        long sectors = device.SectorCount();
        var partitions = new List<RdbPartition>();
        var seen = new HashSet<uint>();
        uint next = rdsk.PartitionList;
        while (next != RdbIds.EndOfList)
        {
            if (next >= sectors || !seen.Add(next) || partitions.Count >= MaxChain)
            {
                problems.Add($"Partition list is broken at block {next}.");
                break;
            }
            var b = device.ReadSector(next);
            if (Endian.U32BE(b, 0) != RdbIds.Part)
            {
                problems.Add($"Block {next} in the partition list is not a PART block.");
                break;
            }
            var p = RdbPartition.Parse(b, (int)next);
            if (!p.ChecksumValid) problems.Add($"Partition '{p.DriveName}' (block {next}) has an invalid checksum.");
            if (p.SizeBlockLongs == 0 || p.Surfaces == 0 || p.BlocksPerTrack == 0)
                problems.Add($"Partition '{p.DriveName}' has zero geometry values.");
            else if (p.EndByteExclusive > device.Length)
                problems.Add($"Partition '{p.DriveName}' extends beyond the end of the device.");
            partitions.Add(p);
            next = p.Next;
        }

        var fileSystems = new List<RdbFileSystem>();
        seen.Clear();
        next = rdsk.FileSysHeaderList;
        while (next != RdbIds.EndOfList)
        {
            if (next >= sectors || !seen.Add(next) || fileSystems.Count >= MaxChain)
            {
                problems.Add($"Filesystem header list is broken at block {next}.");
                break;
            }
            var b = device.ReadSector(next);
            if (Endian.U32BE(b, 0) != RdbIds.Fshd)
            {
                problems.Add($"Block {next} in the filesystem list is not an FSHD block.");
                break;
            }
            uint seg = Endian.U32BE(b, 72);
            int count = CountLoadSegs(device, seg, problems);
            var fs = RdbFileSystem.Parse(b, (int)next, count);
            if (!fs.ChecksumValid) problems.Add($"Filesystem header for {fs.DosType} has an invalid checksum.");
            fileSystems.Add(fs);
            next = fs.Next;
        }

        CheckOverlaps(partitions, problems);
        return new RdbDisk { Rdsk = rdsk, Partitions = partitions, FileSystems = fileSystems, Problems = problems };
    }

    /// <summary>Reassembles the filesystem binary stored in a chain of LSEG blocks.</summary>
    public static byte[] ReadLoadSegData(IBlockDevice device, uint firstBlock)
    {
        using var ms = new MemoryStream();
        var seen = new HashSet<uint>();
        uint next = firstBlock;
        while (next != RdbIds.EndOfList && seen.Add(next) && next < device.SectorCount())
        {
            var b = device.ReadSector(next);
            if (Endian.U32BE(b, 0) != RdbIds.Lseg) break;
            uint summed = Endian.U32BE(b, 4);
            int dataBytes = (int)Math.Clamp((summed - 5) * 4, 0, device.SectorSize - 20);
            ms.Write(b, 20, dataBytes);
            next = Endian.U32BE(b, 16);
        }
        return ms.ToArray();
    }

    private static int CountLoadSegs(IBlockDevice device, uint first, List<string> problems)
    {
        int count = 0;
        var seen = new HashSet<uint>();
        uint next = first;
        while (next != RdbIds.EndOfList)
        {
            if (next >= device.SectorCount() || !seen.Add(next) || count > 100_000)
            {
                problems.Add($"LSEG chain is broken at block {next}.");
                break;
            }
            var b = device.ReadSector(next);
            if (Endian.U32BE(b, 0) != RdbIds.Lseg)
            {
                problems.Add($"Block {next} in a filesystem LSEG chain is not an LSEG block.");
                break;
            }
            if (!AmigaChecksum.IsSumZero(b, (int)Math.Min(Endian.U32BE(b, 4), 128)))
                problems.Add($"LSEG block {next} has an invalid checksum.");
            count++;
            next = Endian.U32BE(b, 16);
        }
        return count;
    }

    private static void CheckOverlaps(List<RdbPartition> parts, List<string> problems)
    {
        var sorted = parts.Where(p => p.CylinderBytes > 0).OrderBy(p => p.StartByte).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].StartByte < sorted[i - 1].EndByteExclusive)
                problems.Add($"Partitions '{sorted[i - 1].DriveName}' and '{sorted[i].DriveName}' overlap.");
        }
        var names = parts.GroupBy(p => p.DriveName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var g in names) problems.Add($"Device name '{g.Key}' is used by more than one partition.");
    }
}
