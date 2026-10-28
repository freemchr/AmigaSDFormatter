namespace AmigaSdCreator.Core.IO;

/// <summary>A raw disk image file (.img / .hdf) exposed as a block device.</summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly FileStream _stream;
    private readonly long _length;

    private FileBlockDevice(FileStream stream, long length, int sectorSize, bool canWrite, string path)
    {
        _stream = stream;
        _length = length;
        SectorSize = sectorSize;
        CanWrite = canWrite;
        Description = $"Image file {Path.GetFileName(path)}";
    }

    public string Description { get; }
    public long Length => _length;
    public int SectorSize { get; }
    public bool CanWrite { get; }

    public static FileBlockDevice OpenRead(string path, int sectorSize = 512)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        return new FileBlockDevice(fs, fs.Length - fs.Length % sectorSize, sectorSize, false, path);
    }

    public static FileBlockDevice OpenReadWrite(string path, int sectorSize = 512)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.RandomAccess);
        return new FileBlockDevice(fs, fs.Length - fs.Length % sectorSize, sectorSize, true, path);
    }

    /// <summary>Creates (or truncates) a sparse image file of exactly <paramref name="length"/> bytes.</summary>
    public static FileBlockDevice Create(string path, long length, int sectorSize = 512)
    {
        if (length <= 0 || length % sectorSize != 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Image length must be a positive multiple of the sector size.");
        var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.RandomAccess);
        fs.SetLength(length);
        return new FileBlockDevice(fs, length, sectorSize, true, path);
    }

    public void Read(long offset, Span<byte> buffer)
    {
        CheckRange(offset, buffer.Length);
        _stream.Position = offset;
        _stream.ReadExactly(buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new InvalidOperationException("Device was opened read-only.");
        CheckRange(offset, buffer.Length);
        _stream.Position = offset;
        _stream.Write(buffer);
    }

    public void Flush() => _stream.Flush(flushToDisk: true);

    private void CheckRange(long offset, int count)
    {
        if (offset < 0 || offset % SectorSize != 0 || count % SectorSize != 0)
            throw new ArgumentException("I/O must be sector aligned.");
        if (offset + count > _length)
            throw new ArgumentOutOfRangeException(nameof(offset), "I/O beyond end of device.");
    }

    public void Dispose() => _stream.Dispose();
}
