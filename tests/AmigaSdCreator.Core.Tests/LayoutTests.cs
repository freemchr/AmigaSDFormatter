using AmigaSdCreator.Core.Diagnostics;
using AmigaSdCreator.Core.FileSystems;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Partitioning;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Tests;

public class LayoutTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void BuiltInProfiles_LoadAndReferenceKnownSources()
    {
        var c = TestSupport.Catalog;
        Assert.Empty(c.LoadErrors);
        Assert.Equal("apollo-a6000", c.Profiles[0].Id);
        foreach (var p in c.Profiles)
            foreach (var i in p.Interfaces)
                foreach (var f in i.Facts)
                    Assert.True(c.Sources.ContainsKey(f.Source), $"{i.Id} cites unknown source {f.Source}");
    }

    [Fact]
    public void UserProfileDirectory_OverridesAndAdds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asdc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mine.json"), """
            { "profiles": [ { "id": "my-board", "name": "My Board", "status": "Experimental", "interfaces": [] } ] }
            """);
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
        var c = ProfileCatalog.Load(dir);
        Assert.NotNull(c.Find("my-board"));
        Assert.NotNull(c.Find("apollo-a6000"));
        Assert.Single(c.LoadErrors);
    }

    [Fact]
    public void A6000DataCard_PlanIsMbrFat32_LabelledSD0_AlignedAt1MiB()
    {
        var plan = TestSupport.Plan("a6000-sd-data", IntendedUse.DataExchange, 8 * GiB);
        Assert.Equal(LayoutKind.MbrFat32, plan.Kind);
        Assert.Equal("SD0", plan.FatLabel);
        Assert.Equal(2048u, plan.FatStartLba);
        Assert.Equal(8 * GiB / 512 - 2048, plan.FatSectorCount);
        Assert.Equal(4096, plan.FatClusterBytes); // fat32format/Rufus table: 4 KiB up to 8 GB
        Assert.DoesNotContain(LayoutValidator.Validate(plan, TestSupport.Iface("a6000-sd-data").Interface), m => m.Severity != MessageSeverity.Info);
    }

    [Fact]
    public void A6000DataCard_WarnsAbove32GB()
    {
        var plan = TestSupport.Plan("a6000-sd-data", IntendedUse.DataExchange, 64 * GiB);
        var msgs = LayoutValidator.Validate(plan, TestSupport.Iface("a6000-sd-data").Interface);
        Assert.Contains(msgs, m => m.Severity == MessageSeverity.Warning && m.Text.Contains("SDHC"));
        Assert.False(LayoutValidator.HasErrors(msgs));
    }

    [Fact]
    public void A1200Ks31_KeepsEverythingBelow4GB_AndFfsAt2GB()
    {
        var plan = TestSupport.Plan("a1200-ide", IntendedUse.BootableSystem, 16 * GiB, "ks31");
        var iface = TestSupport.Iface("a1200-ide").Interface;
        Assert.All(plan.Partitions, p => Assert.True(plan.PartitionBytes(p) <= 2 * GiB));
        Assert.All(plan.Partitions, p => Assert.True((p.HighCyl + 1L) * plan.CylinderBytes <= 4 * GiB));
        Assert.True(plan.Partitions[0].Bootable);
        Assert.Equal("System", plan.Partitions[0].VolumeName);
        Assert.False(LayoutValidator.HasErrors(LayoutValidator.Validate(plan, iface)));
        Assert.True(plan.UnallocatedBytes > 11 * GiB);
    }

    [Fact]
    public void A1200Ks31_PartitionBeyond4GB_IsAnError()
    {
        var plan = TestSupport.Plan("a1200-ide", IntendedUse.HardDrive, 8 * GiB, "ks32");
        // Re-check the ks32 plan as if it were for a Kickstart 3.1 machine.
        var ks31 = plan with { CompatibilityModeId = "ks31" };
        var msgs = LayoutValidator.Validate(ks31, TestSupport.Iface("a1200-ide").Interface);
        Assert.Contains(msgs, m => m.Severity == MessageSeverity.Error && m.Text.Contains("can address"));
    }

    [Fact]
    public void BootPartition_WithNonRomFilesystemAndNoHandler_IsAnError()
    {
        var plan = TestSupport.Plan("a1200-ide", IntendedUse.BootableSystem, 2 * GiB, "ks32");
        plan = plan with { Partitions = plan.Partitions.Select(p => p with { DosType = DosType.Pfs3 }).ToList() };
        var iface = TestSupport.Iface("a1200-ide").Interface;
        Assert.True(LayoutValidator.HasErrors(LayoutValidator.Validate(plan, iface)));

        var withHandler = plan with { FileSystems = [new FileSystemDriverPlan(DosType.Pfs3, "pfs3aio", TestSupport.FakeHandler(), 19 << 16 | 2, "19.2")] };
        var msgs = LayoutValidator.Validate(withHandler, iface);
        Assert.False(LayoutValidator.HasErrors(msgs));
        Assert.Contains(msgs, m => m.Text.Contains("Uninitialized"));
    }

    [Fact]
    public void ProtectedAndPlannedInterfaces_CannotBePartitioned()
    {
        var cf = TestSupport.Iface("a6000-cf-system").Interface;
        var fake = TestSupport.Plan("a6000-sd-hdd", IntendedUse.HardDrive, 2 * GiB) with { InterfaceId = cf.Id };
        Assert.True(LayoutValidator.HasErrors(LayoutValidator.Validate(fake, cf)));
    }

    [Fact]
    public void Recalculate_PacksPartitionsInCylinderOrder()
    {
        var plan = TestSupport.Plan("a6000-sd-hdd", IntendedUse.HardDrive, 4 * GiB);
        plan = plan with
        {
            Partitions =
            [
                plan.Partitions[0] with { DeviceName = "SDH0", RequestedBytes = 512L << 20 },
                plan.Partitions[0] with { DeviceName = "SDH1", VolumeName = "Games", RequestedBytes = 0 },
            ],
        };
        plan = LayoutPlanner.Recalculate(plan);
        Assert.Equal(plan.RdbCylinders, plan.Partitions[0].LowCyl);
        Assert.Equal(plan.Partitions[0].HighCyl + 1, plan.Partitions[1].LowCyl);
        Assert.Equal(plan.Geometry!.Cylinders - 1, plan.Partitions[1].HighCyl);
        Assert.InRange(plan.PartitionBytes(plan.Partitions[0]), 511L << 20, 513L << 20);
    }

    [Fact]
    public void Build_A6000DataCard_VerifiesAndDiagnosesAsCompatible()
    {
        var dev = new MemoryBlockDevice(512 * TestSupport.MiB);
        var (profile, iface) = TestSupport.Iface("a6000-sd-data");
        var plan = LayoutPlanner.Recommend(profile, iface, IntendedUse.DataExchange, dev.Length);
        DiskImageBuilder.Apply(dev, plan, iface);

        var result = LayoutVerifier.Verify(dev, plan);
        Assert.True(result.Passed, string.Join("\n", result.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));

        var report = DiagnosticEngine.Analyse(result.Inspection, profile, iface);
        Assert.Equal(CompatibilityVerdict.Compatible, report.Verdict);
    }

    [Fact]
    public void Build_HardDriveCard_ClearsStaleMbrAndGpt()
    {
        var dev = new MemoryBlockDevice(256 * TestSupport.MiB);
        // Simulate a card that was previously GPT partitioned on Windows.
        var mbr = new MbrPartitionTable { Entries = [new MbrPartitionEntry(1, 0, MbrPartitionTypes.GptProtective, 1, (uint)(dev.SectorCount() - 1))] };
        dev.WriteSector(0, mbr.ToSector());
        var gpt = new byte[512];
        "EFI PART"u8.CopyTo(gpt);
        dev.WriteSector(dev.SectorCount() - 1, gpt);

        var (profile, iface) = TestSupport.Iface("a6000-sd-hdd");
        var plan = LayoutPlanner.Recommend(profile, iface, IntendedUse.HardDrive, dev.Length);
        DiskImageBuilder.Apply(dev, plan, iface);
        var result = LayoutVerifier.Verify(dev, plan);
        Assert.True(result.Passed, string.Join("\n", result.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));
        Assert.False(result.Inspection.HasBackupGptHeader);
    }

    [Fact]
    public void Build_UnformattedPartitions_AreReportedAsNeedingAmigaFormat()
    {
        var dev = new MemoryBlockDevice(256 * TestSupport.MiB);
        var (profile, iface) = TestSupport.Iface("a6000-sd-hdd");
        var plan = LayoutPlanner.Recommend(profile, iface, IntendedUse.HardDrive, dev.Length);
        plan = plan with { Partitions = plan.Partitions.Select(p => p with { FormatNow = false }).ToList() };
        DiskImageBuilder.Apply(dev, plan, iface);
        Assert.True(LayoutVerifier.Verify(dev, plan).Passed);

        var report = DiagnosticEngine.Analyse(DiskInspector.Inspect(dev), profile, iface);
        Assert.Equal(CompatibilityVerdict.NeedsAmigaFormat, report.Verdict);
        Assert.Contains(report.UninitializedCauses, f => f.Code.StartsWith("hdd.unformatted"));
    }

    [Fact]
    public void Build_WithEmbeddedHandler_VerifiesLsegContents()
    {
        var dev = new MemoryBlockDevice(256 * TestSupport.MiB);
        var (profile, iface) = TestSupport.Iface("a1200-ide");
        var plan = LayoutPlanner.Recommend(profile, iface, IntendedUse.HardDrive, dev.Length, compatibilityModeId: "ks32");
        plan = plan with
        {
            Partitions = plan.Partitions.Select(p => p with { DosType = DosType.Pfs3 }).ToList(),
            FileSystems = [new FileSystemDriverPlan(DosType.Pfs3, "testfs", TestSupport.FakeHandler(2000), 19 << 16 | 2, "19.2")],
        };
        DiskImageBuilder.Apply(dev, plan, iface);
        var result = LayoutVerifier.Verify(dev, plan);
        Assert.True(result.Passed, string.Join("\n", result.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")));
    }

    [Fact]
    public void Builder_RefusesInvalidPlan()
    {
        var dev = new MemoryBlockDevice(64 * TestSupport.MiB);
        var (profile, iface) = TestSupport.Iface("a6000-sd-hdd");
        var plan = LayoutPlanner.Recommend(profile, iface, IntendedUse.HardDrive, dev.Length);
        plan = plan with { Partitions = plan.Partitions.Select(p => p with { DeviceName = "BAD:NAME" }).ToList() };
        Assert.Throws<InvalidOperationException>(() => DiskImageBuilder.Apply(dev, plan, iface));
        Assert.True(dev.Data.AsSpan().IndexOfAnyExcept((byte)0) < 0, "Nothing may be written when validation fails.");
    }
}
