using System.Buffers.Binary;
using System.Text;

namespace AmigaSdCreator.Core.IO;

/// <summary>Helpers for big-endian (Amiga) and little-endian (PC) structures.</summary>
internal static class Endian
{
    public static uint U32BE(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32BigEndian(b[offset..]);
    public static int I32BE(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadInt32BigEndian(b[offset..]);
    public static void PutU32BE(Span<byte> b, int offset, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b[offset..], v);
    public static void PutI32BE(Span<byte> b, int offset, int v) => BinaryPrimitives.WriteInt32BigEndian(b[offset..], v);

    public static ushort U16LE(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b[offset..]);
    public static uint U32LE(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b[offset..]);
    public static ulong U64LE(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b[offset..]);
    public static void PutU16LE(Span<byte> b, int offset, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b[offset..], v);
    public static void PutU32LE(Span<byte> b, int offset, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[offset..], v);

    /// <summary>Reads a four character code such as "RDSK" or "DOS\1".</summary>
    public static uint FourCC(string s)
    {
        if (s.Length != 4) throw new ArgumentException("FourCC must be 4 characters.", nameof(s));
        return (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);
    }

    /// <summary>Reads an Amiga BCPL string (length byte followed by characters).</summary>
    public static string BcplString(ReadOnlySpan<byte> b, int offset, int maxLength)
    {
        int len = Math.Min(b[offset], maxLength);
        return Encoding.Latin1.GetString(b.Slice(offset + 1, len));
    }

    public static void PutBcplString(Span<byte> b, int offset, string value, int maxLength)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        int len = Math.Min(bytes.Length, maxLength);
        b[offset] = (byte)len;
        bytes.AsSpan(0, len).CopyTo(b[(offset + 1)..]);
    }

    public static string Ascii(ReadOnlySpan<byte> b, int offset, int length) =>
        Encoding.ASCII.GetString(b.Slice(offset, length)).TrimEnd('\0', ' ');
}
