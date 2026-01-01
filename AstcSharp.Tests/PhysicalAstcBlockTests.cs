using Xunit;
using AstcSharp;
using System;

namespace AstcSharp.Tests
{
    public class PhysicalAstcBlockTests
    {
        [Fact]
        public void GetBlockBits_RoundTrip()
        {
            var orig = new UInt128Ex(0x12345678ABCDEF00UL, 0xCAFEBABEDEADBEEFUL);
            var blk = new PhysicalAstcBlock(orig);
            var bits = blk.GetBlockBits();
            Assert.Equal(orig, bits);
        }

        [Fact]
        public void IsVoidExtent_DetectsKnownPattern()
        {
            var blk = new PhysicalAstcBlock(new UInt128Ex(0xFFFFFFFFFFFFFDFCUL, 0UL));
            Assert.True(blk.IsVoidExtent());
        }

        [Fact]
        public void TestConstructors()
        {
            ulong low = 0x0000000001FE000173UL;
            var blk1 = new PhysicalAstcBlock(low);

            // Construct from bytes (little-endian) to mimic the string constructor
            var bytes = new byte[16];
            BitConverter.GetBytes(low).CopyTo(bytes, 0);
            BitConverter.GetBytes(0UL).CopyTo(bytes, 8);
            var u = UInt128Ex.FromBytes(bytes);
            var blk2 = new PhysicalAstcBlock(u);

            Assert.Equal(blk1.GetBlockBits(), blk2.GetBlockBits());
        }

        [Fact]
        public void TestWeightRange()
        {
            var blk1 = new PhysicalAstcBlock(0x0000000001FE000173UL);
            var wr = blk1.WeightRange();
            Assert.NotNull(wr);
            Assert.Equal(7, wr.Value);

            var blk2 = new PhysicalAstcBlock(0x0000000001FE000373UL);
            Assert.Null(blk2.WeightRange());

            var non_shared_cem = new PhysicalAstcBlock(0x4000000000800D44UL);
            var wr2 = non_shared_cem.WeightRange();
            Assert.NotNull(wr2);
            Assert.Equal(1, wr2.Value);

            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.Null(kErrorBlock.WeightRange());
        }

        [Fact]
        public void TestWeightDims()
        {
            var blk1 = new PhysicalAstcBlock(0x0000000001FE000173UL);
            var dims = blk1.WeightGridDimensions();
            Assert.NotNull(dims);
            Assert.Equal(6, dims.Value.Item1);
            Assert.Equal(5, dims.Value.Item2);

            var blk2 = new PhysicalAstcBlock(0x0000000001FE000373UL);
            var dims2 = blk2.WeightGridDimensions();
            Assert.Null(dims2);
            var err = blk2.IsIllegalEncoding();
            Assert.NotNull(err);
            Assert.Contains("Too many bits", err);

            var blk3 = new PhysicalAstcBlock(0x0000000001FE0005FFUL);
            var dims3 = blk3.WeightGridDimensions();
            Assert.NotNull(dims3);
            Assert.Equal(3, dims3.Value.Item1);
            Assert.Equal(5, dims3.Value.Item2);

            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.Null(kErrorBlock.WeightGridDimensions());

            var non_shared_cem = new PhysicalAstcBlock(0x4000000000800D44UL);
            var dims4 = non_shared_cem.WeightGridDimensions();
            Assert.NotNull(dims4);
            Assert.Equal(8, dims4.Value.Item1);
            Assert.Equal(8, dims4.Value.Item2);
        }

        [Fact]
        public void TestDualPlane()
        {
            var blk1 = new PhysicalAstcBlock(0x0000000001FE000173UL);
            Assert.False(blk1.IsDualPlane());

            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.False(kErrorBlock.IsDualPlane());

            var blk2 = new PhysicalAstcBlock(0x0000000001FE000573UL);
            Assert.False(blk2.IsDualPlane());
            Assert.Null(blk2.WeightGridDimensions());
            var err = blk2.IsIllegalEncoding();
            Assert.NotNull(err);
            Assert.Contains("Too many bits", err);

            var blk3 = new PhysicalAstcBlock(0x0000000001FE0005FFUL);
            Assert.True(blk3.IsDualPlane());

            var blk4 = new PhysicalAstcBlock(0x0000000001FE000108UL);
            Assert.False(blk4.IsDualPlane());
            Assert.Null(blk4.IsIllegalEncoding());
        }

        [Fact]
        public void TestNumWeightBits()
        {
            var blk1 = new PhysicalAstcBlock(0x0000000001FE000173UL);
            Assert.Equal(90, blk1.WeightBitCount());

            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.Null(kErrorBlock.WeightBitCount());

            var void_extent = new PhysicalAstcBlock(0xFFF8003FFE000DFCUL);
            Assert.Null(void_extent.WeightBitCount());

            var blk2 = new PhysicalAstcBlock(0x0000000001FE000573UL);
            Assert.Null(blk2.WeightBitCount());

            var blk3 = new PhysicalAstcBlock(0x0000000001FE0005FFUL);
            Assert.Equal(90, blk3.WeightBitCount());
        }

        [Fact]
        public void TestStartWeightBit()
        {
            var b = new PhysicalAstcBlock(0x4000000000800D44UL);
            Assert.Equal(64, b.WeightStartBit());

            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.Null(kErrorBlock.WeightStartBit());

            var void_extent = new PhysicalAstcBlock(0xFFF8003FFE000DFCUL);
            Assert.Null(void_extent.WeightStartBit());
        }

        [Fact]
        public void TestErrorBlocksAndPartitions()
        {
            // Valid blocks
            Assert.Null(new PhysicalAstcBlock(0x0000000001FE000173UL).IsIllegalEncoding());
            Assert.Null(new PhysicalAstcBlock(0x0000000001FE0005FFUL).IsIllegalEncoding());
            Assert.Null(new PhysicalAstcBlock(0x0000000001FE000108UL).IsIllegalEncoding());

            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            var err = kErrorBlock.IsIllegalEncoding();
            Assert.NotNull(err);
            Assert.Contains("Reserved block mode", err);

            var err_blk = new PhysicalAstcBlock(0x0000000001FE000573UL);
            var errStr = err_blk.IsIllegalEncoding();
            Assert.NotNull(errStr);
            Assert.Contains("Too many bits required for weight grid", errStr);

            var err_blk2 = new PhysicalAstcBlock(0x0000000001FE0005A8UL);
            Assert.NotNull(err_blk2.IsIllegalEncoding());
            var err_blk3 = new PhysicalAstcBlock(0x0000000001FE000588UL);
            Assert.NotNull(err_blk3.IsIllegalEncoding());

            var err_blk4 = new PhysicalAstcBlock(0x0000000001FE00002UL);
            Assert.NotNull(err_blk4.IsIllegalEncoding());

            var dual_plane_four_parts = new PhysicalAstcBlock(0x000000000000001D1FUL);
            Assert.Null(dual_plane_four_parts.PartitionsCount());
            var e = dual_plane_four_parts.IsIllegalEncoding();
            Assert.NotNull(e);
            Assert.Contains("Both four partitions", e);
        }

        [Fact]
        public void TestVoidExtentBlocksAndCoords()
        {
            // Various valid block modes that aren't void extent blocks
            var non_void1 = new PhysicalAstcBlock(0x0000000001FE000173UL);
            Assert.False(non_void1.IsVoidExtent());
            var non_void2 = new PhysicalAstcBlock(0x0000000001FE0005FFUL);
            Assert.False(non_void1.IsVoidExtent());
            var non_void3 = new PhysicalAstcBlock(0x0000000001FE000108UL);
            Assert.False(non_void1.IsVoidExtent());

            // Error block is not a void extent block
            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.False(kErrorBlock.IsVoidExtent());

            // A valid void extent block
            var void_extent_encoding = new PhysicalAstcBlock(0xFFF8003FFE000DFCUL, 0UL);
            Assert.Null(void_extent_encoding.IsIllegalEncoding());
            Assert.True(void_extent_encoding.IsVoidExtent());

            // If we modify the high 64 bits it shouldn't change anything
            var modified = new PhysicalAstcBlock(0xFFF8003FFE000DFCUL, 0xdeadbeefdeadbeef);
            Assert.Null(modified.IsIllegalEncoding());
            Assert.True(modified.IsVoidExtent());
        }

        [Fact]
        public void TestVoidExtentCoordinates()
        {
            // Void extent coords for the single-ulong representation
            var coords = new PhysicalAstcBlock(0xFFF8003FFE000DFCUL).VoidExtentCoords();
            Assert.NotNull(coords);
            Assert.Equal(0, coords[0]);
            Assert.Equal(8191, coords[1]);
            Assert.Equal(0, coords[2]);
            Assert.Equal(8191, coords[3]);

            // If we set the coords to all 1's then it's still a void extent
            // block, but there aren't any void extent coords.
            var be_all_ones = new PhysicalAstcBlock(0xFFFFFFFFFFFFFDFCUL);
            Assert.Null(be_all_ones.IsIllegalEncoding());
            Assert.True(be_all_ones.IsVoidExtent());
            Assert.Null(be_all_ones.VoidExtentCoords());

            // If we set the void extent coords to something where the coords are
            // >= each other, then the encoding is illegal.
            Assert.NotNull(new PhysicalAstcBlock(0x0008004002001DFCUL).IsIllegalEncoding());
            Assert.NotNull(new PhysicalAstcBlock(0x0007FFC001FFFDFCUL).IsIllegalEncoding());
        }

        [Fact]
        public void TestNumPartitionsAndEndpointModes()
        {
            Assert.Equal(1, new PhysicalAstcBlock(0x0000000001FE000173UL).PartitionsCount());
            Assert.Equal(1, new PhysicalAstcBlock(0x0000000001FE0005FFUL).PartitionsCount());
            Assert.Equal(1, new PhysicalAstcBlock(0x0000000001FE000108UL).PartitionsCount());

            Assert.Null(new PhysicalAstcBlock(0x000000000000000973UL).PartitionsCount());
            Assert.Null(new PhysicalAstcBlock(0x000000000000001173UL).PartitionsCount());
            Assert.Null(new PhysicalAstcBlock(0x000000000000001973UL).PartitionsCount());

            var non_shared_cem = new PhysicalAstcBlock(0x4000000000800D44UL);
            Assert.Equal(2, non_shared_cem.PartitionsCount());

            var blk1 = new PhysicalAstcBlock(0x000000000000001961UL);
            for (int i = 0; i < 4; ++i)
            {
                var mode = blk1.GetEndpointMode(i);
                Assert.Equal(ColorEndpointMode.LdrLumaDirect, mode);
            }

            Assert.Null(new PhysicalAstcBlock(0xFFF8003FFE000DFCUL).GetEndpointMode(0));
            Assert.Null(new PhysicalAstcBlock(0x0000000001FE000173UL).GetEndpointMode(1));
            Assert.Null(new PhysicalAstcBlock(0x0000000001FE000173UL).GetEndpointMode(-1));
            Assert.Null(new PhysicalAstcBlock(0x0000000001FE000173UL).GetEndpointMode(100));

            var non_shared = new PhysicalAstcBlock(0x4000000000800D44UL);
            Assert.Equal(ColorEndpointMode.LdrLumaDirect, non_shared.GetEndpointMode(0));
            Assert.Equal(ColorEndpointMode.LdrLumaBaseOffset, non_shared.GetEndpointMode(1));
        }

        [Fact]
        public void TestPartitionIDAndColorBitsAndRanges()
        {
            Assert.Equal(0x3FF, new PhysicalAstcBlock(0x4000000000FFED44UL).PartitionId());
            Assert.Equal(0x155, new PhysicalAstcBlock(0x4000000000AAAD44UL).PartitionId());

            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.Null(kErrorBlock.PartitionId());
            Assert.Null(new PhysicalAstcBlock(0xFFF8003FFE000DFCUL).PartitionId());

            Assert.Equal(2, new PhysicalAstcBlock(0x0000000001FE000173UL).ColorValuesCount());
            Assert.Equal(16, new PhysicalAstcBlock(0x0000000001FE000173UL).ColorBitCount());

            Assert.Null(kErrorBlock.ColorValuesCount());
            Assert.Null(kErrorBlock.ColorBitCount());

            Assert.Equal(4, new PhysicalAstcBlock(0xFFF8003FFE000DFCUL).ColorValuesCount());
            Assert.Equal(64, new PhysicalAstcBlock(0xFFF8003FFE000DFCUL).ColorBitCount());

            Assert.Equal(255, new PhysicalAstcBlock(0x0000000001FE000173UL).ColorValuesRange());
            Assert.Null(kErrorBlock.ColorValuesRange());
            Assert.Equal((1 << 16) - 1, new PhysicalAstcBlock(0xFFF8003FFE000DFCUL).ColorValuesRange());

            Assert.Equal(64, new PhysicalAstcBlock(0xFFF8003FFE000DFCUL).ColorStartBit());
            Assert.Null(kErrorBlock.ColorStartBit());
            Assert.Equal(17, new PhysicalAstcBlock(0x0000000001FE000173UL).ColorStartBit());
            Assert.Equal(17, new PhysicalAstcBlock(0x0000000001FE0005FFUL).ColorStartBit());
            Assert.Equal(17, new PhysicalAstcBlock(0x0000000001FE000108UL).ColorStartBit());

            Assert.Equal(29, new PhysicalAstcBlock(0x4000000000FFED44UL).ColorStartBit());
            Assert.Equal(29, new PhysicalAstcBlock(0x4000000000AAAD44UL).ColorStartBit());
        }
    }
}
