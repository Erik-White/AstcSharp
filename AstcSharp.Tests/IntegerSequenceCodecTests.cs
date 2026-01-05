using Xunit;
using System.Collections.Generic;
using System;
using AstcSharp.IO;
using AstcSharp.BiseEncoding;

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
                    var (mode, bitCount) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(i);
                    Assert.True(mode != BiseEncodingMode.Unknown, $"Range {i} yielded Unknown encoding mode");
                }
        }

        [Fact]
        public void GetCountsForRange_Exact()
        {
            (BiseEncodingMode, int)[] expected = new (BiseEncodingMode, int)[31] {
                (BiseEncodingMode.BitEncoding, 1),
                (BiseEncodingMode.TritEncoding, 0),
                (BiseEncodingMode.BitEncoding, 2),
                (BiseEncodingMode.QuintEncoding, 0),
                (BiseEncodingMode.TritEncoding, 1),
                (BiseEncodingMode.BitEncoding, 3),
                (BiseEncodingMode.BitEncoding, 3),
                (BiseEncodingMode.QuintEncoding, 1),
                (BiseEncodingMode.QuintEncoding, 1),
                (BiseEncodingMode.TritEncoding, 2),
                (BiseEncodingMode.TritEncoding, 2),
                (BiseEncodingMode.BitEncoding, 4),
                (BiseEncodingMode.BitEncoding, 4),
                (BiseEncodingMode.BitEncoding, 4),
                (BiseEncodingMode.BitEncoding, 4),
                (BiseEncodingMode.QuintEncoding, 2),
                (BiseEncodingMode.QuintEncoding, 2),
                (BiseEncodingMode.QuintEncoding, 2),
                (BiseEncodingMode.QuintEncoding, 2),
                (BiseEncodingMode.TritEncoding, 3),
                (BiseEncodingMode.TritEncoding, 3),
                (BiseEncodingMode.TritEncoding, 3),
                (BiseEncodingMode.TritEncoding, 3),
                (BiseEncodingMode.BitEncoding, 5),
                (BiseEncodingMode.BitEncoding, 5),
                (BiseEncodingMode.BitEncoding, 5),
                (BiseEncodingMode.BitEncoding, 5),
                (BiseEncodingMode.BitEncoding, 5),
                (BiseEncodingMode.BitEncoding, 5),
                (BiseEncodingMode.BitEncoding, 5),
                (BiseEncodingMode.BitEncoding, 5)
            };

            for (int i = 1; i < 32; ++i)
            {
                var (mode, bitCount) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(i);
                var (expectedMode, expectedBitCount) = expected[i - 1];
                Assert.Equal(expectedMode, mode);
                Assert.Equal(expectedBitCount, bitCount);
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => BoundedIntegerSequenceCodec.GetPackingModeBitCount(byte.MinValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => BoundedIntegerSequenceCodec.GetPackingModeBitCount(byte.MaxValue + 1));

            var (modeOne, bitCountOne) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(1);
            Assert.Equal(BiseEncodingMode.BitEncoding, modeOne);
            Assert.Equal(1, bitCountOne);
        }

        [Fact]
        public void GetBitCount_ForVarious()
        {
            // bits = 1
            int trits = 0, quints = 0, bits = 1;
            for (int i = 1; i < 64; i++)
            {
                Assert.Equal(i, BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.BitEncoding, i, bits));
                Assert.Equal(i, BoundedIntegerSequenceCodec.GetBitCountForRange(i, 1));
            }

            // bits = 2
            trits = 0; quints = 0; bits = 2;
            for (int i = 0; i < 64; i++)
            {
                Assert.Equal(i * 2, BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.BitEncoding, i, bits));
                Assert.Equal(i * 2, BoundedIntegerSequenceCodec.GetBitCountForRange(i, 3));
            }

            // trits case: 15 values, trits=1, bits=3
            trits = 1; quints = 0; bits = 3;
            Assert.Equal(8 * 3 + 15 * 3, BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.TritEncoding, 15, bits));
            Assert.Equal(BoundedIntegerSequenceCodec.GetBitCountForRange(15, 23), BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.TritEncoding, 15, bits));

            // trits case: 13 values, trits=1, bits=2 -> expected 47
            trits = 1; quints = 0; bits = 2;
            Assert.Equal(47, BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.TritEncoding, 13, bits));
            Assert.Equal(BoundedIntegerSequenceCodec.GetBitCountForRange(13, 11), BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.TritEncoding, 13, bits));

            // quints case: 6 values, quints=1, bits=4
            trits = 0; quints = 1; bits = 4;
            Assert.Equal(7 * 2 + 6 * 4, BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.QuintEncoding, 6, bits));
            Assert.Equal(BoundedIntegerSequenceCodec.GetBitCountForRange(6, 79), BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.QuintEncoding, 6, bits));

            // quints case: 7 values, quints=1, bits=3
            trits = 0; quints = 1; bits = 3;
            Assert.Equal(/* first two quint blocks */ 7 * 2 +
                         /* first two blocks of bits */ 6 * 3 +
                         /* last quint block without the high order four bits */ 3 +
                         /* last block with one set of three bits */ 3,
                         BoundedIntegerSequenceCodec.GetBitCount(BiseEncodingMode.QuintEncoding, 7, bits));
        }

        [Fact]
        public void EncodeDecode_QuintExample()
        {
            const int kValueRange = 79;
            var bitSink = new BitStream();
            var enc = new BoundedIntegerSequenceEncoder(kValueRange);
            enc.AddValue(3);
            enc.AddValue(79);
            enc.AddValue(37);
            enc.Encode(ref bitSink);

            Assert.Equal<uint>(19, bitSink.Bits);

            bool ok = bitSink.GetBits<ulong>(19, out var encoded);
            Assert.True(ok);
            Assert.Equal<ulong>(0x4A7D3, encoded);

            var bitSrc = new BitStream(encoded, 19);
            var dec = new BoundedIntegerSequenceDecoder(kValueRange);
            var decoded = dec.Decode(3, ref bitSrc);
            Assert.Equal(new List<int> { 3, 79, 37 }, decoded);
        }

        [Fact]
        public void EncodeDecode_TritExample()
        {
            const int kValueRange = 11;
            var enc = new BoundedIntegerSequenceEncoder(kValueRange);
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
            var dec = new BoundedIntegerSequenceDecoder(kValueRange);
            var decoded = dec.Decode(5, ref bitSrc);
            Assert.Equal(new List<int> { 7, 5, 3, 6, 10 }, decoded);
        }

        [Fact]
        public void DecodeThenEncode_Quint()
        {
            var vals = new List<int> { 16, 18, 17, 4, 7, 14, 10, 0 };
            const ulong kValEncoding = 0x2b9c83dc;

            var bitSrc = new BitStream(kValEncoding, 64);
            var dec = new BoundedIntegerSequenceDecoder(19);
            var decoded = dec.Decode(8, ref bitSrc);
            Assert.Equal(vals.Count, decoded.Count);
            for (int i = 0; i < vals.Count; ++i) Assert.Equal(vals[i], decoded[i]);

            var bitSink = new BitStream();
            var enc = new BoundedIntegerSequenceEncoder(19);
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
            var dec = new BoundedIntegerSequenceDecoder(11);
            var decoded = dec.Decode(vals.Count, ref bitSrc);
            Assert.Equal(vals.Count, decoded.Count);
            for (int i = 0; i < vals.Count; ++i) Assert.Equal(vals[i], decoded[i]);

            var bitSink = new BitStream();
            var enc = new BoundedIntegerSequenceEncoder(11);
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

                int num_bits = BoundedIntegerSequenceCodec.GetBitCountForRange(num_vals, range);
                if (num_bits >= 64) continue;

                var generated = new List<int>(num_vals);
                for (int i = 0; i < num_vals; ++i) generated.Add(rnd.Next(range + 1));

                var bitSink = new BitStream();
                var enc = new BoundedIntegerSequenceEncoder(range);
                foreach (var v in generated) enc.AddValue(v);
                enc.Encode(ref bitSink);

                Assert.True(bitSink.GetBits<ulong>((int)bitSink.Bits, out var encoded));

                var bitSrc = new BitStream(encoded, 64);
                var dec = new BoundedIntegerSequenceDecoder(range);
                var decoded = dec.Decode(num_vals, ref bitSrc);

                Assert.Equal(generated.Count, decoded.Count);
                for (int i = 0; i < generated.Count; ++i) Assert.Equal(generated[i], decoded[i]);
            }
        }
    }
}
