using System.Diagnostics;
using System.Runtime.Versioning;
using AmigaSdCreator.Core.Safety;

namespace AmigaSdCreator.Linux;

/// <summary>The desktop user behind the process (the one who ran pkexec or sudo when running as root).</summary>
[SupportedOSPlatform("linux")]
public sealed record DesktopUser(string Name, uint Uid, uint Gid, string Home)
{
    public static DesktopUser Current { get; } = Detect();

    /// <summary>True when the app runs as root on behalf of another user.</summary>
    public bool IsDelegated => Uid != 0 && Native.GetEUid() == 0;

    private static DesktopUser Detect()
    {
        string? uidText = Environment.GetEnvironmentVariable("PKEXEC_UID") ?? Environment.GetEnvironmentVariable("SUDO_UID");
        uint uid = uint.TryParse(uidText, out var u) ? u : Native.GetEUid();
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length >= 6 && uint.TryParse(f[2], out var lineUid) && lineUid == uid && uint.TryParse(f[3], out var gid))
                    return new DesktopUser(f[0], uid, gid, f[5]);
            }
        }
        catch (IOException) { }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new DesktopUser(Environment.UserName, uid, uid, home);
    }

    /// <summary>XDG data folder of the desktop user, e.g. ~/.local/share.</summary>
    public string DataHome => IsDelegated || Environment.GetEnvironmentVariable("XDG_DATA_HOME") is not { Length: > 0 }
        ? Path.Combine(Home, ".local", "share")
        : Environment.GetEnvironmentVariable("XDG_DATA_HOME")!;

    /// <summary>Gives a file created by the root process back to the desktop user.</summary>
    public void Claim(string path)
    {
        if (!IsDelegated) return;
        // Only inside the user's home: never hand over system paths or other mounts.
        if (!path.StartsWith(Home.TrimEnd('/') + "/", StringComparison.Ordinal)) return;
        _ = Native.Chown(path, Uid, Gid);
    }
}

[SupportedOSPlatform("linux")]
public static class LinuxDesktop
{
    public const string PolicyAction = "au.com.techgurus.amigasdcreator";

    /// <summary>
    /// Starts the app again as root through pkexec (the polkit password prompt). The installed
    /// package ships a polkit action for the binary that keeps DISPLAY and XAUTHORITY; when running
    /// from a build folder, env passes them explicitly. Returns false if pkexec is missing.
    /// </summary>
    public static bool TryRestartElevated()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the application path.");
        string? pkexec = FindOnPath("pkexec");
        if (pkexec is null) return false;

        var psi = new ProcessStartInfo(pkexec) { UseShellExecute = false };
        bool installed = exe.StartsWith("/usr/", StringComparison.Ordinal) || exe.StartsWith("/opt/", StringComparison.Ordinal);
        if (!installed)
        {
            psi.ArgumentList.Add("env");
            foreach (var name in new[] { "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "LANG" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) psi.ArgumentList.Add($"{name}={v}");
        }
        psi.ArgumentList.Add(exe);
        try
        {
            // pkexec blocks until the elevated app exits, so the caller should shut down after this returns.
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Home, the root folder, and removable media / network mounts, for the file manager.</summary>
    public static IReadOnlyList<(string Name, string Path)> ListPlaces()
    {
        var user = DesktopUser.Current;
        var list = new List<(string, string)> { ("Home", user.Home), ("Computer (/)", "/") };
        foreach (var m in MountTable.Read())
        {
            bool media = m.MountPoint.StartsWith("/media/", StringComparison.Ordinal) || m.MountPoint.StartsWith("/run/media/", StringComparison.Ordinal)
                || m.MountPoint.StartsWith("/mnt/", StringComparison.Ordinal) && !m.MountPoint.StartsWith("/mnt/wsl", StringComparison.Ordinal);
            bool network = m.FsType is "cifs" or "smb3" or "nfs" or "nfs4" or "sshfs" or "fuse.sshfs" or "9p";
            if (!media && !network) continue;
            string kind = network ? "network" : m.FsType is "vfat" or "exfat" or "msdos" ? "removable" : m.FsType;
            list.Add(($"{Path.GetFileName(m.MountPoint)} ({kind})", m.MountPoint));
        }
        string gvfs = $"/run/user/{user.Uid}/gvfs";
        try
        {
            if (Directory.Exists(gvfs))
                foreach (var share in Directory.EnumerateDirectories(gvfs)) list.Add(($"{Path.GetFileName(share)} (network)", share));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.DistinctBy(p => p.Item2).ToList();
    }

    /// <summary>
    /// Finds the card's first partition and returns where it is mounted. If the desktop has not
    /// mounted it, it is mounted under /run (we are root) and unmounted again when disposed.
    /// </summary>
    public static async Task<(string Root, Action? Release)> MountFirstPartitionAsync(LinuxDiskService disks, DeviceIdentity identity, CancellationToken ct)
    {
        string? partition = null;
        for (int i = 0; i < 20; i++)
        {
            var disk = disks.Enumerate().FirstOrDefault(d => d.DevicePath == identity.DevicePath && d.SerialNumber == identity.SerialNumber);
            if (disk?.MountPoints.Count > 0) return (disk.MountPoints[0], null);
            partition ??= FirstPartition(identity.DevicePath);
            if (partition is not null && i >= 3) break; // give the desktop's automounter a moment first
            await Task.Delay(1000, ct);
        }
        if (partition is null) throw new IOException("Linux did not find the boot partition. Remove and reinsert the card, then copy the Emu68 files manually.");

        string target = Path.Combine("/run", $"amiga-sd-creator-{Environment.ProcessId}");
        Directory.CreateDirectory(target);
        await RunAsync("mount", ["-t", "vfat", "-o", "uid=0,gid=0,umask=022,flush", partition, target], ct);
        return (target, () =>
        {
            try
            {
                RunAsync("umount", [target], CancellationToken.None).GetAwaiter().GetResult();
                Directory.Delete(target);
            }
            catch (IOException) { }
        });
    }

    /// <summary>/dev/sdb -> /dev/sdb1, /dev/mmcblk0 -> /dev/mmcblk0p1 (as created by the kernel after the table is re-read).</summary>
    private static string? FirstPartition(string disk)
    {
        string name = Path.GetFileName(disk);
        string candidate = char.IsDigit(name[^1]) ? $"{disk}p1" : $"{disk}1";
        return File.Exists(candidate) || Directory.Exists($"/sys/class/block/{Path.GetFileName(candidate)}") ? candidate : null;
    }

    private static async Task RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi) ?? throw new IOException($"{file} could not be started.");
        string error = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new IOException($"{file} failed: {error.Trim()}");
    }

    public static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    /// <summary>Opens a folder or URL with xdg-open, as the desktop user when running as root.</summary>
    public static void Open(string pathOrUrl)
    {
        var user = DesktopUser.Current;
        var psi = user.IsDelegated && FindOnPath("runuser") is { } runuser
            ? new ProcessStartInfo(runuser, ["-u", user.Name, "--", "xdg-open", pathOrUrl])
            : new ProcessStartInfo("xdg-open", [pathOrUrl]);
        psi.UseShellExecute = false;
        try { Process.Start(psi)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>Raises <see cref="Changed"/> (debounced) when block device nodes appear in or vanish from /dev.</summary>
[SupportedOSPlatform("linux")]
public sealed class DeviceChangeWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;

    public event EventHandler? Changed;

    public DeviceChangeWatcher()
    {
        _debounce = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        try
        {
            _watcher = new FileSystemWatcher("/dev") { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Device change notifications unavailable: {ex.Message}");
            _watcher = null;
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (e.Name is { } n && (n.StartsWith("sd", StringComparison.Ordinal) || n.StartsWith("mmcblk", StringComparison.Ordinal) || n.StartsWith("nvme", StringComparison.Ordinal)))
            _debounce.Change(750, Timeout.Infinite);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
