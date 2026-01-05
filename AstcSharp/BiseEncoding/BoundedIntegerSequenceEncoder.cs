using AstcSharp.IO;

namespace AstcSharp.BiseEncoding;

internal class BoundedIntegerSequenceEncoder : BoundedIntegerSequenceCodec
{
    private readonly List<int> _values = [];

    public BoundedIntegerSequenceEncoder(int range) : base(range) { }

    /// <summary>
    /// Adds a value to the encoding sequence.
    /// </summary>
    public void AddValue(int val) => _values.Add(val);

    /// <summary>
    /// Encodes and writes the stored values encoding to the sink. Repeated calls  will produce the same result.
    /// </summary>
    public void Encode(ref BitStream bitSink)
    {
        int totalBitCount = GetBitCount(_encoding, _values.Count, _bitCount);

        int index = 0;
        int bitsWrittenCount = 0;
        while (index < _values.Count)
        {
            switch (_encoding)
            {
                case BiseEncodingMode.TritEncoding:
                    var trits = new List<int>();
                    for (int i = 0; i < 5; ++i)
                    {
                        if (index < _values.Count) trits.Add(_values[index++]);
                        else trits.Add(0);
                    }
                    EncodeISEBlock<int>(trits, _bitCount, ref bitSink, ref bitsWrittenCount, totalBitCount);
                    break;
                case BiseEncodingMode.QuintEncoding:
                    var quints = new List<int>();
                    for (int i = 0; i < 3; ++i)
                    {
                        var value = index < _values.Count ? _values[index++] : 0;
                        quints.Add(value);
                    }
                    EncodeISEBlock<int>(quints, _bitCount, ref bitSink, ref bitsWrittenCount, totalBitCount);
                    break;
                case BiseEncodingMode.BitEncoding:
                    bitSink.PutBits((uint)_values[index++], GetEncodedBlockSize());
                    break;
            }
        }
    }

    /// <summary>
    /// Clear the stored values.
    /// </summary>
    public void Reset() => _values.Clear();

    private static void EncodeISEBlock<T>(List<int> vals, int bits_per_val, ref BitStream bit_sink, ref int bits_written, int total_num_bits) where T : unmanaged
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
