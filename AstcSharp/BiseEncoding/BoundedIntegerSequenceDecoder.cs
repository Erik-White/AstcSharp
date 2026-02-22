using AstcSharp.IO;

namespace AstcSharp.BiseEncoding;

internal class BoundedIntegerSequenceDecoder : BoundedIntegerSequenceCodec
{
    private static readonly BoundedIntegerSequenceDecoder?[] _cache = new BoundedIntegerSequenceDecoder?[256];

    public static BoundedIntegerSequenceDecoder GetCached(int range)
    {
        var d = _cache[range];
        if (d is null)
        {
            d = new BoundedIntegerSequenceDecoder(range);
            _cache[range] = d;
        }
        return d;
    }

    public BoundedIntegerSequenceDecoder(int range) : base(range) { }

    /// <summary>
    /// Decode a sequence of bounded integers into a caller-provided span.
    /// </summary>
    /// <param name="valuesCount">The number of values to decode.</param>
    /// <param name="bitSource">The source of values to decode from.</param>
    /// <param name="result">The span to write decoded values into.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public void Decode(int valuesCount, ref BitStream bitSource, Span<int> result)
    {
        int totalBitCount = GetBitCount(_encoding, valuesCount, _bitCount);
        int bitsPerBlock = GetEncodedBlockSize();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bitsPerBlock, 64);

        Span<int> blockResult = stackalloc int[5];
        int resultIndex = 0;
        int bitsRemaining = totalBitCount;

        while (bitsRemaining > 0)
        {
            int bitsToRead = Math.Min(bitsRemaining, bitsPerBlock);
            if (!bitSource.TryGetBits(bitsToRead, out ulong blockBits))
                throw new InvalidOperationException("Not enough bits in BitStream to decode BISE block");

            if (_encoding == BiseEncodingMode.BitEncoding)
            {
                if (resultIndex < valuesCount)
                    result[resultIndex++] = (int)blockBits;
            }
            else
            {
                int decoded = DecodeISEBlock(_encoding, blockBits, _bitCount, blockResult);
                for (int i = 0; i < decoded && resultIndex < valuesCount; ++i)
                    result[resultIndex++] = blockResult[i];
            }

            bitsRemaining -= bitsPerBlock;
        }

        if (resultIndex < valuesCount)
            throw new InvalidOperationException("Decoded fewer values than expected from BISE block");
    }

    /// <summary>
    /// Decode a sequence of bounded integers. The number of bits read is dependent on the number
    /// of bits required to encode <paramref name="valuesCount"/> based on the calculation provided
    /// in Section C.2.22 of the ASTC specification.
    /// </summary>
    /// <param name="valuesCount">The number of values to decode.</param>
    /// <param name="bitSource">The source of values to decode from.</param>
    /// <returns>The decoded values. The collection always contains exactly <paramref name="valuesCount"/> elements.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public int[] Decode(int valuesCount, ref BitStream bitSource)
    {
        var result = new int[valuesCount];
        Decode(valuesCount, ref bitSource, result);
        return result;
    }

    /// <summary>
    /// Decode a trit/quint block into a caller-provided span.
    /// Returns the number of values written.
    /// Uses direct bit extraction (no BitStream) and flat encoding tables.
    /// </summary>
    public static int DecodeISEBlock(BiseEncodingMode mode, ulong encodedBlock, int encodedBitCount, Span<int> result)
    {
        ulong mMask = (1UL << encodedBitCount) - 1;

        if (mode == BiseEncodingMode.TritEncoding)
        {
            // 5 values, interleaved bits = [2, 2, 1, 2, 1] = 8 bits total
            int bitPos = 0;
            int m0 = (int)((encodedBlock >> bitPos) & mMask); bitPos += encodedBitCount;
            ulong enc = (encodedBlock >> bitPos) & 0x3; bitPos += 2;
            int m1 = (int)((encodedBlock >> bitPos) & mMask); bitPos += encodedBitCount;
            enc |= ((encodedBlock >> bitPos) & 0x3) << 2; bitPos += 2;
            int m2 = (int)((encodedBlock >> bitPos) & mMask); bitPos += encodedBitCount;
            enc |= ((encodedBlock >> bitPos) & 0x1) << 4; bitPos += 1;
            int m3 = (int)((encodedBlock >> bitPos) & mMask); bitPos += encodedBitCount;
            enc |= ((encodedBlock >> bitPos) & 0x3) << 5; bitPos += 2;
            int m4 = (int)((encodedBlock >> bitPos) & mMask);
            enc |= ((encodedBlock >> (bitPos + encodedBitCount)) & 0x1) << 7;

            int base5 = (int)enc * 5;
            result[0] = (FlatTritEncodings[base5] << encodedBitCount) | m0;
            result[1] = (FlatTritEncodings[base5 + 1] << encodedBitCount) | m1;
            result[2] = (FlatTritEncodings[base5 + 2] << encodedBitCount) | m2;
            result[3] = (FlatTritEncodings[base5 + 3] << encodedBitCount) | m3;
            result[4] = (FlatTritEncodings[base5 + 4] << encodedBitCount) | m4;
            return 5;
        }
        else
        {
            // 3 values, interleaved bits = [3, 2, 2] = 7 bits total
            int bitPos = 0;
            int m0 = (int)((encodedBlock >> bitPos) & mMask); bitPos += encodedBitCount;
            ulong enc = (encodedBlock >> bitPos) & 0x7; bitPos += 3;
            int m1 = (int)((encodedBlock >> bitPos) & mMask); bitPos += encodedBitCount;
            enc |= ((encodedBlock >> bitPos) & 0x3) << 3; bitPos += 2;
            int m2 = (int)((encodedBlock >> bitPos) & mMask);
            enc |= ((encodedBlock >> (bitPos + encodedBitCount)) & 0x3) << 5;

            int base3 = (int)enc * 3;
            result[0] = (FlatQuintEncodings[base3] << encodedBitCount) | m0;
            result[1] = (FlatQuintEncodings[base3 + 1] << encodedBitCount) | m1;
            result[2] = (FlatQuintEncodings[base3 + 2] << encodedBitCount) | m2;
            return 3;
        }
    }

    /// <summary>
    /// Decode a trit/quint block, returning the result as an array.
    /// </summary>
    /// <param name="mode">The encoding mode (trit or quint).</param>
    /// <param name="encodedBlock">The bits representing the encoded block.</param>
    /// <param name="encodedBitCount">The number of bits used for each value.</param>
    /// <returns>An array of decoded integer values.</returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static int[] DecodeISEBlock(BiseEncodingMode mode, ulong encodedBlock, int encodedBitCount)
    {
        int valuesCount = mode == BiseEncodingMode.TritEncoding ? 5 : 3;
        var result = new int[valuesCount];
        DecodeISEBlock(mode, encodedBlock, encodedBitCount, result);
        return result;
    }
}
