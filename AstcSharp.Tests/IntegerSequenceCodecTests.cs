using Xunit;
using AstcSharp;
using System.Collections.Generic;
using System;

namespace AstcSharp.Tests
{
    public class IntegerSequenceCodecTests
    {
        [Fact]
        public void GetCountsForRange_Basic()
        {
            // Check a few known ranges like the C++ test suite does for 1..31
            for (int i = 1; i < 32; ++i)
            {
                IntegerSequenceCodec.GetCountsForRange(i, out var t, out var q, out var b);
                Assert.True(t >= 0);
            }
        }

        [Fact]
        public void GetCountsForRange_Exact()
        {
            int[][] expected = new int[31][] {
                new int[]{0,0,1}, new int[]{1,0,0}, new int[]{0,0,2}, new int[]{0,1,0}, new int[]{1,0,1},
                new int[]{0,0,3}, new int[]{0,0,3}, new int[]{0,1,1}, new int[]{0,1,1}, new int[]{1,0,2},
                new int[]{1,0,2}, new int[]{0,0,4}, new int[]{0,0,4}, new int[]{0,0,4}, new int[]{0,0,4},
                new int[]{0,1,2}, new int[]{0,1,2}, new int[]{0,1,2}, new int[]{0,1,2}, new int[]{1,0,3},
                new int[]{1,0,3}, new int[]{1,0,3}, new int[]{1,0,3}, new int[]{0,0,5}, new int[]{0,0,5},
                new int[]{0,0,5}, new int[]{0,0,5}, new int[]{0,0,5}, new int[]{0,0,5}, new int[]{0,0,5},
                new int[]{0,0,5}
            };

            for (int i = 1; i < 32; ++i)
            {
                IntegerSequenceCodec.GetCountsForRange(i, out var t, out var q, out var b);
                var exp = expected[i - 1];
                Assert.Equal(exp[0], t);
                Assert.Equal(exp[1], q);
                Assert.Equal(exp[2], b);
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => IntegerSequenceCodec.GetCountsForRange(0, out _, out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => IntegerSequenceCodec.GetCountsForRange(256, out _, out _, out _));

            IntegerSequenceCodec.GetCountsForRange(1, out var t1, out var q1, out var b1);
            Assert.Equal(0, t1);
            Assert.Equal(0, q1);
            Assert.Equal(1, b1);
        }

        [Fact]
        public void GetBitCount_ForVarious()
        {
            // bits = 1
            int trits = 0, quints = 0, bits = 1;
            for (int i = 0; i < 64; ++i)
            {
                Assert.Equal(i, IntegerSequenceCodec.GetBitCount(i, trits, quints, bits));
                Assert.Equal(i, IntegerSequenceCodec.GetBitCountForRange(i, 1));
            }

            // bits = 2
            trits = 0; quints = 0; bits = 2;
            for (int i = 0; i < 64; ++i)
            {
                Assert.Equal(2 * i, IntegerSequenceCodec.GetBitCount(i, trits, quints, bits));
                Assert.Equal(2 * i, IntegerSequenceCodec.GetBitCountForRange(i, 3));
            }

            // trits case: 15 values, trits=1, bits=3
            trits = 1; quints = 0; bits = 3;
            Assert.Equal(8 * 3 + 15 * 3, IntegerSequenceCodec.GetBitCount(15, trits, quints, bits));
            Assert.Equal(IntegerSequenceCodec.GetBitCountForRange(15, 23), IntegerSequenceCodec.GetBitCount(15, trits, quints, bits));

            // trits case: 13 values, trits=1, bits=2 -> expected 47
            trits = 1; quints = 0; bits = 2;
            Assert.Equal(47, IntegerSequenceCodec.GetBitCount(13, trits, quints, bits));
            Assert.Equal(IntegerSequenceCodec.GetBitCountForRange(13, 11), IntegerSequenceCodec.GetBitCount(13, trits, quints, bits));

            // quints case: 6 values, quints=1, bits=4
            trits = 0; quints = 1; bits = 4;
            Assert.Equal(7 * 2 + 6 * 4, IntegerSequenceCodec.GetBitCount(6, trits, quints, bits));
            Assert.Equal(IntegerSequenceCodec.GetBitCountForRange(6, 79), IntegerSequenceCodec.GetBitCount(6, trits, quints, bits));

            // quints case: 7 values, quints=1, bits=3
            trits = 0; quints = 1; bits = 3;
            Assert.Equal(/* first two quint blocks */ 7 * 2 +
                         /* first two blocks of bits */ 6 * 3 +
                         /* last quint block without the high order four bits */ 3 +
                         /* last block with one set of three bits */ 3,
                         IntegerSequenceCodec.GetBitCount(7, trits, quints, bits));
        }

        [Fact]
        public void EncodeDecode_QuintExample()
        {
            const int kValueRange = 79;
            var bitSink = new BitStream();
            var enc = new IntegerSequenceEncoder(kValueRange);
            enc.AddValue(3);
            enc.AddValue(79);
            enc.AddValue(37);
            enc.Encode(ref bitSink);

            Assert.Equal<uint>(19, bitSink.Bits);

            bool ok = bitSink.GetBits<ulong>(19, out var encoded);
            Assert.True(ok);
            Assert.Equal<ulong>(0x4A7D3, encoded);

            var bitSrc = new BitStream(encoded, 19);
            var dec = new IntegerSequenceDecoder(kValueRange);
            var decoded = dec.Decode(3, ref bitSrc);
            Assert.Equal(new List<int> { 3, 79, 37 }, decoded);
        }

        [Fact]
        public void EncodeDecode_TritExample()
        {
            const int kValueRange = 11;
            var enc = new IntegerSequenceEncoder(kValueRange);
            enc.AddValue(7);
            enc.AddValue(5);
            enc.AddValue(3);
            enc.AddValue(6);
            enc.AddValue(10);

            var bitSink = new BitStream();
            enc.Encode(ref bitSink);
            Assert.Equal<uint>(18, bitSink.Bits);

            bool ok = bitSink.GetBits<ulong>(18, out var encoded);
            Assert.True(ok);
            Assert.Equal<ulong>(0x37357, encoded);

            var bitSrc = new BitStream(encoded, 19);
            var dec = new IntegerSequenceDecoder(kValueRange);
            var decoded = dec.Decode(5, ref bitSrc);
            Assert.Equal(new List<int> { 7, 5, 3, 6, 10 }, decoded);
        }

        [Fact]
        public void DecodeThenEncode_Quint()
        {
            var vals = new List<int> { 16, 18, 17, 4, 7, 14, 10, 0 };
            const ulong kValEncoding = 0x2b9c83dc;

            var bitSrc = new BitStream(kValEncoding, 64);
            var dec = new IntegerSequenceDecoder(19);
            var decoded = dec.Decode(8, ref bitSrc);
            Assert.Equal(vals.Count, decoded.Count);
            for (int i = 0; i < vals.Count; ++i) Assert.Equal(vals[i], decoded[i]);

            var bitSink = new BitStream();
            var enc = new IntegerSequenceEncoder(19);
            foreach (var v in vals) enc.AddValue(v);
            enc.Encode(ref bitSink);
            Assert.Equal<uint>(35, bitSink.Bits);

            bool ok = bitSink.GetBits<ulong>(35, out var encoded);
            Assert.True(ok);
            Assert.Equal<ulong>(kValEncoding, encoded);
        }

        [Fact]
        public void DecodeThenEncode_Trits()
        {
            var vals = new List<int> { 6,0,0,2,0,0,0,0,8,0,0,0,0,8,8,0 };
            const ulong kValEncoding = 0x0004c0100001006UL;

            var bitSrc = new BitStream(kValEncoding, 64);
            var dec = new IntegerSequenceDecoder(11);
            var decoded = dec.Decode(vals.Count, ref bitSrc);
            Assert.Equal(vals.Count, decoded.Count);
            for (int i = 0; i < vals.Count; ++i) Assert.Equal(vals[i], decoded[i]);

            var bitSink = new BitStream();
            var enc = new IntegerSequenceEncoder(11);
            foreach (var v in vals) enc.AddValue(v);
            enc.Encode(ref bitSink);
            Assert.Equal<uint>(58, bitSink.Bits);

            bool ok = bitSink.GetBits<ulong>(58, out var encoded);
            Assert.True(ok);
            Assert.Equal<ulong>(kValEncoding, encoded);
        }

        [Fact]
        public void RandomReciprocation()
        {
            var rnd = new Random(unchecked((int)0xbad7357));
            for (int test = 0; test < 1600; ++test)
            {
                int num_vals = 4 + rnd.Next(0, 256) % 44;
                int range = 1 + rnd.Next(0, 256) % 63;

                int num_bits = IntegerSequenceCodec.GetBitCountForRange(num_vals, range);
                if (num_bits >= 64) continue;

                var generated = new List<int>(num_vals);
                for (int i = 0; i < num_vals; ++i) generated.Add(rnd.Next(range + 1));

                var bitSink = new BitStream();
                var enc = new IntegerSequenceEncoder(range);
                foreach (var v in generated) enc.AddValue(v);
                enc.Encode(ref bitSink);

                Assert.True(bitSink.GetBits<ulong>((int)bitSink.Bits, out var encoded));

                var bitSrc = new BitStream(encoded, 64);
                var dec = new IntegerSequenceDecoder(range);
                var decoded = dec.Decode(num_vals, ref bitSrc);

                Assert.Equal(generated.Count, decoded.Count);
                for (int i = 0; i < generated.Count; ++i) Assert.Equal(generated[i], decoded[i]);
            }
        }
    }
}
