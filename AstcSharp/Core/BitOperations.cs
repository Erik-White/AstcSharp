namespace AstcSharp.Core;

internal static class BitOperations
{
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