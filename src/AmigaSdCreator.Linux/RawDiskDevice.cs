using System.Runtime.Versioning;
using AmigaSdCreator.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace AmigaSdCreator.Linux;

/// <summary>A whole disk (/dev/sdX, /dev/mmcblkN) as a block device.</summary>
[SupportedOSPlatform("linux")]
public sealed class RawDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;

    internal RawDiskDevice(SafeFileHandle handle, string description, bool canWrite)
    {
        _handle = handle;
        Length = Native.GetLength(handle);
        if (Length < 0) Length = RandomAccess.GetLength(handle);
        SectorSize = Native.GetSectorSize(handle);
        if (SectorSize is < 512 or > 4096) SectorSize = 512;
        Description = description;
        CanWrite = canWrite;
    }

    public string Description { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public bool CanWrite { get; }

    public void Read(long offset, Span<byte> buffer)
    {
        Check(offset, buffer.Length);
        int done = 0;
        while (done < buffer.Length)
        {
            int n = Native.Read(_handle, buffer[done..], offset + done);
            if (n <= 0) throw new IOException($"Unexpected end of device at byte {offset + done:N0}. The card may have been removed.");
            done += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new InvalidOperationException("Device was opened read-only.");
        Check(offset, buffer.Length);
        Native.Write(_handle, buffer, offset);
    }

    public void Flush()
    {
        if (CanWrite) Native.Sync(_handle);
    }

    private void Check(long offset, int count)
    {
        if (offset < 0 || offset % SectorSize != 0 || count % SectorSize != 0)
            throw new ArgumentException("Disk I/O must be sector aligned.");
        if (offset + count > Length) throw new ArgumentOutOfRangeException(nameof(offset), "I/O beyond the end of the disk.");
    }

    internal SafeFileHandle Handle => _handle;

    public void Dispose() => _handle.Dispose();
}
