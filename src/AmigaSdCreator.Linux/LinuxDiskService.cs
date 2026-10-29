using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AmigaSdCreator.Core.IO;
using AmigaSdCreator.Core.Safety;

namespace AmigaSdCreator.Linux;

/// <summary>
/// Disk discovery with lsblk (the disk / partition tree, bus and identity) plus the kernel's own
/// mount and swap tables, and raw access through /dev. A disk is treated as a system disk when any
/// partition, or anything stacked on one (LVM, LUKS, RAID), holds the root, /boot, /usr, /var or
/// /home filesystem, or swap.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDiskService : IDiskService
{
    private const string LsblkColumns = "NAME,PATH,TYPE,SIZE,MODEL,VENDOR,SERIAL,WWN,TRAN,RM,HOTPLUG,RO,PTTYPE,LOG-SEC,PHY-SEC,MAJ:MIN";

    /// <summary>Mount points that make a disk part of the running system.</summary>
    private static readonly string[] SystemMounts = ["/", "/boot", "/boot/efi", "/efi", "/usr", "/var", "/home", "/opt", "/srv"];

    /// <summary>Lists loop devices too (as virtual disks), so the device layer can be tested with image files.</summary>
    public bool IncludeLoopDevices { get; init; } = Environment.GetEnvironmentVariable("ASDC_INCLUDE_LOOP") == "1";

    public bool IsElevated => Native.GetEUid() == 0;

    public Task<IReadOnlyList<DiskDescriptor>> EnumerateAsync(CancellationToken ct = default) =>
        Task.Run(() => Enumerate(), ct);

    public IReadOnlyList<DiskDescriptor> Enumerate()
    {
        string json = RunLsblk();
        var mounts = MountTable.Read();
        var swaps = SwapDevices();
        return Parse(json, mounts, swaps, IncludeLoopDevices);
    }

    /// <summary>Builds descriptors from lsblk JSON. Pure, so it can be tested with captured output.</summary>
    internal static IReadOnlyList<DiskDescriptor> Parse(string lsblkJson, IReadOnlyList<MountEntry> mounts, IReadOnlySet<string> swapDevices, bool includeLoop)
    {
        using var doc = JsonDocument.Parse(lsblkJson);
        var disks = new List<DiskDescriptor>();
        foreach (var d in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            string type = Str(d, "type");
            string name = Str(d, "name");
            bool loop = type == "loop";
            if (!(type == "disk" || loop && includeLoop)) continue;
            if (name.StartsWith("zram", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)) continue;

            var nodes = Descendants(d).ToList();
            var nodeIds = nodes.Select(n => Str(n, "maj:min")).ToHashSet();
            var nodePaths = nodes.Select(n => Str(n, "path")).ToHashSet();
            var diskMounts = mounts.Where(m => nodeIds.Contains(m.Device)).ToList();
            bool system = diskMounts.Any(m => SystemMounts.Contains(m.MountPoint));
            bool swap = nodePaths.Any(swapDevices.Contains);

            // FAT partitions mounted by the desktop (or by hand), in partition order, for the Files page.
            var partitions = d.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array
                ? ch.EnumerateArray().Where(c => Str(c, "type") == "part").ToList() : [];
            var fatMounts = partitions
                .Select(p => diskMounts.Where(m => m.Device == Str(p, "maj:min") && m.FsType is "vfat" or "msdos" or "exfat")
                                       .Select(m => m.MountPoint).OrderBy(m => m.Length).FirstOrDefault())
                .OfType<string>().ToList();

            string tran = Str(d, "tran");
            var bus = loop ? StorageBus.FileBackedVirtual : MapBus(tran, name);
            long size = Long(d, "size");
            string vendor = Str(d, "vendor").Trim();
            string model = Str(d, "model").Trim();
            string path = Str(d, "path");
            string majMin = Str(d, "maj:min");
            disks.Add(new DiskDescriptor
            {
                Number = DiskNumber(majMin),
                DevicePath = path,
                Location = path,
                FriendlyName = string.Join(' ', new[] { vendor, model }.Where(s => s.Length > 0).Distinct()),
                Manufacturer = vendor,
                Model = model,
                SerialNumber = Str(d, "serial").Trim(),
                UniqueId = Str(d, "wwn").Trim(),
                SizeBytes = size,
                LogicalSectorSize = (int)Math.Max(512, Long(d, "log-sec")),
                PhysicalSectorSize = (int)Math.Max(512, Long(d, "phy-sec")),
                Bus = bus,
                IsRemovableMedia = Bool(d, "rm") || Bool(d, "hotplug"),
                IsSystem = system,
                IsBoot = diskMounts.Any(m => m.MountPoint is "/boot" or "/boot/efi" or "/efi"),
                HoldsPageFile = swap,
                IsReadOnly = Bool(d, "ro"),
                HasMedia = size > 0,
                PartitionStyle = Str(d, "pttype") switch { "dos" => "MBR", "gpt" => "GPT", "" => "RAW", var other => other.ToUpperInvariant() },
                MountPoints = fatMounts,
                VolumePaths = diskMounts.Select(m => m.MountPoint).Distinct().ToList(),
            });
        }
        return disks.OrderBy(d => d.DevicePath, StringComparer.Ordinal).ToList();
    }

    public IBlockDevice OpenRead(DiskDescriptor disk) =>
        new RawDiskDevice(Native.Open(disk.DevicePath, write: false), $"{disk.DisplayName} ({disk.DevicePath})", canWrite: false);

    public IExclusiveDiskHandle OpenForWrite(DeviceIdentity identity)
    {
        var current = Enumerate().FirstOrDefault(d => d.DevicePath == identity.DevicePath)
            ?? throw new IOException("The selected card is no longer connected. Nothing was written.");
        if (current.Identity != identity)
            throw new IOException("The device at this path is not the card that was selected. Nothing was written.");
        if (current.IsSystem || current.IsBoot || current.HoldsPageFile)
            throw new UnauthorizedAccessException("Refusing to open a Linux system disk for writing.");

        // Deepest mounts first, so that nested mounts are released before their parents.
        foreach (var mount in current.VolumePaths.OrderByDescending(m => m.Length))
            UnmountWithRetry(mount);

        var handle = Native.Open(identity.DevicePath, write: true);
        var device = new RawDiskDevice(handle, $"{current.DisplayName} ({current.DevicePath})", canWrite: true);
        if (device.Length != identity.SizeBytes || device.SectorSize != identity.SectorSize)
        {
            device.Dispose();
            throw new IOException("The opened disk's size does not match the selected card. Nothing was written.");
        }
        return new ExclusiveDiskHandle(device);
    }

    private static void UnmountWithRetry(string mountPoint)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Native.Unmount(mountPoint);
                return;
            }
            catch (IOException ex) when (attempt < 9)
            {
                Debug.WriteLine(ex.Message);
                Thread.Sleep(300);
            }
            catch (IOException ex)
            {
                throw new IOException($"Linux could not release the card ({mountPoint}). Close any file manager windows or programs using it and try again. {ex.Message}", ex);
            }
        }
    }

    private static string RunLsblk()
    {
        var psi = new ProcessStartInfo("lsblk", ["--json", "--bytes", "--tree", "-o", LsblkColumns])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi) ?? throw new IOException("lsblk could not be started.");
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"lsblk failed: {error.Trim()}");
        return output;
    }

    private static HashSet<string> SwapDevices()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/swaps").Skip(1))
            {
                var dev = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (dev is not null) set.Add(dev.Replace("\\040", " "));
            }
        }
        catch (IOException) { }
        return set;
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var c in children.EnumerateArray())
                foreach (var d in Descendants(c)) yield return d;
    }

    internal static StorageBus MapBus(string tran, string name) => tran switch
    {
        "usb" => StorageBus.Usb,
        "mmc" => StorageBus.Mmc,
        "sata" => StorageBus.Sata,
        "ata" => StorageBus.Ata,
        "nvme" => StorageBus.Nvme,
        "sas" => StorageBus.Sas,
        "scsi" or "spi" or "iscsi" or "fc" => StorageBus.Scsi,
        "virtio" => StorageBus.Virtual,
        _ when name.StartsWith("mmcblk", StringComparison.Ordinal) => StorageBus.Mmc,
        _ when name.StartsWith("nvme", StringComparison.Ordinal) => StorageBus.Nvme,
        _ when name.StartsWith("vd", StringComparison.Ordinal) || name.StartsWith("xvd", StringComparison.Ordinal) => StorageBus.Virtual,
        "" => StorageBus.Unknown,
        _ => StorageBus.Other,
    };

    /// <summary>A stable number per device node (major/minor), so the identity survives other disks coming and going.</summary>
    private static int DiskNumber(string majMin)
    {
        var parts = majMin.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor) ? major << 20 | minor : 0;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt64(),
            JsonValueKind.String when long.TryParse(v.GetString(), out long l) => l,
            _ => 0,
        } : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "1");

    private sealed class ExclusiveDiskHandle(RawDiskDevice device) : IExclusiveDiskHandle
    {
        public IBlockDevice Device => device;

        public void RefreshPartitionTable()
        {
            device.Flush();
            // The kernel and udev pick up the new table when the card is reinserted if this is refused.
            for (int attempt = 0; attempt < 5 && !Native.RereadPartitionTable(device.Handle); attempt++) Thread.Sleep(200);
        }

        public void Dispose() => device.Dispose();
    }
}

/// <summary>One line of /proc/self/mountinfo: the mounted device (major:minor), where, and the filesystem.</summary>
internal sealed record MountEntry(string Device, string MountPoint, string FsType, string Source);

internal static class MountTable
{
    public static IReadOnlyList<MountEntry> Read()
    {
        try { return Parse(File.ReadAllLines("/proc/self/mountinfo")); }
        catch (IOException) { return []; }
    }

    /// <summary>Format: id parent major:minor root mountpoint options [optional...] - fstype source superoptions.</summary>
    public static IReadOnlyList<MountEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<MountEntry>();
        foreach (var line in lines)
        {
            var f = line.Split(' ');
            int dash = Array.IndexOf(f, "-");
            if (f.Length < 5 || dash < 0 || dash + 2 >= f.Length) continue;
            list.Add(new MountEntry(f[2], Unescape(f[4]), f[dash + 1], Unescape(f[dash + 2])));
        }
        return list;
    }

    /// <summary>mountinfo escapes space, tab, newline and backslash as \ooo octal.</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && IsOctal(s, i + 1))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOctal(string s, int at) =>
        at + 3 <= s.Length && s.Substring(at, 3).All(c => c is >= '0' and <= '7');
}
