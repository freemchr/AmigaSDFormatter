using System.Text;
using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Partitioning;

public sealed record GptPartitionEntry(int Index, Guid TypeGuid, Guid PartitionGuid, ulong FirstLba, ulong LastLba, string Name)
{
    public static readonly Guid MicrosoftBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid MicrosoftReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");

    public string TypeName =>
        TypeGuid == MicrosoftBasicData ? "Microsoft basic data" :
        TypeGuid == EfiSystem ? "EFI system" :
        TypeGuid == MicrosoftReserved ? "Microsoft reserved" :
        TypeGuid.ToString();
}

/// <summary>Read-only GUID partition table parser (primary header at LBA 1).</summary>
public sealed class GptPartitionTable
{
    public Guid DiskGuid { get; init; }
    public IReadOnlyList<GptPartitionEntry> Entries { get; init; } = [];

    public static GptPartitionTable? TryRead(IBlockDevice device)
    {
        if (device.SectorCount() < 34) return null;
        var header = device.ReadSector(1);
        if (Encoding.ASCII.GetString(header, 0, 8) != "EFI PART") return null;
        ulong entriesLba = Endian.U64LE(header, 72);
        uint count = Endian.U32LE(header, 80);
        uint entrySize = Endian.U32LE(header, 84);
        if (entrySize < 128 || entrySize > 4096 || count > 1024 || entriesLba >= (ulong)device.SectorCount()) return null;

        long bytes = (long)count * entrySize;
        int sectors = (int)((bytes + device.SectorSize - 1) / device.SectorSize);
        var raw = device.ReadSectors((long)entriesLba, sectors);
        var entries = new List<GptPartitionEntry>();
        for (int i = 0; i < count; i++)
        {
            var e = raw.AsSpan((int)(i * entrySize), (int)entrySize);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty) continue;
            var name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
            entries.Add(new GptPartitionEntry(i + 1, type, new Guid(e.Slice(16, 16)), Endian.U64LE(e, 32), Endian.U64LE(e, 40), name));
        }
        return new GptPartitionTable { DiskGuid = new Guid(header.AsSpan(56, 16)), Entries = entries };
    }
}
