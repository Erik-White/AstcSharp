using System.Buffers.Binary;
using AstcSharp.BiseEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Assembles a 128-bit ASTC block (spec §C.2.7–§C.2.12) field by field, inverting the bit layout
/// the decoders read. Low-positioned fields (block mode, partition info, colour data) are written
/// at their literal bit offsets; weight data is written reversed into the top of the block, since
/// the decoder recovers it via <see cref="UInt128Extensions.ReverseBits(UInt128)"/> and reads it LSB-first.
/// </summary>
internal struct AstcBlockBuilder
{
    private UInt128 bits;

    /// <summary>
    /// Writes <paramref name="count"/> low bits of <paramref name="value"/> at
    /// <paramref name="startBit"/>. Used for the block mode, partition count/seed, colour endpoint
    /// mode, and colour-data fields, which the decoder reads from fixed low-bit positions.
    /// </summary>
    public void PlaceLowField(ulong value, int startBit, int count)
    {
        UInt128 masked = (UInt128)value & UInt128Extensions.OnesMask(count);
        this.bits |= masked << startBit;
    }

    /// <summary>
    /// Writes a colour-data BISE stream beginning at <paramref name="startBit"/>. The stream's
    /// bits are emitted in the same LSB-first order the decoder extracts them.
    /// </summary>
    public void PlaceColorData(in BitStream colorStream, int startBit)
    {
        BitStream stream = colorStream;
        int count = (int)stream.Bits;
        if (count == 0)
        {
            return;
        }

        if (!stream.TryGetBits(count, out UInt128 value))
        {
            throw new InvalidOperationException("Colour BISE stream shorter than its reported bit count.");
        }

        this.bits |= (value & UInt128Extensions.OnesMask(count)) << startBit;
    }

    /// <summary>
    /// Writes a weight BISE stream into the top of the block. The decoder reverses the whole
    /// 128-bit block and reads weights LSB-first (spec §C.2.12), so reversing the stream value
    /// here places weight bit 0 at bit 127, bit 1 at 126, … — exactly what the decoder recovers.
    /// </summary>
    public void PlaceWeightData(in BitStream weightStream)
    {
        BitStream stream = weightStream;
        int count = (int)stream.Bits;
        if (count == 0)
        {
            return;
        }

        if (!stream.TryGetBits(count, out UInt128 value))
        {
            throw new InvalidOperationException("Weight BISE stream shorter than its reported bit count.");
        }

        // value holds the weight bits LSB-first in its low `count` bits; reversing the full
        // 128-bit word moves them to the top so the decoder's block reversal restores them.
        this.bits |= (value & UInt128Extensions.OnesMask(count)).ReverseBits();
    }

    /// <summary>The assembled 128-bit block.</summary>
    public readonly UInt128 Build() => this.bits;

    /// <summary>Writes the assembled block as 16 little-endian bytes into <paramref name="destination"/>.</summary>
    public readonly void WriteTo(Span<byte> destination)
    {
        if (destination.Length < BlockInfo.SizeInBytes)
        {
            throw new ArgumentException($"ASTC block buffer must be at least {BlockInfo.SizeInBytes} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt128LittleEndian(destination, this.bits);
    }
}
