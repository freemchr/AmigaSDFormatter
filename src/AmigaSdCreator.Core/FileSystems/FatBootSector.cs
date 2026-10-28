using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.FileSystems;

/// <summary>FAT12/16/32 BIOS parameter block, interpreted per the Microsoft FAT specification.</summary>
public sealed record FatBootSector
{
    public required string OemName { get; init; }
    public int BytesPerSector { get; init; }
    public int SectorsPerCluster { get; init; }
    public int ReservedSectors { get; init; }
    public int NumberOfFats { get; init; }
    public int RootEntryCount { get; init; }
    public long TotalSectors { get; init; }
    public long SectorsPerFat { get; init; }
    public uint HiddenSectors { get; init; }
    public uint RootCluster { get; init; }
    public int FsInfoSector { get; init; }
    public int BackupBootSector { get; init; }
    public uint VolumeId { get; init; }
    public string BootSectorLabel { get; init; } = "";
    public string FsTypeText { get; init; } = "";
    public long ClusterCount { get; init; }
    public FilesystemKind Kind { get; init; }

    public long ClusterBytes => (long)BytesPerSector * SectorsPerCluster;
    public long FirstFatSector => ReservedSectors;
    public long RootDirSectors => (RootEntryCount * 32L + BytesPerSector - 1) / BytesPerSector;
    public long FirstDataSector => ReservedSectors + NumberOfFats * SectorsPerFat + RootDirSectors;
    public long ClusterToSector(uint cluster) => FirstDataSector + (cluster - 2L) * SectorsPerCluster;

    public static FatBootSector? TryParse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 512 || s[510] != 0x55 || s[511] != 0xAA) return null;
        if (!(s[0] == 0xEB && s[2] == 0x90) && s[0] != 0xE9) return null;
        int bps = Endian.U16LE(s, 11);
        int spc = s[13];
        int rsvd = Endian.U16LE(s, 14);
        int fats = s[16];
        if (bps is not (512 or 1024 or 2048 or 4096)) return null;
        if (spc == 0 || (spc & (spc - 1)) != 0) return null;
        if (rsvd == 0 || fats is < 1 or > 2) return null;

        int rootEnt = Endian.U16LE(s, 17);
        long tot = Endian.U16LE(s, 19);
        if (tot == 0) tot = Endian.U32LE(s, 32);
        long fatSz = Endian.U16LE(s, 22);
        bool fat32Layout = fatSz == 0;
        if (fat32Layout) fatSz = Endian.U32LE(s, 36);
        if (fatSz == 0 || tot == 0) return null;

        long rootDirSectors = (rootEnt * 32L + bps - 1) / bps;
        long dataSec = tot - (rsvd + fats * fatSz + rootDirSectors);
        if (dataSec <= 0) return null;
        long clusters = dataSec / spc;
        var kind = clusters < 4085 ? FilesystemKind.Fat12 : clusters < 65525 ? FilesystemKind.Fat16 : FilesystemKind.Fat32;

        int labelOffset = fat32Layout ? 71 : 43;
        int typeOffset = fat32Layout ? 82 : 54;
        bool extBoot = s[fat32Layout ? 66 : 38] == 0x29;
        return new FatBootSector
        {
            OemName = Endian.Ascii(s, 3, 8),
            BytesPerSector = bps,
            SectorsPerCluster = spc,
            ReservedSectors = rsvd,
            NumberOfFats = fats,
            RootEntryCount = rootEnt,
            TotalSectors = tot,
            SectorsPerFat = fatSz,
            HiddenSectors = Endian.U32LE(s, 28),
            RootCluster = fat32Layout ? Endian.U32LE(s, 44) : 0,
            FsInfoSector = fat32Layout ? Endian.U16LE(s, 48) : 0,
            BackupBootSector = fat32Layout ? Endian.U16LE(s, 50) : 0,
            VolumeId = extBoot ? Endian.U32LE(s, fat32Layout ? 67 : 39) : 0,
            BootSectorLabel = extBoot ? Endian.Ascii(s, labelOffset, 11) : "",
            FsTypeText = extBoot ? Endian.Ascii(s, typeOffset, 8) : "",
            ClusterCount = clusters,
            Kind = kind,
        };
    }
}
