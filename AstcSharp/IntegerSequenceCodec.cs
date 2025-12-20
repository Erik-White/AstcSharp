// Port of astc-codec/src/decoder/integer_sequence_codec.{h,cc}
using System;
using System.Collections.Generic;
using System.Linq;

namespace AstcSharp
{
    internal class IntegerSequenceCodec
    {
        private const int kLog2MaxRangeForBits = 8;
        private const int kNumPossibleRanges = 3 * kLog2MaxRangeForBits - 3;

        private static readonly int[] kInterleavedQuintBits = { 3, 2, 2 };
        private static readonly int[] kInterleavedTritBits = { 2, 2, 1, 2, 1 };

        private static readonly int[] kMaxRanges = InitMaxRanges();

        public enum EncodingMode { kTritEncoding = 0, kQuintEncoding, kBitEncoding }

        protected EncodingMode encoding_;
        protected int bits_;

        protected IntegerSequenceCodec(int range)
        {
            int trits, quints, bits;
            GetCountsForRange(range, out trits, out quints, out bits);
            InitializeWithCounts(trits, quints, bits);
        }

        protected IntegerSequenceCodec(int trits, int quints, int bits)
        {
            InitializeWithCounts(trits, quints, bits);
        }

        private static int[] InitMaxRanges()
        {
            var ranges = new List<int>(kNumPossibleRanges);
            void add_val(int val)
            {
                if (val <= 0 || (1 << kLog2MaxRangeForBits) <= val) return;
                ranges.Add(val);
            }
            for (int i = 0; i <= kLog2MaxRangeForBits; ++i)
            {
                add_val(3 * (1 << i) - 1);
                add_val(5 * (1 << i) - 1);
                add_val((1 << i) - 1);
            }
            ranges.Sort();
            return ranges.ToArray();
        }

        private static bool IsPow2(int x) => x == 0 || (x & (x - 1)) == 0;

        public static void GetCountsForRange(int range, out int trits, out int quints, out int bits)
        {
            if (range <= 0 || range >= (1 << kLog2MaxRangeForBits))
                throw new ArgumentOutOfRangeException(nameof(range));

            trits = quints = bits = 0;
            int idx = Array.FindIndex(kMaxRanges, v => v >= range);
            if (idx < 0) idx = kMaxRanges.Length - 1;
            int max_vals_for_range = kMaxRanges[idx] + 1;

            if ((max_vals_for_range % 3 == 0) && IsPow2(max_vals_for_range / 3))
            {
                bits = Log2Floor(max_vals_for_range / 3);
                trits = 1;
            }
            else if ((max_vals_for_range % 5 == 0) && IsPow2(max_vals_for_range / 5))
            {
                bits = Log2Floor(max_vals_for_range / 5);
                quints = 1;
            }
            else if (IsPow2(max_vals_for_range))
            {
                bits = Log2Floor(max_vals_for_range);
            }
        }

        public static int GetBitCount(int num_vals, int trits, int quints, int bits)
        {
            int trit_bit_count = ((num_vals * 8 * trits) + 4) / 5;
            int quint_bit_count = ((num_vals * 7 * quints) + 2) / 3;
            int base_bit_count = num_vals * bits;
            return trit_bit_count + quint_bit_count + base_bit_count;
        }

        // Convenience wrapper used by reference tests: determine the counts for the
        // given range and return the total number of bits needed for num_vals.
        public static int GetBitCountForRange(int num_vals, int range)
        {
            GetCountsForRange(range, out var trits, out var quints, out var bits);
            return GetBitCount(num_vals, trits, quints, bits);
        }

        protected void InitializeWithCounts(int trits, int quints, int bits)
        {
            if (trits != 0 && quints != 0) throw new InvalidOperationException();
            if (trits > 1 || quints > 1) throw new InvalidOperationException();

            if (trits > 0) encoding_ = EncodingMode.kTritEncoding;
            else if (quints > 0) encoding_ = EncodingMode.kQuintEncoding;
            else encoding_ = EncodingMode.kBitEncoding;

            bits_ = bits;
        }

        protected int NumValsPerBlock()
        {
            int[] kNumValsByEncoding = { 5, 3, 1 };
            return kNumValsByEncoding[(int)encoding_];
        }

        protected int EncodedBlockSize()
        {
            int[] kExtraBlockSizeByEncoding = { 8, 7, 0 };
            int num_vals = NumValsPerBlock();
            return kExtraBlockSizeByEncoding[(int)encoding_] + num_vals * bits_;
        }

        private static int Log2Floor(int v)
        {
            int r = 0;
            while ((1 << (r + 1)) <= v) r++;
            return r;
        }

        private static readonly int[][] kTritEncodings = new int[256][]
        {
            new int[]{0,0,0,0,0}, new int[]{1,0,0,0,0}, new int[]{2,0,0,0,0}, new int[]{0,0,2,0,0}, new int[]{0,1,0,0,0}, new int[]{1,1,0,0,0}, new int[]{2,1,0,0,0}, new int[]{1,0,2,0,0}, new int[]{0,2,0,0,0}, new int[]{1,2,0,0,0}, new int[]{2,2,0,0,0}, new int[]{2,0,2,0,0}, new int[]{0,2,2,0,0}, new int[]{1,2,2,0,0}, new int[]{2,2,2,0,0}, new int[]{2,0,2,0,0}, new int[]{0,0,1,0,0}, new int[]{1,0,1,0,0}, new int[]{2,0,1,0,0}, new int[]{0,1,2,0,0}, new int[]{0,1,1,0,0}, new int[]{1,1,1,0,0}, new int[]{2,1,1,0,0}, new int[]{1,1,2,0,0}, new int[]{0,2,1,0,0}, new int[]{1,2,1,0,0}, new int[]{2,2,1,0,0}, new int[]{2,1,2,0,0}, new int[]{0,0,0,2,2}, new int[]{1,0,0,2,2}, new int[]{2,0,0,2,2}, new int[]{0,0,2,2,2}, new int[]{0,0,0,1,0}, new int[]{1,0,0,1,0}, new int[]{2,0,0,1,0}, new int[]{0,0,2,1,0}, new int[]{0,1,0,1,0}, new int[]{1,1,0,1,0}, new int[]{2,1,0,1,0}, new int[]{1,0,2,1,0}, new int[]{0,2,0,1,0}, new int[]{1,2,0,1,0}, new int[]{2,2,0,1,0}, new int[]{2,0,2,1,0}, new int[]{0,2,2,1,0}, new int[]{1,2,2,1,0}, new int[]{2,2,2,1,0}, new int[]{2,0,2,1,0}, new int[]{0,0,1,1,0}, new int[]{1,0,1,1,0}, new int[]{2,0,1,1,0}, new int[]{0,1,2,1,0}, new int[]{0,1,1,1,0}, new int[]{1,1,1,1,0}, new int[]{2,1,1,1,0}, new int[]{1,1,2,1,0}, new int[]{0,2,1,1,0}, new int[]{1,2,1,1,0}, new int[]{2,2,1,1,0}, new int[]{2,1,2,1,0}, new int[]{0,1,0,2,2}, new int[]{1,1,0,2,2}, new int[]{2,1,0,2,2}, new int[]{1,0,2,2,2}, new int[]{0,0,0,2,0}, new int[]{1,0,0,2,0}, new int[]{2,0,0,2,0}, new int[]{0,0,2,2,0}, new int[]{0,1,0,2,0}, new int[]{1,1,0,2,0}, new int[]{2,1,0,2,0}, new int[]{1,0,2,2,0}, new int[]{0,2,0,2,0}, new int[]{1,2,0,2,0}, new int[]{2,2,0,2,0}, new int[]{2,0,2,2,0}, new int[]{0,2,2,2,0}, new int[]{1,2,2,2,0}, new int[]{2,2,2,2,0}, new int[]{2,0,2,2,0}, new int[]{0,0,1,2,0}, new int[]{1,0,1,2,0}, new int[]{2,0,1,2,0}, new int[]{0,1,2,2,0}, new int[]{0,1,1,2,0}, new int[]{1,1,1,2,0}, new int[]{2,1,1,2,0}, new int[]{1,1,2,2,0}, new int[]{0,2,1,2,0}, new int[]{1,2,1,2,0}, new int[]{2,2,1,2,0}, new int[]{2,1,2,2,0}, new int[]{0,2,0,2,2}, new int[]{1,2,0,2,2}, new int[]{2,2,0,2,2}, new int[]{2,0,2,2,2}, new int[]{0,0,0,0,2}, new int[]{1,0,0,0,2}, new int[]{2,0,0,0,2}, new int[]{0,0,2,0,2}, new int[]{0,1,0,0,2}, new int[]{1,1,0,0,2}, new int[]{2,1,0,0,2}, new int[]{1,0,2,0,2}, new int[]{0,2,0,0,2}, new int[]{1,2,0,0,2}, new int[]{2,2,0,0,2}, new int[]{2,0,2,0,2}, new int[]{0,2,2,0,2}, new int[]{1,2,2,0,2}, new int[]{2,2,2,0,2}, new int[]{2,0,2,0,2}, new int[]{0,0,1,0,2}, new int[]{1,0,1,0,2}, new int[]{2,0,1,0,2}, new int[]{0,1,2,0,2}, new int[]{0,1,1,0,2}, new int[]{1,1,1,0,2}, new int[]{2,1,1,0,2}, new int[]{1,1,2,0,2}, new int[]{0,2,1,0,2}, new int[]{1,2,1,0,2}, new int[]{2,2,1,0,2}, new int[]{2,1,2,0,2}, new int[]{0,2,2,2,2}, new int[]{1,2,2,2,2}, new int[]{2,2,2,2,2}, new int[]{2,0,2,2,2}, new int[]{0,0,0,0,1}, new int[]{1,0,0,0,1}, new int[]{2,0,0,0,1}, new int[]{0,0,2,0,1}, new int[]{0,1,0,0,1}, new int[]{1,1,0,0,1}, new int[]{2,1,0,0,1}, new int[]{1,0,2,0,1}, new int[]{0,2,0,0,1}, new int[]{1,2,0,0,1}, new int[]{2,2,0,0,1}, new int[]{2,0,2,0,1}, new int[]{0,2,2,0,1}, new int[]{1,2,2,0,1}, new int[]{2,2,2,0,1}, new int[]{2,0,2,0,1}, new int[]{0,0,1,0,1}, new int[]{1,0,1,0,1}, new int[]{2,0,1,0,1}, new int[]{0,1,2,0,1}, new int[]{0,1,1,0,1}, new int[]{1,1,1,0,1}, new int[]{2,1,1,0,1}, new int[]{1,1,2,0,1}, new int[]{0,2,1,0,1}, new int[]{1,2,1,0,1}, new int[]{2,2,1,0,1}, new int[]{2,1,2,0,1}, new int[]{0,0,1,2,2}, new int[]{1,0,1,2,2}, new int[]{2,0,1,2,2}, new int[]{0,1,2,2,2}, new int[]{0,0,0,1,1}, new int[]{1,0,0,1,1}, new int[]{2,0,0,1,1}, new int[]{0,0,2,1,1}, new int[]{0,1,0,1,1}, new int[]{1,1,0,1,1}, new int[]{2,1,0,1,1}, new int[]{1,0,2,1,1}, new int[]{0,2,0,1,1}, new int[]{1,2,0,1,1}, new int[]{2,2,0,1,1}, new int[]{2,0,2,1,1}, new int[]{0,2,2,1,1}, new int[]{1,2,2,1,1}, new int[]{2,2,2,1,1}, new int[]{2,0,2,1,1}, new int[]{0,0,1,1,1}, new int[]{1,0,1,1,1}, new int[]{2,0,1,1,1}, new int[]{0,1,2,1,1}, new int[]{0,1,1,1,1}, new int[]{1,1,1,1,1}, new int[]{2,1,1,1,1}, new int[]{1,1,2,1,1}, new int[]{0,2,1,1,1}, new int[]{1,2,1,1,1}, new int[]{2,2,1,1,1}, new int[]{2,1,2,1,1}, new int[]{0,1,1,2,2}, new int[]{1,1,1,2,2}, new int[]{2,1,1,2,2}, new int[]{1,1,2,2,2}, new int[]{0,0,0,2,1}, new int[]{1,0,0,2,1}, new int[]{2,0,0,2,1}, new int[]{0,0,2,2,1}, new int[]{0,1,0,2,1}, new int[]{1,1,0,2,1}, new int[]{2,1,0,2,1}, new int[]{1,0,2,2,1}, new int[]{0,2,0,2,1}, new int[]{1,2,0,2,1}, new int[]{2,2,0,2,1}, new int[]{2,0,2,2,1}, new int[]{0,2,2,2,1}, new int[]{1,2,2,2,1}, new int[]{2,2,2,2,1}, new int[]{2,0,2,2,1}, new int[]{0,0,1,2,1}, new int[]{1,0,1,2,1}, new int[]{2,0,1,2,1}, new int[]{0,1,2,2,1}, new int[]{0,1,1,2,1}, new int[]{1,1,1,2,1}, new int[]{2,1,1,2,1}, new int[]{1,1,2,2,1}, new int[]{0,2,1,2,1}, new int[]{1,2,1,2,1}, new int[]{2,2,1,2,1}, new int[]{2,1,2,2,1}, new int[]{0,2,1,2,2}, new int[]{1,2,1,2,2}, new int[]{2,2,1,2,2}, new int[]{2,1,2,2,2}, new int[]{0,0,0,1,2}, new int[]{1,0,0,1,2}, new int[]{2,0,0,1,2}, new int[]{0,0,2,1,2}, new int[]{0,1,0,1,2}, new int[]{1,1,0,1,2}, new int[]{2,1,0,1,2}, new int[]{1,0,2,1,2}, new int[]{0,2,0,1,2}, new int[]{1,2,0,1,2}, new int[]{2,2,0,1,2}, new int[]{2,0,2,1,2}, new int[]{0,2,2,1,2}, new int[]{1,2,2,1,2}, new int[]{2,2,2,1,2}, new int[]{2,0,2,1,2}, new int[]{0,0,1,1,2}, new int[]{1,0,1,1,2}, new int[]{2,0,1,1,2}, new int[]{0,1,2,1,2}, new int[]{0,1,1,1,2}, new int[]{1,1,1,1,2}, new int[]{2,1,1,1,2}, new int[]{1,1,2,1,2}, new int[]{0,2,1,1,2}, new int[]{1,2,1,1,2}, new int[]{2,2,1,1,2}, new int[]{2,1,2,1,2}, new int[]{0,2,2,2,2}, new int[]{1,2,2,2,2}, new int[]{2,2,2,2,2}, new int[]{2,1,2,2,2}
        };

        private static readonly int[][] kQuintEncodings = new int[128][]
        {
            new int[]{0,0,0}, new int[]{1,0,0}, new int[]{2,0,0}, new int[]{3,0,0}, new int[]{4,0,0}, new int[]{0,4,0}, new int[]{4,4,0}, new int[]{4,4,4}, new int[]{0,1,0}, new int[]{1,1,0}, new int[]{2,1,0}, new int[]{3,1,0}, new int[]{4,1,0}, new int[]{1,4,0}, new int[]{4,4,1}, new int[]{4,4,4}, new int[]{0,2,0}, new int[]{1,2,0}, new int[]{2,2,0}, new int[]{3,2,0}, new int[]{4,2,0}, new int[]{2,4,0}, new int[]{4,4,2}, new int[]{4,4,4}, new int[]{0,3,0}, new int[]{1,3,0}, new int[]{2,3,0}, new int[]{3,3,0}, new int[]{4,3,0}, new int[]{3,4,0}, new int[]{4,4,3}, new int[]{4,4,4}, new int[]{0,0,1}, new int[]{1,0,1}, new int[]{2,0,1}, new int[]{3,0,1}, new int[]{4,0,1}, new int[]{0,4,1}, new int[]{4,0,4}, new int[]{0,4,4}, new int[]{0,1,1}, new int[]{1,1,1}, new int[]{2,1,1}, new int[]{3,1,1}, new int[]{4,1,1}, new int[]{1,4,1}, new int[]{4,1,4}, new int[]{1,4,4}, new int[]{0,2,1}, new int[]{1,2,1}, new int[]{2,2,1}, new int[]{3,2,1}, new int[]{4,2,1}, new int[]{2,4,1}, new int[]{4,2,4}, new int[]{2,4,4}, new int[]{0,3,1}, new int[]{1,3,1}, new int[]{2,3,1}, new int[]{3,3,1}, new int[]{4,3,1}, new int[]{3,4,1}, new int[]{4,3,4}, new int[]{3,4,4}, new int[]{0,0,2}, new int[]{1,0,2}, new int[]{2,0,2}, new int[]{3,0,2}, new int[]{4,0,2}, new int[]{0,4,2}, new int[]{2,0,4}, new int[]{3,0,4}, new int[]{0,1,2}, new int[]{1,1,2}, new int[]{2,1,2}, new int[]{3,1,2}, new int[]{4,1,2}, new int[]{1,4,2}, new int[]{2,1,4}, new int[]{3,1,4}, new int[]{0,2,2}, new int[]{1,2,2}, new int[]{2,2,2}, new int[]{3,2,2}, new int[]{4,2,2}, new int[]{2,4,2}, new int[]{2,2,4}, new int[]{3,2,4}, new int[]{0,3,2}, new int[]{1,3,2}, new int[]{2,3,2}, new int[]{3,3,2}, new int[]{4,3,2}, new int[]{3,4,2}, new int[]{2,3,4}, new int[]{3,3,4}, new int[]{0,0,3}, new int[]{1,0,3}, new int[]{2,0,3}, new int[]{3,0,3}, new int[]{4,0,3}, new int[]{0,4,3}, new int[]{0,0,4}, new int[]{1,0,4}, new int[]{0,1,3}, new int[]{1,1,3}, new int[]{2,1,3}, new int[]{3,1,3}, new int[]{4,1,3}, new int[]{1,4,3}, new int[]{0,1,4}, new int[]{1,1,4}, new int[]{0,2,3}, new int[]{1,2,3}, new int[]{2,2,3}, new int[]{3,2,3}, new int[]{4,2,3}, new int[]{2,4,3}, new int[]{0,2,4}, new int[]{1,2,4}, new int[]{0,3,3}, new int[]{1,3,3}, new int[]{2,3,3}, new int[]{3,3,3}, new int[]{4,3,3}, new int[]{3,4,3}, new int[]{0,3,4}, new int[]{1,3,4}
        };

        // Decode a trit/quint block
        public static int[] DecodeISEBlock(int valRange, ulong blockBits, int numBits)
        {
            if (!(valRange == 3 || valRange == 5)) throw new ArgumentException("valRange");
            int kNumVals = (valRange == 5) ? 3 : 5;
            int[] kInterleavedBits = (valRange == 5) ? kInterleavedQuintBits : kInterleavedTritBits;

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

            int[] encodings = (valRange == 5) ? kQuintEncodings[encoded] : kTritEncodings[encoded];
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
            int[] kInterleavedBits = (valRange == 5) ? kInterleavedQuintBits : kInterleavedTritBits;

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
                        if (kQuintEncodings[j][i] != non_bits[i]) { matches = false; break; }
                    }
                    else
                    {
                        if (kTritEncodings[j][i] != non_bits[i]) { matches = false; break; }
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

        public static IReadOnlyList<int> ISERange() => kMaxRanges;
    }

    internal class IntegerSequenceDecoder : IntegerSequenceCodec
    {
        public IntegerSequenceDecoder(int range) : base(range) { }
        public IntegerSequenceDecoder(int trits, int quints, int bits) : base(trits, quints, bits) { }

        public List<int> Decode(int num_vals, ref BitStream bit_src)
        {
            int trits = (encoding_ == EncodingMode.kTritEncoding) ? 1 : 0;
            int quints = (encoding_ == EncodingMode.kQuintEncoding) ? 1 : 0;
            int total_num_bits = GetBitCount(num_vals, trits, quints, bits_);
            int bits_per_block = EncodedBlockSize();
            if (bits_per_block >= 64) throw new InvalidOperationException();

            int bits_left = total_num_bits;
            var result = new List<int>();
            while (bits_left > 0)
            {
                int toRead = Math.Min(bits_left, bits_per_block);
                bool ok = bit_src.GetBits<ulong>(toRead, out var block_bits);
                if (!ok) throw new InvalidOperationException();
                switch (encoding_)
                {
                    case EncodingMode.kTritEncoding:
                        result.AddRange(IntegerSequenceCodec.DecodeISEBlock(3, block_bits, bits_));
                        break;
                    case EncodingMode.kQuintEncoding:
                        result.AddRange(IntegerSequenceCodec.DecodeISEBlock(5, block_bits, bits_));
                        break;
                    case EncodingMode.kBitEncoding:
                        result.Add((int)block_bits);
                        break;
                }
                bits_left -= bits_per_block;
            }

            if (result.Count < num_vals) throw new InvalidOperationException();
            result.RemoveRange(num_vals, result.Count - num_vals);
            return result;
        }
    }

    internal class IntegerSequenceEncoder : IntegerSequenceCodec
    {
        private readonly List<int> vals_ = new List<int>();

        public IntegerSequenceEncoder(int range) : base(range) { }
        public IntegerSequenceEncoder(int trits, int quints, int bits) : base(trits, quints, bits) { }

        public void AddValue(int val) => vals_.Add(val);

        public void Encode(ref BitStream bit_sink)
        {
            int total_vals = vals_.Count;
            int trits = (encoding_ == EncodingMode.kTritEncoding) ? 1 : 0;
            int quints = (encoding_ == EncodingMode.kQuintEncoding) ? 1 : 0;
            int total_num_bits = GetBitCount(total_vals, trits, quints, bits_);

            int idx = 0;
            int bits_written = 0;
            while (idx < vals_.Count)
            {
                switch (encoding_)
                {
                    case EncodingMode.kTritEncoding:
                        var trit_vals = new List<int>();
                        for (int i = 0; i < 5; ++i)
                        {
                            if (idx < vals_.Count) trit_vals.Add(vals_[idx++]);
                            else trit_vals.Add(0);
                        }
                        IntegerSequenceCodec.EncodeISEBlock<int>(trit_vals, bits_, ref bit_sink, ref bits_written, total_num_bits);
                        break;
                    case EncodingMode.kQuintEncoding:
                        var quint_vals = new List<int>();
                        for (int i = 0; i < 3; ++i)
                        {
                            if (idx < vals_.Count) quint_vals.Add(vals_[idx++]);
                            else quint_vals.Add(0);
                        }
                        IntegerSequenceCodec.EncodeISEBlock<int>(quint_vals, bits_, ref bit_sink, ref bits_written, total_num_bits);
                        break;
                    case EncodingMode.kBitEncoding:
                        bit_sink.PutBits((uint)vals_[idx++], EncodedBlockSize());
                        break;
                }
            }
        }

        public void Reset() => vals_.Clear();
    }
}
