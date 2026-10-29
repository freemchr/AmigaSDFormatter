using System.Runtime.Versioning;
using AmigaSdCreator.Core.Safety;
using AmigaSdCreator.Linux;

namespace AmigaSdCreator.Core.Tests;

/// <summary>
/// The Linux disk layer's parsing of lsblk and /proc/self/mountinfo. These are pure functions and
/// run on any OS; opening real devices needs root and a card, so it is covered by hardware testing.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxDiskTests
{
    // A laptop: NVMe system disk with LUKS + LVM, an SD card in a USB reader (FAT32, mounted by the
    // desktop), a built-in mmc reader holding an Amiga card, an empty reader slot and a loop device.
    private const string Lsblk = """
    {"blockdevices": [
      {"name":"nvme0n1","path":"/dev/nvme0n1","type":"disk","size":512110190592,"model":"Samsung SSD 980","vendor":null,"serial":"S64ANS0R1","wwn":"eui.0025","tran":"nvme","rm":false,"hotplug":false,"ro":false,"pttype":"gpt","log-sec":512,"phy-sec":512,"maj:min":"259:0",
       "children":[
         {"name":"nvme0n1p1","path":"/dev/nvme0n1p1","type":"part","size":536870912,"maj:min":"259:1"},
         {"name":"nvme0n1p2","path":"/dev/nvme0n1p2","type":"part","size":511571132416,"maj:min":"259:2",
          "children":[{"name":"luks","path":"/dev/mapper/luks","type":"crypt","size":511554355200,"maj:min":"253:0",
            "children":[{"name":"vg-root","path":"/dev/mapper/vg-root","type":"lvm","size":500000000000,"maj:min":"253:1"},
                        {"name":"vg-swap","path":"/dev/mapper/vg-swap","type":"lvm","size":8000000000,"maj:min":"253:2"}]}]}]},
      {"name":"sdb","path":"/dev/sdb","type":"disk","size":31914983424,"model":"SD/MMC Reader","vendor":"Generic ","serial":"000000001234","wwn":null,"tran":"usb","rm":true,"hotplug":true,"ro":false,"pttype":"dos","log-sec":512,"phy-sec":512,"maj:min":"8:16",
       "children":[{"name":"sdb1","path":"/dev/sdb1","type":"part","size":31913934848,"maj:min":"8:17"}]},
      {"name":"sdc","path":"/dev/sdc","type":"disk","size":0,"model":"MS Reader","vendor":"Generic ","serial":"000000005678","wwn":null,"tran":"usb","rm":true,"hotplug":true,"ro":false,"pttype":null,"log-sec":512,"phy-sec":512,"maj:min":"8:32"},
      {"name":"mmcblk0","path":"/dev/mmcblk0","type":"disk","size":7948206080,"model":null,"vendor":null,"serial":"0x1234abcd","wwn":null,"tran":null,"rm":false,"hotplug":false,"ro":false,"pttype":null,"log-sec":512,"phy-sec":512,"maj:min":"179:0"},
      {"name":"loop0","path":"/dev/loop0","type":"loop","size":268435456,"model":null,"vendor":null,"serial":null,"wwn":null,"tran":null,"rm":false,"hotplug":false,"ro":false,"pttype":"dos","log-sec":512,"phy-sec":512,"maj:min":"7:0"}
    ]}
    """;

    private static readonly string[] MountInfo =
    [
        "22 1 253:1 / / rw,relatime shared:1 - ext4 /dev/mapper/vg-root rw",
        "23 22 259:1 / /boot/efi rw,relatime shared:2 - vfat /dev/nvme0n1p1 rw,fmask=0077",
        @"60 22 8:17 / /media/chris/SD\0400 rw,nosuid,nodev,relatime shared:30 - vfat /dev/sdb1 rw,uid=1000",
        "61 22 0:50 / /run/user/1000/gvfs rw,nosuid,nodev - fuse.gvfsd-fuse gvfsd-fuse rw",
    ];

    private static IReadOnlyList<DiskDescriptor> Parse(bool loop = false) =>
        LinuxDiskService.Parse(Lsblk, MountTable.Parse(MountInfo), new HashSet<string> { "/dev/mapper/vg-swap" }, loop);

    [Fact]
    public void SystemDisk_IsDetectedThroughLuksAndLvm()
    {
        var nvme = Parse().Single(d => d.DevicePath == "/dev/nvme0n1");
        Assert.True(nvme.IsSystem);
        Assert.True(nvme.IsBoot);
        Assert.True(nvme.HoldsPageFile);
        Assert.Equal(StorageBus.Nvme, nvme.Bus);
        Assert.NotNull(WriteGuard.ProtectionReason(nvme, new SafetyPolicy { AllowNonRemovableBus = true, AllowLargeDisks = true }));
    }

    [Fact]
    public void UsbCard_IsAWritableTarget_WithItsFatMountPoint()
    {
        var card = Parse().Single(d => d.DevicePath == "/dev/sdb");
        Assert.False(card.IsSystem || card.IsBoot || card.HoldsPageFile);
        Assert.Equal(StorageBus.Usb, card.Bus);
        Assert.True(card.IsCardReaderBus);
        Assert.True(card.HasMedia);
        Assert.Equal("MBR", card.PartitionStyle);
        Assert.Equal("Generic SD/MMC Reader", card.FriendlyName);
        Assert.Equal(["/media/chris/SD 0"], card.MountPoints);   // \040 in mountinfo is a space
        Assert.Equal(["/media/chris/SD 0"], card.VolumePaths);   // unmounted before writing
        Assert.Equal("/dev/sdb", card.LocationText);
        Assert.Null(WriteGuard.ProtectionReason(card, new SafetyPolicy()));
    }

    [Fact]
    public void EmptyReaderSlot_HasNoMedia()
    {
        var slot = Parse().Single(d => d.DevicePath == "/dev/sdc");
        Assert.False(slot.HasMedia);
    }

    [Fact]
    public void BuiltInMmcReader_IsTreatedAsCardReader()
    {
        var mmc = Parse().Single(d => d.DevicePath == "/dev/mmcblk0");
        Assert.Equal(StorageBus.Mmc, mmc.Bus);
        Assert.Equal("RAW", mmc.PartitionStyle);
        Assert.Equal("/dev/mmcblk0", mmc.DisplayName);
    }

    [Fact]
    public void LoopDevices_AreHiddenUnlessRequested()
    {
        Assert.DoesNotContain(Parse(), d => d.DevicePath == "/dev/loop0");
        Assert.Equal(StorageBus.FileBackedVirtual, Parse(loop: true).Single(d => d.DevicePath == "/dev/loop0").Bus);
    }

    [Fact]
    public void DiskNumbers_AreStableAndUnique()
    {
        var a = Parse();
        Assert.Equal(a.Count, a.Select(d => d.Number).Distinct().Count());
        Assert.Equal(a.Select(d => d.Identity), Parse().Select(d => d.Identity));
    }

    [Fact]
    public void RawDevice_ReadsAndWritesAFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = TestSupport.TempFile();
        try
        {
            File.WriteAllBytes(path, new byte[4 * 512]);
            var service = new LinuxDiskService();
            var disk = new DiskDescriptor { Number = 1, DevicePath = path, SizeBytes = 4 * 512 };
            using (var dev = service.OpenRead(disk))
            {
                Assert.Equal(4 * 512, dev.Length);
                Assert.False(dev.CanWrite);
                var buf = new byte[512];
                dev.Read(512, buf);
                Assert.All(buf, b => Assert.Equal(0, b));
            }
        }
        finally { File.Delete(path); }
    }
}
