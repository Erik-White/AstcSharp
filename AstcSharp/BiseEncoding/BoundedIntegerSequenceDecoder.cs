using AstcSharp.IO;

namespace AstcSharp.BiseEncoding;

internal class BoundedIntegerSequenceDecoder : BoundedIntegerSequenceCodec
{
    public BoundedIntegerSequenceDecoder(int range) : base(range) { }

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
        int totalBitCount = GetBitCount(_encoding, valuesCount, _bitCount);
        int bitsPerBlock = GetEncodedBlockSize();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bitsPerBlock, 64);

        var result = new int[valuesCount];
        int resultIndex = 0;
        int bitsRemaining = totalBitCount;

        while (bitsRemaining > 0)
        {
            int bitsToRead = Math.Min(bitsRemaining, bitsPerBlock);
            if (!bitSource.TryGetBits<ulong>(bitsToRead, out var blockBits))
                throw new InvalidOperationException("Not enough bits in BitStream to decode BISE block");

            if (_encoding == BiseEncodingMode.BitEncoding)
            {
                if (resultIndex < valuesCount)
                    result[resultIndex++] = (int)blockBits;
            }
            else
            {
                var decodedValues = DecodeISEBlock(_encoding, blockBits, _bitCount);
                for (int i = 0; i < decodedValues.Length && resultIndex < valuesCount; ++i)
                    result[resultIndex++] = decodedValues[i];
            }

            bitsRemaining -= bitsPerBlock;
        }

        if (resultIndex < valuesCount)
            throw new InvalidOperationException("Decoded fewer values than expected from BISE block");

        return result;
    }

    /// <summary>
    /// Decode a trit/quint block
    /// </summary>
    /// <param name="valRange">The range of values, either 3 for trits or 5 for quints.</param>
    /// <param name="encodedBlock">The bits representing the encoded block.</param>
    /// <param name="encodedBitCount">The number of bits used for each value.</param>
    /// <returns>An array of decoded integer values.</returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static int[] DecodeISEBlock(BiseEncodingMode mode, ulong encodedBlock, int encodedBitCount)
    {
        int[] interleavedBits = mode switch
        {
            BiseEncodingMode.TritEncoding => InterleavedTritBits,
            BiseEncodingMode.QuintEncoding => InterleavedQuintBits,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"ASTC blocks only support trit and quint encoding")
        };

        var valuesCount = mode == BiseEncodingMode.TritEncoding ? 5 : 3;
        var bitSource = new BitStream(encodedBlock, dataSize: sizeof(ulong) * 8);
        var result = new int[valuesCount];
        var m = new int[valuesCount];
        ulong encodedBits = 0;
        int encodedBitsRead = 0;

        for (int i = 0; i < valuesCount; i++)
        {
            if (!bitSource.TryGetBits<ulong>(encodedBitCount, out var bits))
                throw new InvalidOperationException();

            m[i] = (int)bits;

            if (!bitSource.TryGetBits<ulong>(interleavedBits[i], out var encoded_bits))
                throw new InvalidOperationException();

            encodedBits |= encoded_bits << encodedBitsRead;
            encodedBitsRead += interleavedBits[i];
        }

        int[] encodings = mode == BiseEncodingMode.TritEncoding
            ? TritEncodings[encodedBits]
            : QuintEncodings[encodedBits];

        for (int i = 0; i < valuesCount; ++i)
        {
            result[i] = (encodings[i] << encodedBitCount) | m[i];
        }

        return result;
    }
}
