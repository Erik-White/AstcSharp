namespace AstcSharp.Core;

internal static class BitOperations
{
    /// <summary>
    /// Return the specified range as a <see cref="UInt128Ex"/> (low bits in <see cref="UInt128Ex.Low"/> field)
    /// </summary>
    public static UInt128Ex GetBits(UInt128Ex value, int start, int length)
    {
        // const int UInt128BitCount = 128;
        
        // if (length <= 0)
        //     return UInt128Ex.Zero;

        // var mask = length == UInt128BitCount
        //     ? UInt128Ex.Zero
        //     : UInt128Ex.Zero >> (UInt128BitCount - length);

        //return (value >> start) & mask;

        if (length <= 0)
            return UInt128Ex.Zero;
            
        var shifted = value >> start;
        if (length >= 128)
            return shifted;

        if (length >= 64)
        {
            ulong lowMask = ~0UL;
            int highBits = length - 64;
            ulong highMask = (highBits == 64) ? ~0UL : ((1UL << highBits) - 1UL);

            return new UInt128Ex(shifted.Low & lowMask, shifted.High & highMask);
        }
        else
        {
            ulong mask = (length == 64) ? ~0UL : ((1UL << length) - 1UL);

            return new UInt128Ex(shifted.Low & mask, 0UL);
        }
    }

    /// <summary>
    /// Return the specified range as a ulong
    /// </summary>
    public static ulong GetBits(ulong value, int start, int length)
    {
        if (length <= 0)
            return 0UL;

        int total_bits = sizeof(ulong) * 8;
        ulong mask = length == total_bits
            ? ~0UL
            : ~0UL >> (total_bits - length);

        return (value >> start) & mask;
    }
    
    /// <summary>
    /// Transfers a few bits of precision from one value to another.
    /// </summary>
    /// <remarks>
    /// The 'bit_transfer_signed' function defined in Section C.2.14 of the ASTC specification
    /// </remarks>
    public static void TransferPrecision(ref int a, ref int b)
    {
        b >>= 1;
        b |= a & 0x80;
        a >>= 1;
        a &= 0x3F;

        if ((a & 0x20) != 0)
            a -= 0x40;
    }

    /// <summary>
    /// Takes two values, |a| in the range [-32, 31], and |b| in the range [0, 255],
    /// and returns the two values in [0, 255] that will reconstruct |a| and |b| when
    /// passed to the <see cref="TransferPrecision"/> function.
    /// </summary>
    public static void TransferPrecisionInverse(ref int a, ref int b)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(a, -32);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(a, 31);
        ArgumentOutOfRangeException.ThrowIfLessThan(b, byte.MinValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(b, byte.MaxValue);

        if (a < 0)
            a += 0x40;

        a <<= 1;
        a |= b & 0x80;
        b <<= 1;
        b &= 0xff;
    }
}