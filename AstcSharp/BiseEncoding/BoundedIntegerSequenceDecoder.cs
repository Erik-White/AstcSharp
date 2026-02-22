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
    /// </summary>
    /// <param name="mode">The encoding mode (trit or quint).</param>
    /// <param name="encodedBlock">The bits representing the encoded block.</param>
    /// <param name="encodedBitCount">The number of bits used for each value.</param>
    /// <param name="result">The span to write decoded values into.</param>
    /// <returns>The number of values written to the result span.</returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static int DecodeISEBlock(BiseEncodingMode mode, ulong encodedBlock, int encodedBitCount, Span<int> result)
    {
        int[] interleavedBits = mode switch
        {
            BiseEncodingMode.TritEncoding => InterleavedTritBits,
            BiseEncodingMode.QuintEncoding => InterleavedQuintBits,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"ASTC blocks only support trit and quint encoding")
        };

        var valuesCount = mode == BiseEncodingMode.TritEncoding ? 5 : 3;
        var bitSource = new BitStream(encodedBlock, dataSize: sizeof(ulong) * 8);
        Span<int> m = stackalloc int[5];
        ulong encodedBits = 0;
        int encodedBitsRead = 0;

        for (int i = 0; i < valuesCount; i++)
        {
            if (!bitSource.TryGetBits(encodedBitCount, out ulong bits))
                throw new InvalidOperationException();

            m[i] = (int)bits;

            if (!bitSource.TryGetBits(interleavedBits[i], out ulong encoded_bits))
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

        return valuesCount;
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
