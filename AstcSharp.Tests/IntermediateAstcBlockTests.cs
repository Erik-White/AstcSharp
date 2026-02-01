using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.TexelBlock;

namespace AstcSharp.Tests;

public class IntermediateAstcBlockTests
{
    private static byte[] LoadASTCFile(string basename)
    {
        var filename = Path.Combine("TestData", "Input", basename + ".astc");
        Assert.True(File.Exists(filename), $"Testdata missing: {filename}");
        var data = File.ReadAllBytes(filename);
        Assert.True(data.Length >= 16, "ASTC file too small");
        return data.Skip(16).ToArray();
    }

    [Fact]
    public void TestUnpackError()
    {
        var kErrorBlock = PhysicalBlock.Create((UInt128)0UL);
        Assert.Null(IntermediateBlock.UnpackVoidExtent(kErrorBlock));
        Assert.Null(IntermediateBlock.UnpackIntermediateBlock(kErrorBlock));
    }

    [Fact]
    public void TestEndpointRangeErrorOnNotSettingWeights()
    {
        var data = new IntermediateBlock.IntermediateBlockData();
        data.weightRange = 15;
        data.weightGridX = 6;
        data.weightGridY = 6;
        Assert.Equal(-1, IntermediateBlock.EndpointRangeForBlock(data));

        var (err, dummy) = IntermediateBlock.Pack(data);
        Assert.NotNull(err);
        Assert.Contains("Incorrect number of weights", err);
    }

    [Fact]
    public void TestEndpointRangeErrorOnNotEnoughBits()
    {
        var data = new IntermediateBlock.IntermediateBlockData();
        data.weightRange = 1;
        data.partitionId = 0;
        data.endpoints = new List<IntermediateBlock.IntermediateEndpointData>();
        data.endpoints.Add(new IntermediateBlock.IntermediateEndpointData { mode = ColorEndpointMode.LdrRgbDirect });
        data.endpoints.Add(new IntermediateBlock.IntermediateEndpointData { mode = ColorEndpointMode.LdrRgbDirect });
        data.endpoints.Add(new IntermediateBlock.IntermediateEndpointData { mode = ColorEndpointMode.LdrRgbDirect });

        data.weightGridX = 8;
        data.weightGridY = 8;
        Assert.Equal(-2, IntermediateBlock.EndpointRangeForBlock(data));

        // Resize weights to match grid
        data.weights = new int[64];
        var (err, dummy) = IntermediateBlock.Pack(data);
        Assert.NotNull(err);
        Assert.Contains("illegal color range", err);
    }

    [Fact]
    public void TestEndpointRangeForBlock()
    {
        var data = new IntermediateBlock.IntermediateBlockData();
        data.weightRange = 2;
        data.endpoints = new List<IntermediateBlock.IntermediateEndpointData> { new IntermediateBlock.IntermediateEndpointData(), new IntermediateBlock.IntermediateEndpointData() };
        data.dualPlaneChannel = null;
        foreach (var ep in data.endpoints) ep.mode = ColorEndpointMode.LdrRgbDirect;

        var weight_params = new List<(int w, int h)>();
        for (int y = 2; y < 8; ++y)
            for (int x = 2; x < 8; ++x)
                weight_params.Add((x, y));

        weight_params.Sort((a, b) => (a.w * a.h).CompareTo(b.w * b.h));

        int last_color_range = 255;
        foreach (var p in weight_params)
        {
            data.weightGridX = p.w;
            data.weightGridY = p.h;
            int color_range = IntermediateBlock.EndpointRangeForBlock(data);
            Assert.True(color_range <= last_color_range);
            last_color_range = Math.Min(color_range, last_color_range);
        }
        Assert.True(last_color_range < 255);
    }

    [Fact]
    public void TestUnpackNonVoidExtentBlock()
    {
        var blk = PhysicalBlock.Create((UInt128)0x0000000001FE000173UL);
        var b = IntermediateBlock.UnpackIntermediateBlock(blk);
        Assert.NotNull(b);
        var data = b!;
        Assert.Equal(6, data.weightGridX);
        Assert.Equal(5, data.weightGridY);
        Assert.Equal(7, data.weightRange);
        Assert.Null(data.partitionId);
        Assert.Null(data.dualPlaneChannel);
        Assert.Equal(30, data.weights.Length);
        foreach (var w in data.weights) Assert.Equal(0, w);
        Assert.Single(data.endpoints);
        var ep = data.endpoints[0];
        Assert.Equal(ColorEndpointMode.LdrLumaDirect, ep.mode);
        Assert.Equal(2, ep.colors.Count);
        Assert.Equal(0, ep.colors[0]);
        Assert.Equal(255, ep.colors[1]);
    }

    [Fact]
    public void TestPackNonVoidExtentBlock()
    {
        var data = new IntermediateBlock.IntermediateBlockData();
        data.weightGridX = 6;
        data.weightGridY = 5;
        data.weightRange = 7;
        data.partitionId = null;
        data.dualPlaneChannel = null;
        data.weights = new int[30];
        var ep = new IntermediateBlock.IntermediateEndpointData { mode = ColorEndpointMode.LdrLumaDirect };
        ep.colors.Add(0); ep.colors.Add(255);
        data.endpoints.Add(ep);

        var (err, packed) = IntermediateBlock.Pack(data);
        Assert.Null(err);
        Assert.Equal((UInt128)0x0000000001FE000173UL, packed);
    }

    [Fact]
    public void TestUnpackVoidExtentBlock()
    {
        var void_blk = PhysicalBlock.Create((UInt128)0xFFFFFFFFFFFFFDFCUL);
        var b = IntermediateBlock.UnpackVoidExtent(void_blk);
        Assert.NotNull(b);
        var data = b.Value;
        Assert.Equal((ushort)0, data.r);
        Assert.Equal((ushort)0, data.g);
        Assert.Equal((ushort)0, data.b);
        Assert.Equal((ushort)0, data.a);
        foreach (var c in data.coords) Assert.Equal((1 << 13) - 1, c);

        var more_interesting = new UInt128(0xdeadbeefdeadbeefUL, 0xFFF8003FFE000DFCUL);
        b = IntermediateBlock.UnpackVoidExtent(PhysicalBlock.Create(more_interesting));
        Assert.NotNull(b);
        var other = b.Value;
        Assert.Equal((ushort)0xbeef, other.r);
        Assert.Equal((ushort)0xdead, other.g);
        Assert.Equal((ushort)0xbeef, other.b);
        Assert.Equal((ushort)0xdead, other.a);
        Assert.Equal(0, other.coords[0]);
        Assert.Equal(8191, other.coords[1]);
        Assert.Equal(0, other.coords[2]);
        Assert.Equal(8191, other.coords[3]);
    }

    [Fact]
    public void TestPackVoidExtentBlock()
    {
        var data = new IntermediateBlock.VoidExtentData();
        data.r = 0; data.g = 0; data.b = 0; data.a = 0;
        data.coords = new ushort[4];
        for (int i = 0; i < 4; ++i) data.coords[i] = (ushort)((1 << 13) - 1);

        var (err, packed) = IntermediateBlock.Pack(data);
        Assert.Null(err);
        Assert.Equal((UInt128)0xFFFFFFFFFFFFFDFCUL, packed);

        data.r = 0xbeef; data.g = 0xdead; data.b = 0xbeef; data.a = 0xdead;
        data.coords = new ushort[4] { 0, 8191, 0, 8191 };
        (err, packed) = IntermediateBlock.Pack(data);
        Assert.Null(err);
        Assert.Equal(new UInt128(0xdeadbeefdeadbeefUL, 0xFFF8003FFE000DFCUL), packed);
    }

    [Fact]
    public void TestPackUnpackWithSameCEM()
    {
        var orig = new UInt128(0xe8e8eaea20000980UL, 0x20000200cb73f045UL);
        var b = IntermediateBlock.UnpackIntermediateBlock(PhysicalBlock.Create(orig));
        Assert.NotNull(b);
        var (err, repacked) = IntermediateBlock.Pack(b!);
        Assert.Null(err);
        Assert.Equal(orig, repacked);

        orig = new UInt128(0x3300c30700cb01c5UL, 0x0573907b8c0f6879UL);
        b = IntermediateBlock.UnpackIntermediateBlock(PhysicalBlock.Create(orig));
        Assert.NotNull(b);
        (err, repacked) = IntermediateBlock.Pack(b!);
        Assert.Null(err);
        Assert.Equal(orig, repacked);
    }

    [Fact]
    public void TestPackingWithLargeGap()
    {
        var orig = new UInt128(0xBEDEAD0000000000UL, 0x0000000001FE032EUL);
        var b = IntermediateBlock.UnpackIntermediateBlock(PhysicalBlock.Create(orig));
        Assert.NotNull(b);
        var data = b!;
        Assert.Equal(2, data.weightGridX);
        Assert.Equal(3, data.weightGridY);
        Assert.Equal(15, data.weightRange);
        Assert.Null(data.partitionId);
        Assert.Null(data.dualPlaneChannel);
        Assert.Single(data.endpoints);
        Assert.Equal(ColorEndpointMode.LdrLumaDirect, data.endpoints[0].mode);
        Assert.Equal(2, data.endpoints[0].colors.Count);
        Assert.Equal(255, data.endpoints[0].colors[0]);
        Assert.Equal(0, data.endpoints[0].colors[1]);

        var (err, repacked) = IntermediateBlock.Pack(data);
        Assert.Null(err);
        Assert.Equal(orig, repacked);
    }

    [Fact]
    public void TestEndpointRange()
    {
        var blk = PhysicalBlock.Create((UInt128)0x0000000001FE000173UL);
        Assert.NotNull(blk.GetColorValuesRange());
        Assert.Equal(255, blk.GetColorValuesRange().Value);

        var b = IntermediateBlock.UnpackIntermediateBlock(blk);
        Assert.NotNull(b);
        var data = b!;
        Assert.Single(data.endpoints);
        Assert.Equal(ColorEndpointMode.LdrLumaDirect, data.endpoints[0].mode);
        Assert.Equal(new List<int> { 0, 255 }, data.endpoints[0].colors);
        Assert.NotNull(data.endpointRange);
        Assert.Equal(255, data.endpointRange.Value);
    }
    // The comprehensive pack/unpack test that iterates over ASTC testdata.
    // This test port mirrors the reference C++ test and may be slower; it is
    // kept to ensure broad parity with the reference dataset.
    [Theory]
    [InlineData("checkered_4", 4)]
    [InlineData("checkered_5", 5)]
    [InlineData("checkered_6", 6)]
    [InlineData("checkered_7", 7)]
    [InlineData("checkered_8", 8)]
    [InlineData("checkered_9", 9)]
    [InlineData("checkered_10", 10)]
    [InlineData("checkered_11", 11)]
    [InlineData("checkered_12", 12)]
    public void TestPackUnpack(string image_name, int checkered_dim)
    {
        const int astc_dim = 8;
        int img_dim = checkered_dim * astc_dim;
        var astc = LoadASTCFile(image_name);
        int numBlocks = (img_dim / astc_dim) * (img_dim / astc_dim);
        Assert.Equal(0, astc.Length % PhysicalBlock.SizeInBytes);
        for (int i = 0; i < numBlocks; ++i)
        {
            var slice = new ReadOnlySpan<byte>(astc, i * PhysicalBlock.SizeInBytes, PhysicalBlock.SizeInBytes);
            var block_bits = new UInt128(BitConverter.ToUInt64(slice.Slice(8, 8)), BitConverter.ToUInt64(slice.Slice(0, 8)));
            var block = PhysicalBlock.Create(block_bits);
            UInt128 repacked;
            string? err;
            if (block.IsVoidExtent)
            {
                var vb = IntermediateBlock.UnpackVoidExtent(block);
                Assert.NotNull(vb);
                (err, repacked) = IntermediateBlock.Pack(vb!.Value);
                Assert.Null(err);
            }
            else
            {
                var ib = IntermediateBlock.UnpackIntermediateBlock(block);
                Assert.NotNull(ib);
                var block_data = ib!;

                // make sure endpointRange was set to ColorValuesRange
                Assert.Equal(block.GetColorValuesRange(), block_data.endpointRange);

                block_data.endpointRange = null;
                (err, repacked) = IntermediateBlock.Pack(block_data);
                Assert.Null(err);
            }

            var pb = PhysicalBlock.Create(repacked);
            Assert.False(pb.IsIllegalEncoding);

            var pb_num_color_bits = pb.GetColorBitCount().Value;
            var pb_color_mask = UInt128Extensions.OnesMask(pb_num_color_bits);
            var pb_color_bits = (pb.BlockBits >> pb.GetColorStartBit().Value) & pb_color_mask;

            var b_num_color_bits = block.GetColorBitCount().Value;
            var b_color_mask = UInt128Extensions.OnesMask(b_num_color_bits);
            var b_color_bits = (block.BlockBits >> block.GetColorStartBit().Value) & b_color_mask;

            Assert.Equal(pb_color_mask, b_color_mask);
            Assert.Equal(pb_color_bits, b_color_bits);

            Assert.Equal(pb.IsVoidExtent, block.IsVoidExtent);
            Assert.Equal(pb.GetVoidExtentCoordinates(), block.GetVoidExtentCoordinates());

            Assert.Equal(pb.GetWeightGridDimensions(), block.GetWeightGridDimensions());
            Assert.Equal(pb.GetWeightRange(), block.GetWeightRange());
            Assert.Equal(pb.GetWeightBitCount(), block.GetWeightBitCount());
            Assert.Equal(pb.GetWeightStartBit(), block.GetWeightStartBit());

            Assert.Equal(pb.IsDualPlane, block.IsDualPlane);
            Assert.Equal(pb.GetDualPlaneChannel(), block.GetDualPlaneChannel());

            Assert.Equal(pb.GetPartitionsCount(), block.GetPartitionsCount());
            Assert.Equal(pb.GetPartitionId(), block.GetPartitionId());

            Assert.Equal(pb.GetColorValuesCount(), block.GetColorValuesCount());
            Assert.Equal(pb.GetColorValuesRange(), block.GetColorValuesRange());

            var numParts = pb.GetPartitionsCount().GetValueOrDefault(0);
            for (int j = 0; j < numParts; ++j)
            {
                Assert.Equal(pb.GetEndpointMode(j), block.GetEndpointMode(j));
            }
        }
    }
}
