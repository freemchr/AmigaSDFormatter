namespace AmigaSdCreator.Core.IO;

/// <summary>
/// A sector-addressable storage target: a disk image file, an in-memory buffer or a physical disk.
/// All offsets are in bytes but must be multiples of <see cref="SectorSize"/>.
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>Human readable description used in logs and reports (never contains user data).</summary>
    string Description { get; }

    /// <summary>Total addressable length in bytes.</summary>
    long Length { get; }

    /// <summary>Logical sector size in bytes (512 for nearly all SD cards).</summary>
    int SectorSize { get; }

    bool CanWrite { get; }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes from <paramref name="offset"/>.</summary>
    void Read(long offset, Span<byte> buffer);

    /// <summary>Writes the whole <paramref name="buffer"/> at <paramref name="offset"/>.</summary>
    void Write(long offset, ReadOnlySpan<byte> buffer);

    /// <summary>Flushes any cached writes to the underlying medium.</summary>
    void Flush();
}

public static class BlockDeviceExtensions
{
    public static long SectorCount(this IBlockDevice device) => device.Length / device.SectorSize;

    public static byte[] ReadSector(this IBlockDevice device, long lba)
    {
        var buffer = new byte[device.SectorSize];
        device.Read(lba * device.SectorSize, buffer);
        return buffer;
    }

    public static byte[] ReadSectors(this IBlockDevice device, long lba, int count)
    {
        var buffer = new byte[checked(device.SectorSize * count)];
        device.Read(lba * device.SectorSize, buffer);
        return buffer;
    }

    public static void WriteSector(this IBlockDevice device, long lba, ReadOnlySpan<byte> data)
    {
        if (data.Length != device.SectorSize)
            throw new ArgumentException($"Sector data must be exactly {device.SectorSize} bytes.", nameof(data));
        device.Write(lba * device.SectorSize, data);
    }

    /// <summary>Writes zeros over a byte range in large chunks. Range must be sector aligned.</summary>
    public static void Zero(this IBlockDevice device, long offset, long length, CancellationToken cancellationToken = default)
    {
        if (length <= 0) return;
        var chunk = new byte[(int)Math.Min(length, 1024 * 1024)];
        long done = 0;
        while (done < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk.Length, length - done);
            device.Write(offset + done, chunk.AsSpan(0, n));
            done += n;
        }
    }
}
