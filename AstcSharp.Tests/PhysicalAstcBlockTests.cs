using AstcSharp.Core;
using AstcSharp.ColorEncoding;
using AstcSharp.TexelBlock;

namespace AstcSharp.Tests;

public class PhysicalAstcBlockTests
{
    [Fact]
    public void GetBlockBits_RoundTrip()
    {
        var orig = (UInt128)0x12345678ABCDEF00UL | ((UInt128)0xCAFEBABEDEADBEEFUL << 64);
        var blk = PhysicalBlock.Create(orig);
        var bits = blk.BlockBits;
        Assert.Equal(orig, bits);
    }

    [Fact]
    public void IsVoidExtent_DetectsKnownPattern()
    {
        var blk = PhysicalBlock.Create((UInt128)0xFFFFFFFFFFFFFDFCUL);
        Assert.True(blk.IsVoidExtent);
    }

    [Fact]
    public void TestConstructors()
    {
        const ulong low = 0x0000000001FE000173UL;
        
        var blk1 = PhysicalBlock.Create(low);
        var blk2 = PhysicalBlock.Create((UInt128)low);

        Assert.Equal(blk1.BlockBits, blk2.BlockBits);
    }

    [Fact]
    public void TestWeightRange()
    {
        var blk1 = PhysicalBlock.Create(0x0000000001FE000173UL);
        var wr = blk1.GetWeightRange();
        Assert.NotNull(wr);
        Assert.Equal(7, wr.Value);

        var blk2 = PhysicalBlock.Create(0x0000000001FE000373UL);
        Assert.Null(blk2.GetWeightRange());

        var non_shared_cem = PhysicalBlock.Create(0x4000000000800D44UL);
        var wr2 = non_shared_cem.GetWeightRange();
        Assert.NotNull(wr2);
        Assert.Equal(1, wr2.Value);

        var kErrorBlock = PhysicalBlock.Create((UInt128)0UL);
        Assert.Null(kErrorBlock.GetWeightRange());
    }

    [Fact]
    public void TestWeightDims()
    {
        var blk1 = PhysicalBlock.Create(0x0000000001FE000173UL);
        var dims = blk1.GetWeightGridDimensions();
        Assert.NotNull(dims);
        Assert.Equal(6, dims.Value.Item1);
        Assert.Equal(5, dims.Value.Item2);

        var blk2 = PhysicalBlock.Create(0x0000000001FE000373UL);
        var dims2 = blk2.GetWeightGridDimensions();
        Assert.Null(dims2);
        var err = blk2.IdentifyInvalidEncodingIssues();
        Assert.NotNull(err);
        Assert.Contains("Too many bits", err);

        var blk3 = PhysicalBlock.Create(0x0000000001FE0005FFUL);
        var dims3 = blk3.GetWeightGridDimensions();
        Assert.NotNull(dims3);
        Assert.Equal(3, dims3.Value.Item1);
        Assert.Equal(5, dims3.Value.Item2);

        var kErrorBlock = PhysicalBlock.Create((UInt128)0UL);
        Assert.Null(kErrorBlock.GetWeightGridDimensions());

        var non_shared_cem = PhysicalBlock.Create(0x4000000000800D44UL);
        var dims4 = non_shared_cem.GetWeightGridDimensions();
        Assert.NotNull(dims4);
        Assert.Equal(8, dims4.Value.Item1);
        Assert.Equal(8, dims4.Value.Item2);
    }

    [Fact]
    public void TestDualPlane()
    {
        var blk1 = PhysicalBlock.Create(0x0000000001FE000173UL);
        Assert.False(blk1.IsDualPlane);

        var kErrorBlock = PhysicalBlock.Create((UInt128)0UL);
        Assert.False(kErrorBlock.IsDualPlane);

        var blk2 = PhysicalBlock.Create(0x0000000001FE000573UL);
        Assert.False(blk2.IsDualPlane);
        Assert.Null(blk2.GetWeightGridDimensions());
        var err = blk2.IdentifyInvalidEncodingIssues();
        Assert.NotNull(err);
        Assert.Contains("Too many bits", err);

        var blk3 = PhysicalBlock.Create(0x0000000001FE0005FFUL);
        Assert.True(blk3.IsDualPlane);

        var blk4 = PhysicalBlock.Create(0x0000000001FE000108UL);
        Assert.False(blk4.IsDualPlane);
        Assert.False(blk4.IsIllegalEncoding);
    }

    [Fact]
    public void TestNumWeightBits()
    {
        var blk1 = PhysicalBlock.Create(0x0000000001FE000173UL);
        Assert.Equal(90, blk1.GetWeightBitCount());

        var kErrorBlock = PhysicalBlock.Create((UInt128)0UL);
        Assert.Null(kErrorBlock.GetWeightBitCount());

        var void_extent = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);
        Assert.Null(void_extent.GetWeightBitCount());

        var blk2 = PhysicalBlock.Create(0x0000000001FE000573UL);
        Assert.Null(blk2.GetWeightBitCount());

        var blk3 = PhysicalBlock.Create(0x0000000001FE0005FFUL);
        Assert.Equal(90, blk3.GetWeightBitCount());
    }

    [Fact]
    public void TestStartWeightBit()
    {
        var b = PhysicalBlock.Create(0x4000000000800D44UL);
        Assert.Equal(64, b.GetWeightStartBit());

        var kErrorBlock = PhysicalBlock.Create((UInt128)0UL);
        Assert.Null(kErrorBlock.GetWeightStartBit());

        var void_extent = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);
        Assert.Null(void_extent.GetWeightStartBit());
    }

    [Fact]
    public void TestErrorBlocksAndPartitions()
    {
        // Valid blocks
        Assert.False(PhysicalBlock.Create(0x0000000001FE000173UL).IsIllegalEncoding);
        Assert.False(PhysicalBlock.Create(0x0000000001FE0005FFUL).IsIllegalEncoding);
        Assert.False(PhysicalBlock.Create(0x0000000001FE000108UL).IsIllegalEncoding);

            var kErrorBlock = PhysicalBlock.Create(UInt128.Zero);
        var err = kErrorBlock.IdentifyInvalidEncodingIssues();
        Assert.NotNull(err);
        Assert.Contains("Reserved block mode", err);

        var err_blk = PhysicalBlock.Create(0x0000000001FE000573UL);
        var errStr = err_blk.IdentifyInvalidEncodingIssues();
        Assert.NotNull(errStr);
        Assert.Contains("Too many bits required for weight grid", errStr);

        var err_blk2 = PhysicalBlock.Create(0x0000000001FE0005A8UL);
        Assert.NotNull(err_blk2.IdentifyInvalidEncodingIssues());
        var err_blk3 = PhysicalBlock.Create(0x0000000001FE000588UL);
        Assert.NotNull(err_blk3.IdentifyInvalidEncodingIssues());

        var err_blk4 = PhysicalBlock.Create(0x0000000001FE00002UL);
        Assert.NotNull(err_blk4.IdentifyInvalidEncodingIssues());

        var dual_plane_four_parts = PhysicalBlock.Create(0x000000000000001D1FUL);
        Assert.Null(dual_plane_four_parts.GetPartitionsCount());
        var e = dual_plane_four_parts.IdentifyInvalidEncodingIssues();
        Assert.NotNull(e);
        Assert.Contains("Both four partitions", e);
    }

    [Fact]
    public void TestVoidExtentBlocksAndCoords()
    {
        // Various valid block modes that aren't void extent blocks
        var non_void1 = PhysicalBlock.Create(0x0000000001FE000173UL);
        Assert.False(non_void1.IsVoidExtent);
        var non_void2 = PhysicalBlock.Create(0x0000000001FE0005FFUL);
        Assert.False(non_void1.IsVoidExtent);
        var non_void3 = PhysicalBlock.Create(0x0000000001FE000108UL);
        Assert.False(non_void1.IsVoidExtent);

        // Error block is not a void extent block
        var kErrorBlock = PhysicalBlock.Create(UInt128.Zero);
        Assert.False(kErrorBlock.IsVoidExtent);

        // A valid void extent block
        var void_extent_encoding = PhysicalBlock.Create(0xFFF8003FFE000DFCUL, 0UL);
        Assert.False(void_extent_encoding.IsIllegalEncoding);
        Assert.True(void_extent_encoding.IsVoidExtent);

        // If we modify the high 64 bits it shouldn't change anything
        var modified = PhysicalBlock.Create(0xFFF8003FFE000DFCUL, 0xdeadbeefdeadbeef);
        Assert.False(modified.IsIllegalEncoding);
        Assert.True(modified.IsVoidExtent);
    }

    [Fact]
    public void TestVoidExtentCoordinates()
    {
        // Void extent coords for the single-ulong representation
        var coords = PhysicalBlock.Create(0xFFF8003FFE000DFCUL).GetVoidExtentCoordinates();
        Assert.NotNull(coords);
        Assert.Equal(0, coords[0]);
        Assert.Equal(8191, coords[1]);
        Assert.Equal(0, coords[2]);
        Assert.Equal(8191, coords[3]);

        // If we set the coords to all 1's then it's still a void extent
        // block, but there aren't any void extent coords.
        var be_all_ones = PhysicalBlock.Create(0xFFFFFFFFFFFFFDFCUL);
        Assert.False(be_all_ones.IsIllegalEncoding);
        Assert.True(be_all_ones.IsVoidExtent);
        Assert.Null(be_all_ones.GetVoidExtentCoordinates());

        // If we set the void extent coords to something where the coords are
        // >= each other, then the encoding is illegal.
        Assert.True(PhysicalBlock.Create(0x0008004002001DFCUL).IsIllegalEncoding);
        Assert.True(PhysicalBlock.Create(0x0007FFC001FFFDFCUL).IsIllegalEncoding);
    }

    [Fact]
    public void TestNumPartitionsAndEndpointModes()
    {
        Assert.Equal(1, PhysicalBlock.Create(0x0000000001FE000173UL).GetPartitionsCount());
        Assert.Equal(1, PhysicalBlock.Create(0x0000000001FE0005FFUL).GetPartitionsCount());
        Assert.Equal(1, PhysicalBlock.Create(0x0000000001FE000108UL).GetPartitionsCount());

        Assert.Null(PhysicalBlock.Create(0x000000000000000973UL).GetPartitionsCount());
        Assert.Null(PhysicalBlock.Create(0x000000000000001173UL).GetPartitionsCount());
        Assert.Null(PhysicalBlock.Create(0x000000000000001973UL).GetPartitionsCount());

        var non_shared_cem = PhysicalBlock.Create(0x4000000000800D44UL);
        Assert.Equal(2, non_shared_cem.GetPartitionsCount());

        var blk1 = PhysicalBlock.Create(0x000000000000001961UL);
        for (int i = 0; i < 4; ++i)
        {
            var mode = blk1.GetEndpointMode(i);
            Assert.Equal(ColorEndpointMode.LdrLumaDirect, mode);
        }

        Assert.Null(PhysicalBlock.Create(0xFFF8003FFE000DFCUL).GetEndpointMode(0));
        Assert.Null(PhysicalBlock.Create(0x0000000001FE000173UL).GetEndpointMode(1));
        Assert.Null(PhysicalBlock.Create(0x0000000001FE000173UL).GetEndpointMode(-1));
        Assert.Null(PhysicalBlock.Create(0x0000000001FE000173UL).GetEndpointMode(100));

        var non_shared = PhysicalBlock.Create(0x4000000000800D44UL);
        Assert.Equal(ColorEndpointMode.LdrLumaDirect, non_shared.GetEndpointMode(0));
        Assert.Equal(ColorEndpointMode.LdrLumaBaseOffset, non_shared.GetEndpointMode(1));
    }

    [Fact]
    public void TestPartitionIDAndColorBitsAndRanges()
    {
        Assert.Equal(0x3FF, PhysicalBlock.Create(0x4000000000FFED44UL).GetPartitionId());
        Assert.Equal(0x155, PhysicalBlock.Create(0x4000000000AAAD44UL).GetPartitionId());

        var kErrorBlock = PhysicalBlock.Create(UInt128.Zero);
        Assert.Null(kErrorBlock.GetPartitionId());
        Assert.Null(PhysicalBlock.Create(0xFFF8003FFE000DFCUL).GetPartitionId());

        Assert.Equal(2, PhysicalBlock.Create(0x0000000001FE000173UL).GetColorValuesCount());
        Assert.Equal(16, PhysicalBlock.Create(0x0000000001FE000173UL).GetColorBitCount());

        Assert.Null(kErrorBlock.GetColorValuesCount());
        Assert.Null(kErrorBlock.GetColorBitCount());

        Assert.Equal(4, PhysicalBlock.Create(0xFFF8003FFE000DFCUL).GetColorValuesCount());
        Assert.Equal(64, PhysicalBlock.Create(0xFFF8003FFE000DFCUL).GetColorBitCount());

        Assert.Equal(255, PhysicalBlock.Create(0x0000000001FE000173UL).GetColorValuesRange());
        Assert.Null(kErrorBlock.GetColorValuesRange());
        Assert.Equal((1 << 16) - 1, PhysicalBlock.Create(0xFFF8003FFE000DFCUL).GetColorValuesRange());

        Assert.Equal(64, PhysicalBlock.Create(0xFFF8003FFE000DFCUL).GetColorStartBit());
        Assert.Null(kErrorBlock.GetColorStartBit());
        Assert.Equal(17, PhysicalBlock.Create(0x0000000001FE000173UL).GetColorStartBit());
        Assert.Equal(17, PhysicalBlock.Create(0x0000000001FE0005FFUL).GetColorStartBit());
        Assert.Equal(17, PhysicalBlock.Create(0x0000000001FE000108UL).GetColorStartBit());

        Assert.Equal(29, PhysicalBlock.Create(0x4000000000FFED44UL).GetColorStartBit());
        Assert.Equal(29, PhysicalBlock.Create(0x4000000000AAAD44UL).GetColorStartBit());
    }
}
