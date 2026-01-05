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
    /// <exception cref="InvalidOperationException"></exception>
    public List<int> Decode(int valuesCount, ref BitStream bitSource)
    {
        int totalBitsCount = GetBitCount(_encoding, valuesCount, _bitCount);
        int bitsPerBlock = GetEncodedBlockSize();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bitsPerBlock, 64);

        int bitsRemaining = totalBitsCount;
        var result = new List<int>();

        while (bitsRemaining > 0)
        {
            int toRead = Math.Min(bitsRemaining, bitsPerBlock);
            bool ok = bitSource.GetBits<ulong>(toRead, out var blockBits);
            if (!ok) throw new InvalidOperationException();
            switch (_encoding)
            {
                case BiseEncodingMode.TritEncoding:
                    result.AddRange(DecodeISEBlock(3, blockBits, _bitCount));
                    break;
                case BiseEncodingMode.QuintEncoding:
                    result.AddRange(DecodeISEBlock(5, blockBits, _bitCount));
                    break;
                case BiseEncodingMode.BitEncoding:
                    result.Add((int)blockBits);
                    break;
            }
            bitsRemaining -= bitsPerBlock;
        }

        if (result.Count < valuesCount) throw new InvalidOperationException();
        result.RemoveRange(valuesCount, result.Count - valuesCount);
        
        return result;
    }

    /// <summary>
    /// Decode a trit/quint block
    /// </summary>
    /// <param name="valRange">The range of values, either 3 for trits or 5 for quints.</param>
    /// <param name="blockBits">The bits representing the encoded block.</param>
    /// <param name="numBits">The number of bits used for each value.</param>
    /// <returns>An array of decoded integer values.</returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static int[] DecodeISEBlock(int valRange, ulong blockBits, int numBits)
    {
        if (!(valRange == 3 || valRange == 5))
            throw new ArgumentException("valRange must be 3 or 5", nameof(valRange));

        int kNumVals = (valRange == 5) ? 3 : 5;
        int[] kInterleavedBits = (valRange == 5) ? InterleavedQuintBits : InterleavedTritBits;

        var blockBitSrc = new BitStream(blockBits, 64);

        var m = new int[kNumVals];
        ulong encoded = 0;
        int encoded_bits_read = 0;
        for (int i = 0; i < kNumVals; ++i)
        {
            bool res = blockBitSrc.GetBits<ulong>(numBits, out var bits);
            if (!res) throw new InvalidOperationException();
            m[i] = (int)bits;

            res = blockBitSrc.GetBits<ulong>(kInterleavedBits[i], out var encoded_bits);
            if (!res) throw new InvalidOperationException();
            encoded |= encoded_bits << encoded_bits_read;
            encoded_bits_read += kInterleavedBits[i];
        }

        int[] encodings = (valRange == 5) ? QuintEncodings[encoded] : TritEncodings[encoded];
        var result = new int[kNumVals];
        for (int i = 0; i < kNumVals; ++i)
        {
            result[i] = (encodings[i] << numBits) | m[i];
        }
        return result;
    }
}
