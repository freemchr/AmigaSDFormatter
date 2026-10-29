using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AmigaSdCreator.Linux;

/// <summary>libc calls for raw block-device access. Constants are the Linux values shared by x86-64 and arm64.</summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class Native
{
    private const int O_RDONLY = 0, O_RDWR = 2, O_EXCL = 0x80, O_CLOEXEC = 0x80000;
    private const int EINTR = 4;
    internal const ulong BLKGETSIZE64 = 0x80081272, BLKSSZGET = 0x1268, BLKRRPART = 0x125F;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenRaw(string path, int flags);

    [LibraryImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static partial nint PRead(SafeFileHandle fd, byte* buf, nuint count, long offset);

    [LibraryImport("libc", EntryPoint = "pwrite", SetLastError = true)]
    private static partial nint PWrite(SafeFileHandle fd, byte* buf, nuint count, long offset);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int FSync(SafeFileHandle fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(SafeFileHandle fd, ulong request, void* arg);

    [LibraryImport("libc", EntryPoint = "umount2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UMount2(string target, int flags);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEUid();

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Chown(string path, uint uid, uint gid);

    /// <summary>
    /// Opens a block device. For writing, O_EXCL makes the kernel refuse the open while any partition
    /// of the disk is still mounted or otherwise claimed.
    /// </summary>
    public static SafeFileHandle Open(string path, bool write)
    {
        int flags = (write ? O_RDWR | O_EXCL : O_RDONLY) | O_CLOEXEC;
        int fd;
        do fd = OpenRaw(path, flags); while (fd < 0 && Marshal.GetLastPInvokeError() == EINTR);
        if (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            string why = err switch
            {
                13 => "permission denied (root rights are needed)",
                16 => "the device is busy - a partition is still mounted or in use",
                2 or 6 => "the device is gone",
                _ => new Win32Exception(err).Message,
            };
            if (err == 13) throw new UnauthorizedAccessException($"Cannot open {path}: {why}.");
            throw new IOException($"Cannot open {path}: {why}.");
        }
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    public static int Read(SafeFileHandle fd, Span<byte> buffer, long offset)
    {
        fixed (byte* p = buffer)
        {
            nint n;
            do n = PRead(fd, p, (nuint)buffer.Length, offset); while (n < 0 && Marshal.GetLastPInvokeError() == EINTR);
            if (n < 0) throw LastError("Reading the card");
            return (int)n;
        }
    }

    public static void Write(SafeFileHandle fd, ReadOnlySpan<byte> buffer, long offset)
    {
        fixed (byte* p = buffer)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                nint n = PWrite(fd, p + done, (nuint)(buffer.Length - done), offset + done);
                if (n < 0 && Marshal.GetLastPInvokeError() == EINTR) continue;
                if (n <= 0) throw LastError("Writing the card");
                done += (int)n;
            }
        }
    }

    public static void Sync(SafeFileHandle fd)
    {
        if (FSync(fd) != 0) throw LastError("Flushing the card");
    }

    /// <summary>Size in bytes, or -1 if the handle is not a block device (e.g. a regular file in tests).</summary>
    public static long GetLength(SafeFileHandle fd)
    {
        ulong size;
        return Ioctl(fd, BLKGETSIZE64, &size) == 0 ? (long)size : -1;
    }

    public static int GetSectorSize(SafeFileHandle fd)
    {
        int size;
        return Ioctl(fd, BLKSSZGET, &size) == 0 ? size : 512;
    }

    /// <summary>Asks the kernel to re-read the partition table. Returns false if it was busy.</summary>
    public static bool RereadPartitionTable(SafeFileHandle fd) => Ioctl(fd, BLKRRPART, null) == 0;

    public static void Unmount(string mountPoint)
    {
        if (UMount2(mountPoint, 0) != 0) throw LastError($"Unmounting {mountPoint}");
    }

    public static IOException LastError(string what) =>
        new($"{what} failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
}
