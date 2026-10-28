using AmigaSdCreator.Core.IO;

namespace AmigaSdCreator.Core.Partitioning;

/// <summary>Checksums used by RDB blocks and the Amiga filesystems.</summary>
public static class AmigaChecksum
{
    /// <summary>
    /// RDB style: the 32-bit sum of the first <paramref name="summedLongs"/> big-endian longs is zero.
    /// Computes and stores the value at <paramref name="checksumOffset"/>.
    /// </summary>
    public static void SetSumToZero(Span<byte> block, int summedLongs, int checksumOffset)
    {
        Endian.PutU32BE(block, checksumOffset, 0);
        uint sum = Sum(block, summedLongs);
        Endian.PutU32BE(block, checksumOffset, unchecked(0u - sum));
    }

    public static bool IsSumZero(ReadOnlySpan<byte> block, int summedLongs) => Sum(block, summedLongs) == 0;

    public static uint Sum(ReadOnlySpan<byte> block, int longs)
    {
        uint sum = 0;
        for (int i = 0; i < longs; i++) sum = unchecked(sum + Endian.U32BE(block, i * 4));
        return sum;
    }
}
