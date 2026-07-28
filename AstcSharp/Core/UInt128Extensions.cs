namespace AstcSharp.Core;

internal static class UInt128Extensions
{
    /// <summary>
    /// Bits per <see cref="ulong"/> half of a <see cref="UInt128"/>. The low/high split, and any
    /// bit arithmetic that crosses it, is expressed in terms of this width.
    /// </summary>
    public const int HalfBits = 64;

    /// <summary>
    /// The lower 64 bits of the <see cref="UInt128"/> value
    /// </summary>
    public static ulong Low(this UInt128 value)
        => (ulong)value;

    /// <summary>
    /// The upper 64 bits of the <see cref="UInt128"/> value
    /// </summary>
    public static ulong High(this UInt128 value)
        => (ulong)(value >> HalfBits);

    /// <summary>
    /// A mask with the lowest n bits set to 1
    /// </summary>
    public static UInt128 OnesMask(int n) => n switch
    {
        <= 0 => UInt128.Zero,
        >= 128 => UInt128.MaxValue,
        _ => UInt128.MaxValue >> (128 - n)
    };

    /// <summary>
    /// Reverses bits across the full 128-bit value. Used by the BISE weight decoder
    /// (ASTC spec §C.2.12) — weight data is encoded most-significant-bit-first into the
    /// high end of the block, so callers reverse the block before reading weights as
    /// a normal little-endian sequence.
    /// </summary>
    public static UInt128 ReverseBits(this UInt128 value)
    {
        ulong revLow = ReverseBits(value.Low());
        ulong revHigh = ReverseBits(value.High());

        return new UInt128(revLow, revHigh);
    }

    private static ulong ReverseBits(ulong x)
    {
        x = ((x >> 1) & 0x5555555555555555UL) | ((x & 0x5555555555555555UL) << 1);
        x = ((x >> 2) & 0x3333333333333333UL) | ((x & 0x3333333333333333UL) << 2);
        x = ((x >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((x & 0x0F0F0F0F0F0F0F0FUL) << 4);
        x = ((x >> 8) & 0x00FF00FF00FF00FFUL) | ((x & 0x00FF00FF00FF00FFUL) << 8);
        x = ((x >> 16) & 0x0000FFFF0000FFFFUL) | ((x & 0x0000FFFF0000FFFFUL) << 16);
        x = (x >> 32) | (x << 32);

        return x;
    }
}
