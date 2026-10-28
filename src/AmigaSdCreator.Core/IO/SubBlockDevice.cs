namespace AmigaSdCreator.Core.IO;

/// <summary>A window onto part of another device (e.g. one partition). Does not own the parent.</summary>
public sealed class SubBlockDevice : IBlockDevice
{
    private readonly IBlockDevice _parent;
    private readonly long _offset;

    public SubBlockDevice(IBlockDevice parent, long offset, long length)
    {
        if (offset < 0 || length <= 0 || offset + length > parent.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Range lies outside the parent device.");
        if (offset % parent.SectorSize != 0 || length % parent.SectorSize != 0)
            throw new ArgumentException("Range must be sector aligned.");
        _parent = parent;
        _offset = offset;
        Length = length;
    }

    public string Description => $"{_parent.Description} @ {_offset}";
    public long Length { get; }
    public int SectorSize => _parent.SectorSize;
    public bool CanWrite => _parent.CanWrite;

    public void Read(long offset, Span<byte> buffer)
    {
        if (offset + buffer.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        _parent.Read(_offset + offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (offset + buffer.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        _parent.Write(_offset + offset, buffer);
    }

    public void Flush() => _parent.Flush();
    public void Dispose() { }
}
