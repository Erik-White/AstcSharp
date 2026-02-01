using Xunit;
using AstcSharp;
using AstcSharp.IO;

namespace AstcSharp.Tests
{
    public class BitStreamTests
    {
        [Fact]
        public void Decode_Basic()
        {
            // Equivalent tests from the C++ reference for decoding
            {
                var stream = new BitStream(0UL, 1);
                var bits = stream.GetBits<uint>(1);
                Assert.NotNull(bits);
                Assert.Equal<uint>(0, bits.Value);
                bits = stream.GetBits<uint>(1);
                Assert.Null(bits);
            }

            {
                var stream = new BitStream(0b1010101010101010UL, 32);
                Assert.Equal<uint>(32, stream.Bits);
                var bits = stream.GetBits<uint>(1);
                Assert.NotNull(bits);
                Assert.Equal<uint>(0, bits.Value);

                bits = stream.GetBits<uint>(3);
                Assert.NotNull(bits);
                Assert.Equal<uint>(0b101, bits.Value);

                bits = stream.GetBits<uint>(8);
                Assert.NotNull(bits);
                Assert.Equal<uint>(0b10101010, bits.Value);

                Assert.Equal<uint>(20, stream.Bits);

                bits = stream.GetBits<uint>(20);
                Assert.NotNull(bits);
                Assert.Equal<uint>(0b1010, bits.Value);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                var stream = new BitStream(kAllBits, 64);
                Assert.Equal<uint>(64, stream.Bits);
                var bits = stream.GetBits<ulong>(64);
                Assert.NotNull(bits);
                Assert.Equal(kAllBits, bits.Value);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream(kAllBits, 64);
                Assert.Equal<uint>(64, stream.Bits);
                var bits = stream.GetBits<ulong>(40);
                Assert.NotNull(bits);
                Assert.Equal(k40Bits, bits.Value);
                Assert.Equal<uint>(24, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream(kAllBits, 32);
                var bits = stream.GetBits<ulong>(0);
                Assert.NotNull(bits);
                Assert.Equal<ulong>(0, bits.Value);
                bits = stream.GetBits<ulong>(32);
                Assert.NotNull(bits);
                Assert.Equal(k40Bits & 0xFFFFFFFFUL, bits.Value);
                bits = stream.GetBits<ulong>(0);
                Assert.NotNull(bits);
                Assert.Equal<ulong>(0, bits.Value);
                Assert.Equal<uint>(0, stream.Bits);
            }
        }

        [Fact]
        public void Encode_Basic()
        {
            {
                var stream = new BitStream();
                stream.PutBits(0U, 1);
                stream.PutBits(0b11U, 2);
                Assert.Equal<uint>(3, stream.Bits);

                var bits = stream.GetBits<uint>(3);
                Assert.NotNull(bits);
                Assert.Equal<uint>(0b110, bits.Value);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                var stream = new BitStream();
                stream.PutBits(kAllBits, 64);
                Assert.Equal<uint>(64, stream.Bits);
                var bits = stream.GetBits<ulong>(64);
                Assert.NotNull(bits);
                Assert.Equal(kAllBits, bits.Value);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream();
                stream.PutBits(kAllBits, 40);
                var bits = stream.GetBits<ulong>(40);
                Assert.NotNull(bits);
                Assert.Equal(k40Bits, bits.Value);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream();
                stream.PutBits(0U, 0);
                stream.PutBits((uint)(kAllBits & 0xFFFFFFFFUL), 32);
                stream.PutBits(0U, 0);

                var bits = stream.GetBits<ulong>(32);
                Assert.NotNull(bits);
                Assert.Equal(k40Bits & 0xFFFFFFFFUL, bits.Value);
                Assert.Equal<uint>(0, stream.Bits);
            }
        }
    }
}
