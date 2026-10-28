using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Diagnostics;

/// <summary>
/// Turns a read-only <see cref="DiskInspection"/> into findings and recommendations for a
/// selected hardware interface. Rules never conclude that data is corrupt just because Windows
/// cannot read it: an Amiga layout is unreadable to Windows by design.
/// </summary>
public static class DiagnosticEngine
{
    public static DiagnosticReport Analyse(DiskInspection inspection, HardwareProfile? profile = null, StorageInterfaceProfile? iface = null, IntendedUse? use = null, string? deviceName = null)
    {
        var f = new List<Finding>();
        var r = new List<Recommendation>();

        DescribeStructure(inspection, f);

        if (iface is not null)
        {
            use ??= iface.Uses.FirstOrDefault();
            if (iface.Status == VerificationStatus.Protected) AssessProtected(inspection, iface, f, r);
            else if (iface.Layouts.Contains(LayoutKind.Emu68)) AssessEmu68(inspection, f, r);
            else if (iface.Layouts.Contains(LayoutKind.MbrFat32)) AssessDataCard(inspection, iface, f, r);
            else if (iface.Layouts.Contains(LayoutKind.Rdb)) AssessHardDrive(inspection, iface, use.Value, f, r);
        }

        if (inspection.Partitions.Count > 0 || inspection.Scheme is PartitionScheme.Unknown)
            r.Insert(0, new Recommendation("If the card holds files you need, make a full backup image before changing anything.", RecommendedAction.BackupFirst));

        var (verdict, confidence, summary) = Conclude(inspection, iface, f);
        return new DiagnosticReport
        {
            Inspection = inspection,
            DeviceName = deviceName,
            Profile = profile,
            Interface = iface,
            Use = use,
            Findings = f,
            Recommendations = r.DistinctBy(x => x.Text).ToList(),
            Verdict = verdict,
            Confidence = confidence,
            Summary = summary,
        };
    }

    private static void DescribeStructure(DiskInspection d, List<Finding> f)
    {
        switch (d.Scheme)
        {
            case PartitionScheme.Mbr:
                f.Add(Info("layout.mbr", "MBR partition table detected", $"{d.Partitions.Count} partition(s)."));
                break;
            case PartitionScheme.Gpt:
                f.Add(Info("layout.gpt", "GPT partition table detected", $"{d.Partitions.Count} partition(s)."));
                break;
            case PartitionScheme.Rdb:
                f.Add(Info("layout.rdb", "Amiga RDB detected", $"RDSK block at sector {d.Rdb!.Rdsk.Location}; {d.Rdb.Partitions.Count} partition(s), {d.Rdb.FileSystems.Count} embedded filesystem(s)."));
                break;
            case PartitionScheme.Unpartitioned:
                f.Add(Info("layout.none", "No partition table: the filesystem starts at sector 0",
                    "This is a 'superfloppy' (PC) or RDB-less hardfile (Amiga) layout."));
                break;
            case PartitionScheme.Blank:
                f.Add(Info("layout.blank", "The card appears to be empty", "The first megabyte contains no partition table or filesystem."));
                break;
            default:
                f.Add(new Finding { Code = "layout.unknown", Severity = FindingSeverity.Warning, Confidence = Confidence.Low,
                    Title = "Unknown layout - further inspection required",
                    Detail = "No MBR, GPT, Amiga RDB or known filesystem was found at the start of the card. This does not mean the data is damaged; it may use a format this tool does not recognise." });
                break;
        }

        if (d.Scheme != PartitionScheme.Rdb && d.Rdb is null)
            f.Add(Info("rdb.absent", "Amiga RDB not detected", "No RDSK block in the first 16 sectors."));

        foreach (var p in d.Partitions)
        {
            var fs = p.Filesystem;
            string where = d.Scheme == PartitionScheme.Unpartitioned ? "Card" : p.Table == "RDB" ? $"{p.Name}:" : p.Name;
            string label = fs.Label is null ? "" : $" labelled '{fs.Label}'";
            string text = fs.Kind switch
            {
                FilesystemKind.Blank => $"{where} has no filesystem",
                FilesystemKind.Unknown => $"{where}: filesystem not recognised",
                _ => $"{fs.DisplayName} filesystem detected on {where}{label}",
            };
            string detail = $"{Format.Bytes(p.LengthBytes)} at offset {Format.Bytes(p.StartByte)}." +
                (fs.ClusterBytes is long c ? $" Cluster size {Format.Bytes(c)}." : "") +
                (fs.Notes.Count > 0 ? " " + string.Join(" ", fs.Notes) : "");
            f.Add(new Finding { Code = $"fs.{fs.Kind.ToString().ToLowerInvariant()}", Severity = FindingSeverity.Info, Title = text, Detail = detail,
                Confidence = fs.StructureValidated || fs.Kind == FilesystemKind.Blank ? Confidence.High : Confidence.Medium });
        }

        if (d.HasConflictingRdbAndMbr)
        {
            f.Add(new Finding
            {
                Code = "layout.stale-rdb",
                Severity = FindingSeverity.Problem,
                Confidence = Confidence.High,
                ExplainsUninitialized = true,
                Title = "Leftover Amiga RDB found behind a PC partition table",
                Detail = $"Sector 0 holds a PC MBR/boot sector, but an old RDSK block is still present at sector {d.Rdb!.Rdsk.Location}. Windows uses the MBR, while Amiga software scans the first 16 sectors for an RDB and may mount the old Amiga partitions instead - which then appear as 'Uninitialized' because the PC format overwrote them.",
            });
        }
        if (d.HasBackupGptHeader && d.Scheme != PartitionScheme.Gpt)
            f.Add(new Finding { Code = "layout.stale-gpt", Severity = FindingSeverity.Warning, Confidence = Confidence.High,
                Title = "Leftover GPT backup header at the end of the card",
                Detail = "Windows or other tools may detect the card as GPT and offer to 'repair' it, which would overwrite the current layout." });
        if (d.Problems.Count > 0)
            f.Add(new Finding { Code = "layout.inconsistent", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium,
                Title = "Partition structures contain inconsistencies", Detail = string.Join(" ", d.Problems) });
    }

    private static void AssessDataCard(DiskInspection d, StorageInterfaceProfile iface, List<Finding> f, List<Recommendation> r)
    {
        string label = iface.Defaults.FatLabel ?? "SD0";
        var prepare = new Recommendation($"Prepare the card as a data-exchange card: MBR with one FAT32 partition labelled '{label}' (Apollo's documented recipe). This erases the card.", RecommendedAction.PrepareDataCard);

        if (iface.Constraints.RecommendedMaxCapacityBytes is long rec && d.LengthBytes > rec)
            f.Add(new Finding { Code = "card.sdxc", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium,
                Title = $"Card is larger than {Format.Bytes(rec)} (SDXC class)",
                Detail = "The documented card type for this slot is SDHC (up to 32 GB). Larger cards ship formatted as exFAT and may need reformatting; their behaviour with FAT32 is not officially documented." });

        switch (d.Scheme)
        {
            case PartitionScheme.Rdb:
                f.Add(new Finding { Code = "data.is-rdb", Severity = FindingSeverity.Problem, Confidence = Confidence.High, ExplainsUninitialized = true,
                    Title = "This is an Amiga hard-drive card, not a FAT32 data card",
                    Detail = "The card is partitioned with an Amiga RDB. Windows cannot read it, which is normal - it is not damaged. The SD 'Mount' function for data exchange expects FAT32, so mounting it that way can show 'Uninitialized'." });
                r.Add(new Recommendation("If this card should stay an Amiga hard drive, select the hard-drive interface for this slot and diagnose again instead of reformatting."));
                r.Add(prepare);
                return;
            case PartitionScheme.Gpt:
                f.Add(new Finding { Code = "data.gpt", Severity = FindingSeverity.Problem, Confidence = Confidence.Medium, ExplainsUninitialized = true,
                    Title = "GPT partitioning is not the documented layout",
                    Detail = "Apollo's documented FAT32 recipe uses an MBR partition table. A GPT card may not mount." });
                r.Add(prepare);
                return;
            case PartitionScheme.Blank:
                r.Add(prepare);
                return;
            case PartitionScheme.Unknown:
                r.Add(new Recommendation("The layout is not recognised. If you do not need the contents, prepare it as a data card; otherwise inspect it on the machine that created it."));
                return;
        }

        var first = d.Partitions.FirstOrDefault();
        if (first is null) { r.Add(prepare); return; }

        if (d.Scheme == PartitionScheme.Unpartitioned && first.Filesystem.IsFat)
            f.Add(new Finding { Code = "data.superfloppy", Severity = FindingSeverity.Warning, Confidence = Confidence.Low, ExplainsUninitialized = true,
                Title = "FAT filesystem without a partition table",
                Detail = "The documented recipe creates an MBR partition. Whether ApolloOS mounts a FAT32 card without one is unverified." });

        if (d.Partitions.Count > 1)
            f.Add(new Finding { Code = "data.multi", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium,
                Title = "More than one partition", Detail = "Only a single partition is documented; additional partitions may not be visible on the Amiga." });

        switch (first.Filesystem.Kind)
        {
            case FilesystemKind.ExFat:
                f.Add(new Finding { Code = "data.exfat", Severity = FindingSeverity.Problem, Confidence = Confidence.High, ExplainsUninitialized = true,
                    Title = "exFAT is not supported",
                    Detail = "The card uses exFAT (the factory format of most cards over 32 GB). ApolloOS reads FAT32; an exFAT card is not recognised and can be reported as uninitialised or unreadable." });
                r.Add(prepare);
                break;
            case FilesystemKind.Ntfs:
                f.Add(new Finding { Code = "data.ntfs", Severity = FindingSeverity.Problem, Confidence = Confidence.High, ExplainsUninitialized = true,
                    Title = "NTFS is not supported", Detail = "Amiga systems cannot read NTFS." });
                r.Add(prepare);
                break;
            case FilesystemKind.Fat12 or FilesystemKind.Fat16:
                f.Add(new Finding { Code = "data.fat16", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium,
                    Title = $"{first.Filesystem.DisplayName} instead of FAT32", Detail = "The documented recipe is FAT32. FAT16 may work with the FAT handler but is not the documented configuration." });
                break;
            case FilesystemKind.Fat32:
                AssessFat32(d, first, iface, label, f, r, prepare);
                break;
            case FilesystemKind.Blank:
                f.Add(new Finding { Code = "data.unformatted", Severity = FindingSeverity.Problem, Confidence = Confidence.High, ExplainsUninitialized = true,
                    Title = "The partition has no filesystem", Detail = "A partition exists but was never formatted." });
                r.Add(prepare);
                break;
            default:
                if (first.Filesystem.IsAmiga)
                {
                    f.Add(new Finding { Code = "data.amiga-fs", Severity = FindingSeverity.Problem, Confidence = Confidence.Medium, ExplainsUninitialized = true,
                        Title = "Amiga filesystem without an RDB",
                        Detail = "The card holds an Amiga filesystem but no RDB. It needs a mountlist (e.g. an RDB-less hardfile) and is not a FAT32 data card." });
                }
                else
                {
                    f.Add(new Finding { Code = "data.unknown-fs", Severity = FindingSeverity.Problem, Confidence = Confidence.Medium, ExplainsUninitialized = true,
                        Title = "First partition does not contain FAT32", Detail = "The filesystem was not recognised as FAT32." });
                }
                r.Add(prepare);
                break;
        }
    }

    private static void AssessFat32(DiskInspection d, InspectedPartition p, StorageInterfaceProfile iface, string label, List<Finding> f, List<Recommendation> r, Recommendation prepare)
    {
        bool issue = false;
        if (iface.Constraints.MaxFatClusterBytes is long max && p.Filesystem.ClusterBytes > max)
        {
            issue = true;
            f.Add(new Finding { Code = "data.cluster", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium, ExplainsUninitialized = true,
                Title = $"Large FAT32 clusters ({Format.Bytes(p.Filesystem.ClusterBytes!.Value)})",
                Detail = $"Clusters above {Format.Bytes(max)} are unusual and not supported by every FAT driver." });
        }
        if (d.Mbr?.UsedEntries.FirstOrDefault() is { } e && e.Type != MbrPartitionTypes.Fat32Lba && e.Type != MbrPartitionTypes.Fat32Chs)
        {
            issue = true;
            f.Add(new Finding { Code = "data.parttype", Severity = FindingSeverity.Warning, Confidence = Confidence.Low, ExplainsUninitialized = true,
                Title = $"Partition type is {e.TypeName}", Detail = "A FAT32 partition is normally type 0x0C. Some drivers select the handler from the partition type." });
        }
        if (!p.Filesystem.StructureValidated)
        {
            issue = true;
            f.Add(new Finding { Code = "data.fat-structure", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium,
                Title = "FAT32 structures look unusual", Detail = string.Join(" ", p.Filesystem.Notes) });
        }
        if (!string.Equals(p.Filesystem.Label, label, StringComparison.OrdinalIgnoreCase))
            f.Add(new Finding { Code = "data.label", Severity = FindingSeverity.Info, Confidence = Confidence.Low,
                Title = $"Volume label is '{p.Filesystem.Label ?? "(none)"}', not '{label}'",
                Detail = $"Apollo's formatting instructions set the label to '{label}'. Whether the label is required is not documented." });

        if (!issue)
        {
            f.Add(new Finding { Code = "data.ok", Severity = FindingSeverity.Ok, Confidence = Confidence.Medium,
                Title = "FAT32 data card layout matches the documented configuration",
                Detail = "If the Amiga still does not read it, Apollo notes that some factory FAT32 formats are incompatible; reformatting with the documented recipe is the fix." });
            r.Add(new Recommendation("No change needed. Insert the card and use Start Menu > SD Card > Mount in ApolloOS.", RecommendedAction.NoChangeNeeded));
            r.Add(new Recommendation("If ApolloOS still reports the card as uninitialised, back up its files and reformat it with the documented recipe.", RecommendedAction.PrepareDataCard));
        }
        else r.Add(prepare);
    }

    private static void AssessHardDrive(DiskInspection d, StorageInterfaceProfile iface, IntendedUse use, List<Finding> f, List<Recommendation> r)
    {
        var prepare = new Recommendation("Prepare the card as an Amiga hard drive (RDB with Amiga partitions, as HDToolBox creates). This erases the card.", RecommendedAction.PrepareHardDriveCard);
        if (d.Scheme != PartitionScheme.Rdb)
        {
            if (d.Scheme == PartitionScheme.Blank)
            {
                f.Add(new Finding { Code = "hdd.blank", Severity = FindingSeverity.Problem, Confidence = Confidence.High, ExplainsUninitialized = true,
                    Title = "No Amiga partitions", Detail = "The card is empty; it needs an RDB and partitions." });
            }
            else if (d.Scheme == PartitionScheme.Unpartitioned && d.Partitions.FirstOrDefault()?.Filesystem.IsAmiga == true)
            {
                f.Add(new Finding { Code = "hdd.rdbless", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium,
                    Title = "RDB-less Amiga hardfile layout",
                    Detail = "The card holds a single Amiga filesystem without an RDB. It can only be used with a mountlist; it will not auto-mount or boot as a hard drive." });
            }
            else
            {
                f.Add(new Finding { Code = "hdd.pc-layout", Severity = FindingSeverity.Problem, Confidence = Confidence.High, ExplainsUninitialized = true,
                    Title = "The card has a PC layout, not an Amiga hard-drive layout",
                    Detail = "Amiga hard drives use a Rigid Disk Block. A card formatted on Windows (FAT32/exFAT) shows as 'Uninitialized' when used as an Amiga hard drive. The card is not damaged." });
            }
            r.Add(prepare);
            return;
        }

        var rdb = d.Rdb!;
        var limits = iface.EffectiveLimits(null);
        var handlers = new HashSet<DosType>(limits.RomFs);
        foreach (var fs in rdb.FileSystems) handlers.Add(fs.DosType);
        bool needsFormat = false, problems = false;

        foreach (var p in d.Partitions)
        {
            string name = $"{p.Name}:";
            if (p.NoMount)
                f.Add(Info($"hdd.nomount.{p.Name}", $"{name} is set not to mount automatically", "The partition has the 'No Mount' flag."));

            switch (p.Filesystem.Kind)
            {
                case FilesystemKind.Blank:
                    needsFormat = true;
                    f.Add(new Finding { Code = $"hdd.unformatted.{p.Name}", Severity = FindingSeverity.Warning, Confidence = Confidence.High, ExplainsUninitialized = true,
                        Title = $"{name} is partitioned but not formatted",
                        Detail = "This partition will appear as 'Uninitialized' on the Amiga. That is expected for a new partition - format it from Workbench (select the icon, then Icons > Format Disk) or with the Format command." });
                    break;
                case FilesystemKind.AmigaDos when p.Filesystem.StructureValidated:
                    if (p.DosType is { } dt && p.Filesystem.DosType is { } fdt && dt != fdt)
                        f.Add(new Finding { Code = $"hdd.dostype-mismatch.{p.Name}", Severity = FindingSeverity.Warning, Confidence = Confidence.High, ExplainsUninitialized = true,
                            Title = $"{name} DosType mismatch",
                            Detail = $"The RDB says {dt.FriendlyName} but the partition was formatted as {fdt.FriendlyName}. The handler may not recognise the volume." });
                    else
                        f.Add(new Finding { Code = $"hdd.ok.{p.Name}", Severity = FindingSeverity.Ok, Confidence = Confidence.High,
                            Title = $"{name} '{p.Filesystem.Label}' is a valid {p.Filesystem.DisplayName} volume", Detail = p.Filesystem.FreeBytes is long free ? $"{Format.Bytes(free)} free." : "" });
                    break;
                case FilesystemKind.AmigaDos:
                    problems = true;
                    f.Add(new Finding { Code = $"hdd.ffs-unvalidated.{p.Name}", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium,
                        Title = $"{name} has an FFS boot block but its root block could not be validated",
                        Detail = string.Join(" ", p.Filesystem.Notes) + " This can be normal for non-default block sizes; the Amiga may need to validate the volume." });
                    break;
                case FilesystemKind.Pfs or FilesystemKind.Sfs or FilesystemKind.OtherAmiga:
                    f.Add(Info($"hdd.fs.{p.Name}", $"{name} holds {p.Filesystem.DisplayName}", "Structure was identified by signature only."));
                    break;
                default:
                    problems = true;
                    f.Add(new Finding { Code = $"hdd.unknown-fs.{p.Name}", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium, ExplainsUninitialized = true,
                        Title = $"{name}: filesystem not recognised",
                        Detail = "The partition contains data that is not an Amiga filesystem this tool recognises. The Amiga may show it as 'Uninitialized' or as 'Not a DOS disk'." });
                    break;
            }

            if (p.DosType is { } pdt && !handlers.Contains(pdt))
                f.Add(new Finding { Code = $"hdd.handler.{p.Name}", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium, ExplainsUninitialized = true,
                    Title = $"{name} needs a {pdt.FriendlyName} handler",
                    Detail = "The handler is not embedded in the RDB and is not built into this system's ROM as far as the profile records. Without it the partition will not mount (or will mount incorrectly)." });

            if (limits.MaxEnd is long maxEnd && p.EndByteExclusive > maxEnd)
            {
                problems = true;
                f.Add(new Finding { Code = $"hdd.beyond-limit.{p.Name}", Severity = FindingSeverity.Problem, Confidence = Confidence.High,
                    Title = $"{name} ends beyond {Format.Bytes(maxEnd)}", Detail = "This system's disk driver cannot address that far; data beyond the limit would be corrupted." });
            }
        }

        if (use == IntendedUse.BootableSystem && !d.Partitions.Any(p => p.Bootable))
        {
            problems = true;
            f.Add(new Finding { Code = "hdd.no-boot", Severity = FindingSeverity.Problem, Confidence = Confidence.High,
                Title = "No bootable partition", Detail = "None of the partitions is flagged bootable." });
        }

        if (needsFormat) r.Add(new Recommendation("Format the unformatted partitions on the Amiga. No changes are needed from Windows.", RecommendedAction.FormatOnAmiga));
        if (problems) r.Add(prepare);
        if (!needsFormat && !problems) r.Add(new Recommendation("No change needed.", RecommendedAction.NoChangeNeeded));
    }

    private static void AssessEmu68(DiskInspection d, List<Finding> f, List<Recommendation> r)
    {
        var prepare = new Recommendation("Prepare the card as a PiStorm / Emu68 card: FAT32 boot partition plus an Amiga partition (type 0x76) with an RDB. This erases the card.", RecommendedAction.PrepareHardDriveCard);
        bool boot = d.Scheme == PartitionScheme.Mbr && d.Partitions.FirstOrDefault()?.Filesystem.IsFat == true;
        bool amiga = d.Partitions.Any(p => p.Table == "RDB in 0x76");
        if (!boot)
            f.Add(new Finding { Code = "emu68.no-boot", Severity = FindingSeverity.Problem, Confidence = Confidence.High,
                Title = "No FAT boot partition first", Detail = "Emu68 boots from a FAT32 (or FAT16) partition holding its release files." });
        if (!amiga)
            f.Add(new Finding { Code = "emu68.no-amiga", Severity = FindingSeverity.Warning, Confidence = Confidence.Medium, ExplainsUninitialized = true,
                Title = "No Amiga partition (type 0x76) with an RDB", Detail = "Without one, the card has no Amiga hard-drive storage under Emu68." });
        if (boot && amiga)
            f.Add(new Finding { Code = "emu68.ok", Severity = FindingSeverity.Ok, Confidence = Confidence.Medium, Title = "PiStorm / Emu68 layout found",
                Detail = "Boot partition and Amiga partition present. The Emu68 files must be on the boot partition." });
        else r.Add(prepare);
    }

    private static void AssessProtected(DiskInspection d, StorageInterfaceProfile iface, List<Finding> f, List<Recommendation> r)
    {
        f.Add(new Finding { Code = "protected", Severity = FindingSeverity.Info, Confidence = Confidence.High,
            Title = $"{iface.Name} is a system drive",
            Detail = "This tool only backs up or restores complete authorised images for this interface; it never repartitions it." });
        r.Add(new Recommendation("Make a backup image of this card before any change.", RecommendedAction.BackupFirst));
        if (d.Scheme is PartitionScheme.Blank or PartitionScheme.Unknown)
            r.Add(new Recommendation("Restore an authorised ApolloOS image obtained from Apollo.", RecommendedAction.RestoreAuthorisedImage));
    }

    private static (CompatibilityVerdict, Confidence, string) Conclude(DiskInspection d, StorageInterfaceProfile? iface, List<Finding> f)
    {
        if (iface is null)
            return (CompatibilityVerdict.NotAssessed, Confidence.High, $"{d.SchemeDescription} layout with {d.Partitions.Count} partition(s). Select a hardware profile for a compatibility assessment.");

        var problem = f.Where(x => x.Severity == FindingSeverity.Problem).OrderByDescending(x => x.Confidence).FirstOrDefault();
        if (problem is not null)
            return (CompatibilityVerdict.Incompatible, problem.Confidence, problem.Title);
        if (d.Scheme == PartitionScheme.Unknown)
            return (CompatibilityVerdict.Unknown, Confidence.Low, "The card's layout could not be identified.");
        if (f.Any(x => x.Code.StartsWith("hdd.unformatted", StringComparison.Ordinal)))
            return (CompatibilityVerdict.NeedsAmigaFormat, Confidence.High, "Partitioned correctly; some partitions still need formatting on the Amiga.");

        bool warnings = f.Any(x => x.Severity == FindingSeverity.Warning);
        bool verified = iface.Status == VerificationStatus.Verified;
        if (!warnings && verified)
            return (CompatibilityVerdict.Compatible, Confidence.High, "The card matches the documented requirements.");
        return (CompatibilityVerdict.LikelyCompatible, warnings ? Confidence.Low : Confidence.Medium,
            warnings ? "No blocking problems found, but see the warnings." : "No problems found; some requirements for this interface are unverified.");
    }

    private static Finding Info(string code, string title, string detail) =>
        new() { Code = code, Severity = FindingSeverity.Info, Title = title, Detail = detail, Confidence = Confidence.High };
}
