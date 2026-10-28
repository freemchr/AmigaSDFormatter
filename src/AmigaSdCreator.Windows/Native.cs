using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AmigaSdCreator.Windows;

internal static partial class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileFlagWriteThrough = 0x80000000;

    public const uint IoctlDiskGetLengthInfo = 0x0007405C;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlDiskUpdateProperties = 0x00070140;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlDismountVolume = 0x00090020;
    public const uint FsctlUnlockVolume = 0x0009001C;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle handle);

    public static SafeFileHandle Open(string path, bool write)
    {
        var h = CreateFile(path, GenericRead | (write ? GenericWrite : 0), FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            write ? FileFlagWriteThrough : 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw err == 5
                ? new UnauthorizedAccessException($"Access to {path} was denied. Administrator rights are required for direct card access.")
                : new IOException($"Could not open {path} (Windows error {err}: {Marshal.GetPInvokeErrorMessage(err)}).", err);
        }
        return h;
    }

    public static unsafe long GetLength(SafeFileHandle h)
    {
        long len;
        if (!DeviceIoControl(h, IoctlDiskGetLengthInfo, null, 0, &len, sizeof(long), out _, IntPtr.Zero))
            throw LastError("Reading the disk size");
        return len;
    }

    /// <summary>Returns BytesPerSector from DISK_GEOMETRY_EX.</summary>
    public static unsafe int GetSectorSize(SafeFileHandle h)
    {
        var buf = stackalloc byte[256];
        if (!DeviceIoControl(h, IoctlDiskGetDriveGeometryEx, null, 0, buf, 256, out _, IntPtr.Zero))
            throw LastError("Reading the disk geometry");
        return *(int*)(buf + 20); // DISK_GEOMETRY: Cylinders(8) MediaType(4) TracksPerCylinder(4) SectorsPerTrack(4) BytesPerSector(4)
    }

    public static unsafe void Ioctl(SafeFileHandle h, uint code, string what)
    {
        if (!DeviceIoControl(h, code, null, 0, null, 0, out _, IntPtr.Zero)) throw LastError(what);
    }

    /// <summary>Disk numbers that a volume spans (VOLUME_DISK_EXTENTS).</summary>
    public static unsafe IReadOnlyList<int> VolumeDiskNumbers(SafeFileHandle volume)
    {
        var buf = stackalloc byte[1024];
        if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, null, 0, buf, 1024, out _, IntPtr.Zero)) return [];
        int count = *(int*)buf;
        var result = new List<int>();
        for (int i = 0; i < count && i < 40; i++) result.Add(*(int*)(buf + 8 + i * 24)); // DISK_EXTENT: DiskNumber(4) pad(4) Start(8) Length(8)
        return result;
    }

    public static IOException LastError(string what)
    {
        int err = Marshal.GetLastPInvokeError();
        return new IOException($"{what} failed (Windows error {err}: {Marshal.GetPInvokeErrorMessage(err)}).", err);
    }
}
