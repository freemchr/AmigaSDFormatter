namespace AmigaSdCreator.Core.IO;

/// <summary>In-memory block device, used for tests and for previewing layouts.</summary>
public sealed class MemoryBlockDevice : IBlockDevice
{
    private readonly byte[] _data;

    public MemoryBlockDevice(long length, int sectorSize = 512)
    {
        if (length % sectorSize != 0) throw new ArgumentException("Length must be sector aligned.", nameof(length));
        _data = new byte[checked((int)length)];
        SectorSize = sectorSize;
    }

    public string Description => $"Memory device ({_data.Length} bytes)";
    public long Length => _data.Length;
    public int SectorSize { get; }
    public bool CanWrite => true;
    public byte[] Data => _data;

    public void Read(long offset, Span<byte> buffer)
    {
        Check(offset, buffer.Length);
        _data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        Check(offset, buffer.Length);
        buffer.CopyTo(_data.AsSpan((int)offset));
    }

    public void Flush() { }

    private void Check(long offset, int count)
    {
        if (offset < 0 || offset % SectorSize != 0 || count % SectorSize != 0)
            throw new ArgumentException("I/O must be sector aligned.");
        if (offset + count > _data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
    }

    public void Dispose() { }
}
