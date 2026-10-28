using System.Text;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.FileSystems;

/// <summary>Identifies the filesystem at the start of a volume. Read-only.</summary>
public static class FilesystemProbe
{
    public static FilesystemInfo Probe(IBlockDevice volume)
    {
        if (volume.Length < 512 * 4) return new FilesystemInfo { Kind = FilesystemKind.Unknown, Notes = ["Volume too small to identify."] };
        var first = volume.ReadSectors(0, 4);
        var s0 = first.AsSpan(0, 512);

        string oem = Encoding.ASCII.GetString(s0.Slice(3, 8));
        if (oem == "EXFAT   ")
            return new FilesystemInfo { Kind = FilesystemKind.ExFat, TotalBytes = volume.Length, Label = null, Notes = ["exFAT detected from the boot sector signature."] };
        if (oem == "NTFS    ")
            return new FilesystemInfo { Kind = FilesystemKind.Ntfs, TotalBytes = volume.Length };

        var fat = FatBootSector.TryParse(s0);
        if (fat is not null) return DescribeFat(volume, fat);

        uint id = Endian.U32BE(s0, 0);
        var dosType = new DosType(id);
        if (dosType.IsDosFamily) return AmigaFfsInspector.Inspect(volume);
        if (dosType.IsPfs || (id & 0xFFFFFF00) == 0x50465300)
            return new FilesystemInfo { Kind = FilesystemKind.Pfs, DosType = dosType, TotalBytes = volume.Length, Notes = ["PFS boot block signature found (structure not validated)."] };
        if (dosType.IsSfs)
            return new FilesystemInfo { Kind = FilesystemKind.Sfs, DosType = dosType, TotalBytes = volume.Length, Notes = ["SFS signature found (structure not validated)."] };

        if (IsUniform(volume, 0x00)) return new FilesystemInfo { Kind = FilesystemKind.Blank, TotalBytes = volume.Length, Notes = ["The first 64 KiB are all zero."] };
        if (IsUniform(volume, 0xFF)) return new FilesystemInfo { Kind = FilesystemKind.Blank, TotalBytes = volume.Length, Notes = ["The first 64 KiB are erased (0xFF)."] };

        return new FilesystemInfo { Kind = FilesystemKind.Unknown, TotalBytes = volume.Length };
    }

    private static FilesystemInfo DescribeFat(IBlockDevice volume, FatBootSector fat)
    {
        var notes = new List<string>();
        string? label = string.IsNullOrWhiteSpace(fat.BootSectorLabel) || fat.BootSectorLabel == "NO NAME" ? null : fat.BootSectorLabel;
        long? free = null;
        bool validated = false;

        if (fat.TotalSectors * fat.BytesPerSector > volume.Length)
            notes.Add("The filesystem claims to be larger than its partition.");

        if (fat.BytesPerSector == volume.SectorSize && fat.Kind == FilesystemKind.Fat32)
        {
            try
            {
                var rootSector = fat.ClusterToSector(fat.RootCluster);
                if (rootSector < volume.SectorCount())
                {
                    var dir = volume.ReadSector(rootSector);
                    for (int i = 0; i < dir.Length; i += 32)
                    {
                        if (dir[i] == 0) break;
                        if (dir[i] != 0xE5 && dir[i + 11] == 0x08) { label = Endian.Ascii(dir, i, 11); break; }
                    }
                }
                var fsinfo = volume.ReadSector(fat.FsInfoSector);
                if (Endian.U32LE(fsinfo, 0) == 0x41615252 && Endian.U32LE(fsinfo, 484) == 0x61417272)
                {
                    uint fc = Endian.U32LE(fsinfo, 488);
                    if (fc != 0xFFFFFFFF && fc <= fat.ClusterCount) free = fc * fat.ClusterBytes;
                }
                else notes.Add("FSInfo sector signature missing.");

                var fat0 = volume.ReadSector(fat.FirstFatSector);
                validated = (Endian.U32LE(fat0, 0) & 0x0FFFFF00) == 0x0FFFFF00;
                if (!validated) notes.Add("The first FAT entry does not carry the media descriptor.");
            }
            catch (ArgumentOutOfRangeException)
            {
                notes.Add("FAT structures point outside the partition.");
            }
        }

        return new FilesystemInfo
        {
            Kind = fat.Kind,
            Label = label,
            ClusterBytes = fat.ClusterBytes,
            BytesPerSector = fat.BytesPerSector,
            TotalBytes = fat.TotalSectors * fat.BytesPerSector,
            FreeBytes = free,
            StructureValidated = validated,
            Notes = notes,
        };
    }

    private static bool IsUniform(IBlockDevice volume, byte value)
    {
        int len = (int)Math.Min(64 * 1024, volume.Length);
        len -= len % volume.SectorSize;
        var buf = new byte[len];
        volume.Read(0, buf);
        return buf.AsSpan().IndexOfAnyExcept(value) < 0;
    }
}
