using Xunit;
using AstcSharp.Reference;

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
                bool ok = stream.GetBits<uint>(1, out var bits);
                Assert.True(ok);
                Assert.Equal<uint>(0, bits);
                ok = stream.GetBits<uint>(1, out bits);
                Assert.False(ok);
            }

            {
                var stream = new BitStream(0b1010101010101010UL, 32);
                Assert.Equal<uint>(32, stream.Bits);
                bool ok = stream.GetBits<uint>(1, out var bits);
                Assert.True(ok);
                Assert.Equal<uint>(0, bits);

                ok = stream.GetBits<uint>(3, out bits);
                Assert.True(ok);
                Assert.Equal<uint>(0b101, bits);

                ok = stream.GetBits<uint>(8, out bits);
                Assert.True(ok);
                Assert.Equal<uint>(0b10101010, bits);

                Assert.Equal<uint>(20, stream.Bits);

                ok = stream.GetBits<uint>(20, out bits);
                Assert.True(ok);
                Assert.Equal<uint>(0b1010, bits);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                var stream = new BitStream(kAllBits, 64);
                Assert.Equal<uint>(64, stream.Bits);
                bool ok = stream.GetBits<ulong>(64, out var bits);
                Assert.True(ok);
                Assert.Equal<ulong>(kAllBits, bits);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream(kAllBits, 64);
                Assert.Equal<uint>(64, stream.Bits);
                bool ok = stream.GetBits<ulong>(40, out var bits);
                Assert.True(ok);
                Assert.Equal<ulong>(k40Bits, bits);
                Assert.Equal<uint>(24, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream(kAllBits, 32);
                bool ok = stream.GetBits<ulong>(0, out var bits);
                Assert.True(ok);
                Assert.Equal<ulong>(0, bits);
                ok = stream.GetBits<ulong>(32, out bits);
                Assert.True(ok);
                Assert.Equal<ulong>(k40Bits & 0xFFFFFFFFUL, bits);
                ok = stream.GetBits<ulong>(0, out bits);
                Assert.True(ok);
                Assert.Equal<ulong>(0, bits);
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

                bool ok = stream.GetBits<uint>(3, out var bits);
                Assert.True(ok);
                Assert.Equal<uint>(0b110, bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                var stream = new BitStream();
                stream.PutBits(kAllBits, 64);
                Assert.Equal<uint>(64, stream.Bits);
                bool ok = stream.GetBits<ulong>(64, out var bits);
                Assert.True(ok);
                Assert.Equal<ulong>(kAllBits, bits);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream();
                stream.PutBits(kAllBits, 40);
                bool ok = stream.GetBits<ulong>(40, out var bits);
                Assert.True(ok);
                Assert.Equal<ulong>(k40Bits, bits);
                Assert.Equal<uint>(0, stream.Bits);
            }

            {
                const ulong kAllBits = 0xFFFFFFFFFFFFFFFFUL;
                const ulong k40Bits = 0x000000FFFFFFFFFFUL;
                var stream = new BitStream();
                stream.PutBits(0U, 0);
                stream.PutBits((uint)(kAllBits & 0xFFFFFFFFUL), 32);
                stream.PutBits(0U, 0);

                bool ok = stream.GetBits<ulong>(32, out var bits);
                Assert.True(ok);
                Assert.Equal<ulong>(k40Bits & 0xFFFFFFFFUL, bits);
                Assert.Equal<uint>(0, stream.Bits);
            }
        }
    }
}
