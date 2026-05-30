namespace AstcSharp.BiseEncoding;

/// <summary>
/// BISE encoder (ASTC spec §C.2.12) — the inverse of <see cref="BoundedIntegerSequenceDecoder"/>.
/// Packs a sequence of bounded integers into bits/trits/quints using the same block layout the
/// decoder reads, so <c>Decode(Encode(values)) == values</c> for every spec-valid range.
/// </summary>
/// <remarks>
/// Each value splits into a high part (the trit/quint symbol) and a low part (the mantissa bits).
/// The high parts of a block (5 for trits, 3 for quints) select a packed byte via reverse lookup
/// tables built from <see cref="BoundedIntegerSequenceCodec.FlatTritEncodings"/> /
/// <see cref="BoundedIntegerSequenceCodec.FlatQuintEncodings"/>; the mantissas and the packed
/// symbol bits are then interleaved at the exact positions the decoder extracts them from.
/// </remarks>
internal static class BoundedIntegerSequenceEncoder
{
    // One BISE block holds five trits or three quints (ASTC spec §C.2.12).
    private const int TritsPerBlock = 5;
    private const int QuintsPerBlock = 3;

    // A trit is a base-3 digit, a quint base-5; these are the radices used to index the
    // reverse-lookup tables (and equal the alphabet sizes named in BiseEncodingMode).
    private const int TritRadix = 3;
    private const int QuintRadix = 5;

    // Distinct symbol tuples per block: TritRadix^TritsPerBlock = 3^5, QuintRadix^QuintsPerBlock = 5^3.
    private const int TritTupleCount = 243;
    private const int QuintTupleCount = 125;

    // Reverse lookup: trit-tuple (base-3 digits) -> 8-bit packed selector.
    private static readonly byte[] TritTupleToPacked = BuildReverseTable(
        BoundedIntegerSequenceCodec.FlatTritEncodings, TritsPerBlock, TritRadix, TritTupleCount);

    // Reverse lookup: quint-tuple (base-5 digits) -> 7-bit packed selector.
    private static readonly byte[] QuintTupleToPacked = BuildReverseTable(
        BoundedIntegerSequenceCodec.FlatQuintEncodings, QuintsPerBlock, QuintRadix, QuintTupleCount);

    /// <summary>
    /// Encodes <paramref name="values"/> (each in <c>[0, range]</c>) into <paramref name="stream"/>
    /// using the most space-efficient BISE packing for <paramref name="range"/>, mirroring the
    /// layout <see cref="BoundedIntegerSequenceDecoder.Decode"/> reads.
    /// </summary>
    public static void Encode(int range, ReadOnlySpan<int> values, ref BitStream stream)
    {
        (BiseEncodingMode mode, int bitCount) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(range);
        Encode(mode, bitCount, values, ref stream);
    }

    /// <summary>
    /// Encodes <paramref name="values"/> with an explicit BISE mode and mantissa
    /// <paramref name="bitCount"/> (both typically from
    /// <see cref="BoundedIntegerSequenceCodec.GetPackingModeBitCount"/>).
    /// </summary>
    public static void Encode(BiseEncodingMode mode, int bitCount, ReadOnlySpan<int> values, ref BitStream stream)
    {
        switch (mode)
        {
            case BiseEncodingMode.BitEncoding:
                EncodeBits(values, bitCount, ref stream);
                break;
            case BiseEncodingMode.TritEncoding:
                EncodeTrits(values, bitCount, ref stream);
                break;
            case BiseEncodingMode.QuintEncoding:
                EncodeQuints(values, bitCount, ref stream);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid BISE encoding mode");
        }
    }

    private static void EncodeBits(ReadOnlySpan<int> values, int bitCount, ref BitStream stream)
    {
        for (int i = 0; i < values.Length; i++)
        {
            stream.PutBits((ulong)values[i], bitCount);
        }
    }

    /// <summary>
    /// Packs values five at a time as trit blocks. The trailing partial block (when the count is
    /// not a multiple of 5) is emitted truncated to the exact bit length the decoder consumes —
    /// padding-symbol positions are treated as 0, matching the spec's variable-length rule.
    /// </summary>
    private static void EncodeTrits(ReadOnlySpan<int> values, int bitCount, ref BitStream stream)
    {
        ulong mantissaMask = bitCount == 0 ? 0UL : (1UL << bitCount) - 1UL;
        int blockBitLength = BoundedIntegerSequenceCodec.GetEncodedBlockSize(BiseEncodingMode.TritEncoding, bitCount);

        // Reused each iteration; the inner loop fully overwrites both spans, so no stale state
        // carries between blocks. Hoisted out of the loop to avoid per-iteration stack growth.
        Span<int> tritDigits = stackalloc int[TritsPerBlock];
        Span<int> mantissas = stackalloc int[TritsPerBlock];

        for (int blockStart = 0; blockStart < values.Length; blockStart += TritsPerBlock)
        {
            int count = Math.Min(TritsPerBlock, values.Length - blockStart);
            for (int i = 0; i < TritsPerBlock; i++)
            {
                int value = i < count ? values[blockStart + i] : 0;
                tritDigits[i] = value >> bitCount;
                mantissas[i] = (int)((ulong)value & mantissaMask);
            }

            int packed = TritTupleToPacked[TupleIndex(tritDigits, TritRadix)];
            ulong blockBits = EmitTritBlock(packed, mantissas, bitCount);
            int validBits = ValidPartialBitLength(count, BiseEncodingMode.TritEncoding, bitCount, blockBitLength);
            stream.PutBits(blockBits, validBits);
        }
    }

    /// <summary>
    /// Packs values three at a time as quint blocks, with the same trailing-truncation handling
    /// as <see cref="EncodeTrits"/>.
    /// </summary>
    private static void EncodeQuints(ReadOnlySpan<int> values, int bitCount, ref BitStream stream)
    {
        ulong mantissaMask = bitCount == 0 ? 0UL : (1UL << bitCount) - 1UL;
        int blockBitLength = BoundedIntegerSequenceCodec.GetEncodedBlockSize(BiseEncodingMode.QuintEncoding, bitCount);

        // Reused each iteration; the inner loop fully overwrites both spans, so no stale state
        // carries between blocks. Hoisted out of the loop to avoid per-iteration stack growth.
        Span<int> quintDigits = stackalloc int[QuintsPerBlock];
        Span<int> mantissas = stackalloc int[QuintsPerBlock];

        for (int blockStart = 0; blockStart < values.Length; blockStart += QuintsPerBlock)
        {
            int count = Math.Min(QuintsPerBlock, values.Length - blockStart);
            for (int i = 0; i < QuintsPerBlock; i++)
            {
                int value = i < count ? values[blockStart + i] : 0;
                quintDigits[i] = value >> bitCount;
                mantissas[i] = (int)((ulong)value & mantissaMask);
            }

            int packed = QuintTupleToPacked[TupleIndex(quintDigits, QuintRadix)];
            ulong blockBits = EmitQuintBlock(packed, mantissas, bitCount);
            int validBits = ValidPartialBitLength(count, BiseEncodingMode.QuintEncoding, bitCount, blockBitLength);
            stream.PutBits(blockBits, validBits);
        }
    }

    /// <summary>
    /// Builds one trit block's bit pattern with mantissas and the 8-bit packed trit selector
    /// interleaved at the positions <see cref="BoundedIntegerSequenceDecoder"/> reads them:
    /// [m0, t(2), m1, t(2), m2, t(1), m3, t(2), m4, t(1)].
    /// </summary>
    private static ulong EmitTritBlock(int packedTrits, ReadOnlySpan<int> mantissas, int bitCount)
    {
        ulong block = 0;
        int pos = 0;
        block |= (ulong)mantissas[0] << pos; pos += bitCount;
        block |= (ulong)(packedTrits & 0x3) << pos; pos += 2;
        block |= (ulong)mantissas[1] << pos; pos += bitCount;
        block |= (ulong)((packedTrits >> 2) & 0x3) << pos; pos += 2;
        block |= (ulong)mantissas[2] << pos; pos += bitCount;
        block |= (ulong)((packedTrits >> 4) & 0x1) << pos; pos += 1;
        block |= (ulong)mantissas[3] << pos; pos += bitCount;
        block |= (ulong)((packedTrits >> 5) & 0x3) << pos; pos += 2;
        block |= (ulong)mantissas[4] << pos; pos += bitCount;
        block |= (ulong)((packedTrits >> 7) & 0x1) << pos;
        return block;
    }

    /// <summary>
    /// Builds one quint block's bit pattern: [m0, q(3), m1, q(2), m2, q(2)].
    /// </summary>
    private static ulong EmitQuintBlock(int packedQuints, ReadOnlySpan<int> mantissas, int bitCount)
    {
        ulong block = 0;
        int pos = 0;
        block |= (ulong)mantissas[0] << pos; pos += bitCount;
        block |= (ulong)(packedQuints & 0x7) << pos; pos += 3;
        block |= (ulong)mantissas[1] << pos; pos += bitCount;
        block |= (ulong)((packedQuints >> 3) & 0x3) << pos; pos += 2;
        block |= (ulong)mantissas[2] << pos; pos += bitCount;
        block |= (ulong)((packedQuints >> 5) & 0x3) << pos;
        return block;
    }

    /// <summary>
    /// The number of low bits of a full block that hold the first <paramref name="count"/> values
    /// (ASTC spec §C.2.22 — a partial trailing block is stored truncated). For a full block this
    /// is <paramref name="fullBlockBits"/>; for a partial one it is the bit count the decoder
    /// computes for that many values.
    /// </summary>
    private static int ValidPartialBitLength(int count, BiseEncodingMode mode, int bitCount, int fullBlockBits)
    {
        int valuesPerBlock = mode == BiseEncodingMode.TritEncoding ? TritsPerBlock : QuintsPerBlock;
        return count >= valuesPerBlock
            ? fullBlockBits
            : BoundedIntegerSequenceCodec.GetBitCount(mode, count, bitCount);
    }

    /// <summary>
    /// Mixed-radix index of a digit tuple (digit 0 least significant), used to address the
    /// reverse-lookup tables: <c>digits[0] + digits[1]*radix + digits[2]*radix^2 + …</c>.
    /// </summary>
    private static int TupleIndex(ReadOnlySpan<int> digits, int radix)
    {
        int index = 0;
        int place = 1;
        for (int i = 0; i < digits.Length; i++)
        {
            index += digits[i] * place;
            place *= radix;
        }

        return index;
    }

    /// <summary>
    /// Builds a reverse table mapping each symbol tuple (mixed-radix index) to the packed selector
    /// that decodes to it. <paramref name="flatEncodings"/> is the decoder's forward table
    /// (<paramref name="symbolsPerBlock"/> symbols per row); rows beyond <paramref name="tupleCount"/>
    /// distinct tuples are duplicates, so the lowest packed index is kept for each tuple to
    /// guarantee a clean round-trip through the decoder.
    /// </summary>
    private static byte[] BuildReverseTable(int[] flatEncodings, int symbolsPerBlock, int radix, int tupleCount)
    {
        byte[] table = new byte[tupleCount];
        bool[] seen = new bool[tupleCount];
        int rowCount = flatEncodings.Length / symbolsPerBlock;

        Span<int> digits = stackalloc int[symbolsPerBlock];
        for (int packed = 0; packed < rowCount; packed++)
        {
            int baseIndex = packed * symbolsPerBlock;
            for (int i = 0; i < symbolsPerBlock; i++)
            {
                digits[i] = flatEncodings[baseIndex + i];
            }

            int index = TupleIndex(digits, radix);
            if (!seen[index])
            {
                seen[index] = true;
                table[index] = (byte)packed;
            }
        }

        return table;
    }
}
