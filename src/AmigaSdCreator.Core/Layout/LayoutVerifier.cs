using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Layout;

public sealed record VerificationCheck(string Name, bool Passed, string Detail);

public sealed record VerificationResult(IReadOnlyList<VerificationCheck> Checks, DiskInspection Inspection)
{
    public bool Passed => Checks.All(c => c.Passed);
}

/// <summary>Re-reads a device after preparation and checks every structure against the plan.</summary>
public static class LayoutVerifier
{
    public static VerificationResult Verify(IBlockDevice device, LayoutPlan plan)
    {
        var inspection = DiskInspector.Inspect(device);
        var checks = new List<VerificationCheck>();
        void Check(string name, bool ok, string detail) => checks.Add(new VerificationCheck(name, ok, detail));

        Check("No unexpected structures", inspection.Problems.Count == 0 && !inspection.HasConflictingRdbAndMbr && !inspection.HasBackupGptHeader,
            inspection.Problems.Count == 0 ? "No inconsistencies found." : string.Join(" ", inspection.Problems));

        if (plan.Kind == LayoutKind.MbrFat32)
        {
            Check("Partition table", inspection.Scheme == PartitionScheme.Mbr, $"Found {inspection.SchemeDescription}.");
            var p = inspection.Partitions.FirstOrDefault();
            bool one = inspection.Partitions.Count == 1 && p is not null;
            Check("Single FAT32 partition", one && p!.StartByte == (long)plan.FatStartLba * plan.SectorSize && p.LengthBytes == (long)plan.FatSectorCount * plan.SectorSize,
                one ? $"Partition at {p!.StartByte}, {Format.Bytes(p.LengthBytes)}." : $"{inspection.Partitions.Count} partitions found.");
            if (p is not null)
            {
                Check("Partition type", inspection.Mbr?.Entries[0].Type == MbrPartitionTypes.Fat32Lba, inspection.Mbr?.Entries[0].TypeName ?? "none");
                Check("Filesystem", p.Filesystem.Kind == FilesystemKind.Fat32 && p.Filesystem.StructureValidated, p.Filesystem.DisplayName);
                Check("Volume label", string.Equals(p.Filesystem.Label, Fat32Formatter.NormaliseLabel(plan.FatLabel), StringComparison.Ordinal), p.Filesystem.Label ?? "(none)");
            }
            Check("No RDB signature", inspection.Rdb is null, inspection.Rdb is null ? "No RDSK block in the first 16 sectors." : "An RDSK block is present.");
        }
        else if (plan.Kind == LayoutKind.Emu68)
        {
            var e = inspection.Mbr?.UsedEntries.ToList() ?? [];
            Check("Partition table", inspection.Scheme == PartitionScheme.Mbr && e.Count == 2, $"Found {inspection.SchemeDescription} with {e.Count} partition(s).");
            if (e.Count == 2)
            {
                Check("Boot partition", e[0].Type == MbrPartitionTypes.Fat32Lba && e[0].FirstLba == plan.FatStartLba && e[0].SectorCount == plan.FatSectorCount, e[0].TypeName);
                Check("Amiga partition (0x76)", e[1].Type == MbrPartitionTypes.AmigaRdb && e[1].FirstLba == plan.AmigaStartLba, e[1].TypeName);
            }
            using (var boot = new SubBlockDevice(device, (long)plan.FatStartLba * plan.SectorSize, (long)plan.FatSectorCount * plan.SectorSize))
            {
                var fs = FilesystemProbe.Probe(boot);
                Check("Boot filesystem", fs.Kind == FilesystemKind.Fat32 && fs.StructureValidated && fs.Label == Fat32Formatter.NormaliseLabel(plan.FatLabel), $"{fs.DisplayName} '{fs.Label}'");
            }
            using var amiga = new SubBlockDevice(device, (long)plan.AmigaStartLba * plan.SectorSize, plan.AmigaRegionBytes);
            var inner = Verify(amiga, plan with { Kind = LayoutKind.Rdb, DeviceBytes = plan.AmigaRegionBytes });
            checks.AddRange(inner.Checks.Select(c => c with { Name = "Amiga: " + c.Name }));
            // The nested RDB is expected, so drop the "no unexpected structures" complaint about it.
            checks.RemoveAll(c => c.Name == "No unexpected structures" && !c.Passed && inspection.Problems.Count == 0);
        }
        else if (plan.Kind == LayoutKind.Rdb)
        {
            Check("Partition table", inspection.Scheme == PartitionScheme.Rdb && inspection.Rdb is not null, $"Found {inspection.SchemeDescription}.");
            if (inspection.Rdb is { } rdb)
            {
                Check("RDB checksums", rdb.Rdsk.ChecksumValid && rdb.Partitions.All(x => x.ChecksumValid) && rdb.FileSystems.All(f => f.ChecksumValid), "RDSK, PART and FSHD checksums.");
                Check("Geometry", rdb.Rdsk.Geometry == plan.Geometry, $"{rdb.Rdsk.Geometry.Cylinders} cylinders x {rdb.Rdsk.Geometry.Heads} heads x {rdb.Rdsk.Geometry.Sectors} sectors.");
                Check("Partition count", rdb.Partitions.Count == plan.Partitions.Count, $"{rdb.Partitions.Count} of {plan.Partitions.Count}.");

                foreach (var (expected, actual) in plan.Partitions.Zip(rdb.Partitions))
                {
                    bool same = actual.DriveName == expected.DeviceName && actual.LowCyl == expected.LowCyl && actual.HighCyl == expected.HighCyl
                        && actual.DosType == expected.DosType && actual.IsBootable == expected.Bootable && actual.BootPri == expected.BootPriority
                        && actual.MaxTransfer == expected.MaxTransfer && actual.Mask == expected.Mask;
                    Check($"{expected.DeviceName}: entry", same, $"Cylinders {actual.LowCyl}-{actual.HighCyl}, {actual.DosType.FriendlyName}.");

                    var found = inspection.Partitions.FirstOrDefault(x => x.Name == expected.DeviceName);
                    if (expected.WillBeFormatted)
                    {
                        bool ok = found is not null && found.Filesystem.Kind == FilesystemKind.AmigaDos && found.Filesystem.StructureValidated
                            && found.Filesystem.DosType == expected.DosType && found.Filesystem.Label == expected.VolumeName;
                        Check($"{expected.DeviceName}: filesystem", ok, found is null ? "Not found." : $"{found.Filesystem.DisplayName} '{found.Filesystem.Label}'.");
                    }
                    else
                    {
                        Check($"{expected.DeviceName}: left for Amiga format", found is not null && found.Filesystem.Kind == FilesystemKind.Blank,
                            "Partition start is blank as intended.");
                    }
                }

                Check("Embedded filesystems", rdb.FileSystems.Count == plan.FileSystems.Count, $"{rdb.FileSystems.Count} of {plan.FileSystems.Count}.");
                foreach (var (expected, actual) in plan.FileSystems.Zip(rdb.FileSystems))
                {
                    var data = RdbReader.ReadLoadSegData(device, actual.SegListBlock);
                    bool same = actual.DosType == expected.DosType && data.Length >= expected.Binary.Length
                        && data.AsSpan(0, expected.Binary.Length).SequenceEqual(expected.Binary)
                        && data.AsSpan(expected.Binary.Length).IndexOfAnyExcept((byte)0) < 0;
                    Check($"Filesystem {expected.DosType.FriendlyName}", same, $"{expected.SourceName} {expected.VersionText}, {expected.Binary.Length} bytes.");
                }
            }
        }

        return new VerificationResult(checks, inspection);
    }
}
