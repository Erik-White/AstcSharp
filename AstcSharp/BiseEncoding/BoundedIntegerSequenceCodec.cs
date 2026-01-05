using AstcSharp.IO;

namespace AstcSharp.BiseEncoding;

/// <summary>
/// The Bounded Integer Sequence Encoding (BISE) allows storage of character sequences using
/// arbitrary alphabets of up to 256 symbols. Each alphabet size is encoded in the most
/// space-efficient choice of bits, trits, and quints.
/// </summary>
internal partial class BoundedIntegerSequenceCodec
{
    private const int Log2MaxRangeForBits = 8;
    private const int TotalRangeVariations = 3 * Log2MaxRangeForBits - 3;

    private static readonly int[] InterleavedQuintBits = [3, 2, 2];
    private static readonly int[] InterleavedTritBits = [2, 2, 1, 2, 1];
    private static readonly int[] MaxRanges = InitMaxRanges();

    protected BiseEncodingMode _encoding;
    protected int _bitCount;

    protected BoundedIntegerSequenceCodec(int range)
    {
        var (encodingMode, bitCount) = GetPackingModeBitCount(range);
        _encoding = encodingMode;
        _bitCount = bitCount;
    }

// A cached table containing the max ranges for values encoded using ASTC's
// Bounded Integer Sequence Encoding. These are the numbers between 1 and 255
// that can be represented exactly as a number in the ranges
// [0, 2^k), [0, 3 * 2^k), and [0, 5 * 2^k).
    private static int[] InitMaxRanges()
    {
        var ranges = new List<int>(TotalRangeVariations);
        void add_val(int val)
        {
            if (val <= 0 || (1 << Log2MaxRangeForBits) <= val) return;
            ranges.Add(val);
        }
        for (int i = 0; i <= Log2MaxRangeForBits; ++i)
        {
            add_val(3 * (1 << i) - 1);
            add_val(5 * (1 << i) - 1);
            add_val((1 << i) - 1);
        }
        ranges.Sort();
        return ranges.ToArray();
    }

    public static (BiseEncodingMode Mode, int BitCount) GetPackingModeBitCount(int range)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(range, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(range, 1 << Log2MaxRangeForBits);

        int index = Array.FindIndex(MaxRanges, v => v >= range);
        int maxValue = index < 0
            ? MaxRanges.Last() + 1
            : MaxRanges[index] + 1;

        var encodingMode = Enum.GetValues<BiseEncodingMode>()
            .OrderDescending()
            .FirstOrDefault(em => (maxValue % (int)em == 0) && int.IsPow2(maxValue / (int)em));
        
        return encodingMode != BiseEncodingMode.Unknown
            ? (encodingMode, int.Log2(maxValue / (int)encodingMode))
            : throw new ArgumentOutOfRangeException($"Invalid range for BISE encoding: {range}");
    }

    /// <summary>
    /// Returns the overall bit count for a range of values encoded
    /// </summary>
    public static int GetBitCount(BiseEncodingMode encodingMode, int valuesCount, int bitCount)
    {
        var encodingBitCount = encodingMode switch
        {
            BiseEncodingMode.TritEncoding => ((valuesCount * 8) + 4) / 5,
            BiseEncodingMode.QuintEncoding => ((valuesCount * 7) + 2) / 3,
            BiseEncodingMode.BitEncoding => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(encodingMode), "Invalid encoding mode"),
        };
        var baseBitCount = valuesCount * bitCount;

        return encodingBitCount + baseBitCount;
    }

    // Convenience wrapper used by reference tests: determine the counts for the
    // given range and return the total number of bits needed for num_vals.
    public static int GetBitCountForRange(int valuesCount, int range)
    {
        var (mode, bitCount) = GetPackingModeBitCount(range);

        return GetBitCount(mode, valuesCount, bitCount);
    }

    protected int GetEncodedBlockSize()
    {
        var (blockSize, extraBlockSize) = _encoding switch
        {
            BiseEncodingMode.TritEncoding => (5, 8),
            BiseEncodingMode.QuintEncoding => (3, 7),
            BiseEncodingMode.BitEncoding => (1, 0),
            _ => (0, 0),
        };
        
        return extraBlockSize + blockSize * _bitCount;
    }

    public static IReadOnlyList<int> ISERange() => MaxRanges;

    /// <summary>
    /// Trit encodings for BISE blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tables are used to decode the blocks of values encoded using the ASTC
    /// integer sequence encoding. The theory is that five trits (values that can
    /// take any number in the range [0, 2]) can take on a total of 3^5 = 243 total
    /// values, which can be stored in eight bits. These eight bits are used to
    /// decode the five trits based on the ASTC specification in Section C.2.12.
    /// </para>
    /// <para>
    /// For simplicity, we have stored a look-up table here so that we don't need
    /// to implement the decoding logic. Similarly, seven bits are used to decode
    /// three quints (since 5^3 = 125 < 128).
    /// </para>
    /// </remarks>
    private static readonly int[][] TritEncodings =
    [
        [0,0,0,0,0], [1,0,0,0,0], [2,0,0,0,0], [0,0,2,0,0], [0,1,0,0,0],
        [1,1,0,0,0], [2,1,0,0,0], [1,0,2,0,0], [0,2,0,0,0], [1,2,0,0,0],
        [2,2,0,0,0], [2,0,2,0,0], [0,2,2,0,0], [1,2,2,0,0], [2,2,2,0,0],
        [2,0,2,0,0], [0,0,1,0,0], [1,0,1,0,0], [2,0,1,0,0], [0,1,2,0,0],
        [0,1,1,0,0], [1,1,1,0,0], [2,1,1,0,0], [1,1,2,0,0], [0,2,1,0,0],
        [1,2,1,0,0], [2,2,1,0,0], [2,1,2,0,0], [0,0,0,2,2], [1,0,0,2,2],
        [2,0,0,2,2], [0,0,2,2,2], [0,0,0,1,0], [1,0,0,1,0], [2,0,0,1,0],
        [0,0,2,1,0], [0,1,0,1,0], [1,1,0,1,0], [2,1,0,1,0], [1,0,2,1,0],
        [0,2,0,1,0], [1,2,0,1,0], [2,2,0,1,0], [2,0,2,1,0], [0,2,2,1,0],
        [1,2,2,1,0], [2,2,2,1,0], [2,0,2,1,0], [0,0,1,1,0], [1,0,1,1,0],
        [2,0,1,1,0], [0,1,2,1,0], [0,1,1,1,0], [1,1,1,1,0], [2,1,1,1,0],
        [1,1,2,1,0], [0,2,1,1,0], [1,2,1,1,0], [2,2,1,1,0], [2,1,2,1,0],
        [0,1,0,2,2], [1,1,0,2,2], [2,1,0,2,2], [1,0,2,2,2], [0,0,0,2,0],
        [1,0,0,2,0], [2,0,0,2,0], [0,0,2,2,0], [0,1,0,2,0], [1,1,0,2,0],
        [2,1,0,2,0], [1,0,2,2,0], [0,2,0,2,0], [1,2,0,2,0], [2,2,0,2,0],
        [2,0,2,2,0], [0,2,2,2,0], [1,2,2,2,0], [2,2,2,2,0], [2,0,2,2,0],
        [0,0,1,2,0], [1,0,1,2,0], [2,0,1,2,0], [0,1,2,2,0], [0,1,1,2,0],
        [1,1,1,2,0], [2,1,1,2,0], [1,1,2,2,0], [0,2,1,2,0], [1,2,1,2,0],
        [2,2,1,2,0], [2,1,2,2,0], [0,2,0,2,2], [1,2,0,2,2], [2,2,0,2,2],
        [2,0,2,2,2], [0,0,0,0,2], [1,0,0,0,2], [2,0,0,0,2], [0,0,2,0,2],
        [0,1,0,0,2], [1,1,0,0,2], [2,1,0,0,2], [1,0,2,0,2], [0,2,0,0,2],
        [1,2,0,0,2], [2,2,0,0,2], [2,0,2,0,2], [0,2,2,0,2], [1,2,2,0,2],
        [2,2,2,0,2], [2,0,2,0,2], [0,0,1,0,2], [1,0,1,0,2], [2,0,1,0,2],
        [0,1,2,0,2], [0,1,1,0,2], [1,1,1,0,2], [2,1,1,0,2], [1,1,2,0,2],
        [0,2,1,0,2], [1,2,1,0,2], [2,2,1,0,2], [2,1,2,0,2], [0,2,2,2,2],
        [1,2,2,2,2], [2,2,2,2,2], [2,0,2,2,2], [0,0,0,0,1], [1,0,0,0,1],
        [2,0,0,0,1], [0,0,2,0,1], [0,1,0,0,1], [1,1,0,0,1], [2,1,0,0,1],
        [1,0,2,0,1], [0,2,0,0,1], [1,2,0,0,1], [2,2,0,0,1], [2,0,2,0,1],
        [0,2,2,0,1], [1,2,2,0,1], [2,2,2,0,1], [2,0,2,0,1], [0,0,1,0,1],
        [1,0,1,0,1], [2,0,1,0,1], [0,1,2,0,1], [0,1,1,0,1], [1,1,1,0,1],
        [2,1,1,0,1], [1,1,2,0,1], [0,2,1,0,1], [1,2,1,0,1], [2,2,1,0,1],
        [2,1,2,0,1], [0,0,1,2,2], [1,0,1,2,2], [2,0,1,2,2], [0,1,2,2,2],
        [0,0,0,1,1], [1,0,0,1,1], [2,0,0,1,1], [0,0,2,1,1], [0,1,0,1,1],
        [1,1,0,1,1], [2,1,0,1,1], [1,0,2,1,1], [0,2,0,1,1], [1,2,0,1,1],
        [2,2,0,1,1], [2,0,2,1,1], [0,2,2,1,1], [1,2,2,1,1], [2,2,2,1,1],
        [2,0,2,1,1], [0,0,1,1,1], [1,0,1,1,1], [2,0,1,1,1], [0,1,2,1,1],
        [0,1,1,1,1], [1,1,1,1,1], [2,1,1,1,1], [1,1,2,1,1], [0,2,1,1,1],
        [1,2,1,1,1], [2,2,1,1,1], [2,1,2,1,1], [0,1,1,2,2], [1,1,1,2,2],
        [2,1,1,2,2], [1,1,2,2,2], [0,0,0,2,1], [1,0,0,2,1], [2,0,0,2,1],
        [0,0,2,2,1], [0,1,0,2,1], [1,1,0,2,1], [2,1,0,2,1], [1,0,2,2,1],
        [0,2,0,2,1], [1,2,0,2,1], [2,2,0,2,1], [2,0,2,2,1], [0,2,2,2,1],
        [1,2,2,2,1], [2,2,2,2,1], [2,0,2,2,1], [0,0,1,2,1], [1,0,1,2,1],
        [2,0,1,2,1], [0,1,2,2,1], [0,1,1,2,1], [1,1,1,2,1], [2,1,1,2,1],
        [1,1,2,2,1], [0,2,1,2,1], [1,2,1,2,1], [2,2,1,2,1], [2,1,2,2,1],
        [0,2,1,2,2], [1,2,1,2,2], [2,2,1,2,2], [2,1,2,2,2], [0,0,0,1,2],
        [1,0,0,1,2], [2,0,0,1,2], [0,0,2,1,2], [0,1,0,1,2], [1,1,0,1,2],
        [2,1,0,1,2], [1,0,2,1,2], [0,2,0,1,2], [1,2,0,1,2], [2,2,0,1,2],
        [2,0,2,1,2], [0,2,2,1,2], [1,2,2,1,2], [2,2,2,1,2], [2,0,2,1,2],
        [0,0,1,1,2], [1,0,1,1,2], [2,0,1,1,2], [0,1,2,1,2], [0,1,1,1,2],
        [1,1,1,1,2], [2,1,1,1,2], [1,1,2,1,2], [0,2,1,1,2], [1,2,1,1,2],
        [2,2,1,1,2], [2,1,2,1,2], [0,2,2,2,2], [1,2,2,2,2], [2,2,2,2,2],
        [2,1,2,2,2]
    ];

    /// <summary>
    /// Quint encodings for BISE blocks.
    /// </summary>
    /// <remarks>
    /// See <see cref="TritEncodings"/> for more details.
    /// </remarks>
    private static readonly int[][] QuintEncodings =
    [
        [0,0,0], [1,0,0], [2,0,0], [3,0,0], [4,0,0], [0,4,0], [4,4,0],
        [4,4,4], [0,1,0], [1,1,0], [2,1,0], [3,1,0], [4,1,0], [1,4,0],
        [4,4,1], [4,4,4], [0,2,0], [1,2,0], [2,2,0], [3,2,0], [4,2,0],
        [2,4,0], [4,4,2], [4,4,4], [0,3,0], [1,3,0], [2,3,0], [3,3,0],
        [4,3,0], [3,4,0], [4,4,3], [4,4,4], [0,0,1], [1,0,1], [2,0,1],
        [3,0,1], [4,0,1], [0,4,1], [4,0,4], [0,4,4], [0,1,1], [1,1,1],
        [2,1,1], [3,1,1], [4,1,1], [1,4,1], [4,1,4], [1,4,4], [0,2,1],
        [1,2,1], [2,2,1], [3,2,1], [4,2,1], [2,4,1], [4,2,4], [2,4,4],
        [0,3,1], [1,3,1], [2,3,1], [3,3,1], [4,3,1], [3,4,1], [4,3,4],
        [3,4,4], [0,0,2], [1,0,2], [2,0,2], [3,0,2], [4,0,2], [0,4,2],
        [2,0,4], [3,0,4], [0,1,2], [1,1,2], [2,1,2], [3,1,2], [4,1,2],
        [1,4,2], [2,1,4], [3,1,4], [0,2,2], [1,2,2], [2,2,2], [3,2,2],
        [4,2,2], [2,4,2], [2,2,4], [3,2,4], [0,3,2], [1,3,2], [2,3,2],
        [3,3,2], [4,3,2], [3,4,2], [2,3,4], [3,3,4], [0,0,3], [1,0,3],
        [2,0,3], [3,0,3], [4,0,3], [0,4,3], [0,0,4], [1,0,4], [0,1,3],
        [1,1,3], [2,1,3], [3,1,3], [4,1,3], [1,4,3], [0,1,4], [1,1,4],
        [0,2,3], [1,2,3], [2,2,3], [3,2,3], [4,2,3], [2,4,3], [0,2,4],
        [1,2,4], [0,3,3], [1,3,3], [2,3,3], [3,3,3], [4,3,3], [3,4,3],
        [0,3,4], [1,3,4]
    ];

    // Decode a trit/quint block
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

    public static void EncodeISEBlock<T>(List<int> vals, int bits_per_val, ref BitStream bit_sink, ref int bits_written, int total_num_bits) where T : unmanaged
    {
        int kNumVals = vals.Count;
        int valRange = (kNumVals == 3) ? 5 : 3;
        int num_bits_per_block = (valRange == 5) ? 7 : 8;
        int[] kInterleavedBits = (valRange == 5) ? InterleavedQuintBits : InterleavedTritBits;

        var non_bits = new int[kNumVals];
        var bits = new int[kNumVals];
        for (int i = 0; i < kNumVals; ++i)
        {
            bits[i] = vals[i] & ((1 << bits_per_val) - 1);
            non_bits[i] = vals[i] >> bits_per_val;
        }

        // Determine how many interleaved bits for this block given the global
        // total_num_bits and how many bits have already been written.
        int temp_bits_added = bits_written;
        int num_encoded_bits = 0;
        for (int i = 0; i < kNumVals; ++i)
        {
            temp_bits_added += bits_per_val;
            if (temp_bits_added >= total_num_bits) break;
            num_encoded_bits += kInterleavedBits[i];
            temp_bits_added += kInterleavedBits[i];
            if (temp_bits_added >= total_num_bits) break;
        }

        int non_bit_encoding = -1;
        for (int j = (1 << num_encoded_bits) - 1; j >= 0; --j)
        {
            bool matches = true;
            for (int i = 0; i < kNumVals; ++i)
            {
                if (valRange == 5)
                {
                    if (QuintEncodings[j][i] != non_bits[i]) { matches = false; break; }
                }
                else
                {
                    if (TritEncodings[j][i] != non_bits[i]) { matches = false; break; }
                }
            }
            if (matches) { non_bit_encoding = j; break; }
        }

        if (non_bit_encoding < 0) throw new InvalidOperationException();

        int non_bit_encoding_copy = non_bit_encoding;
        for (int i = 0; i < kNumVals; ++i)
        {
            if (bits_written + bits_per_val <= total_num_bits)
            {
                bit_sink.PutBits((uint)bits[i], bits_per_val);
                bits_written += bits_per_val;
            }
            int num_int_bits = kInterleavedBits[i];
            int int_bits = non_bit_encoding_copy & ((1 << num_int_bits) - 1);
            if (bits_written + num_int_bits <= total_num_bits)
            {
                bit_sink.PutBits((uint)int_bits, num_int_bits);
                bits_written += num_int_bits;
                non_bit_encoding_copy >>= num_int_bits;
            }
        }
    }
}
