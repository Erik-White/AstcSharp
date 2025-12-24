using Xunit;
using AstcSharp;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace AstcSharp.Tests
{
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
            var kErrorBlock = new PhysicalAstcBlock(new UInt128Ex(0UL, 0UL));
            Assert.Null(IntermediateAstcBlock.UnpackVoidExtent(kErrorBlock));
            Assert.Null(IntermediateAstcBlock.UnpackIntermediateBlock(kErrorBlock));
        }

        [Fact]
        public void TestEndpointRangeErrorOnNotSettingWeights()
        {
            var data = new IntermediateAstcBlock.IntermediateBlockData();
            data.weightRange = 15;
            // endpoints empty -> nothing to set; mimic C++ loop no-op
            data.weightGridX = 6;
            data.weightGridY = 6;
            Assert.Equal(-1, IntermediateAstcBlock.EndpointRangeForBlock(data));

            var err = IntermediateAstcBlock.Pack(data, out var dummy);
            Assert.NotNull(err);
            Assert.Contains("Incorrect number of weights", err);
        }

        [Fact]
        public void TestEndpointRangeErrorOnNotEnoughBits()
        {
            var data = new IntermediateAstcBlock.IntermediateBlockData();
            data.weightRange = 1;
            data.partitionId = 0;
            data.endpoints = new List<IntermediateAstcBlock.IntermediateEndpointData>();
            data.endpoints.Add(new IntermediateAstcBlock.IntermediateEndpointData { mode = ColorEndpointMode.kLdrRgbDirect });
            data.endpoints.Add(new IntermediateAstcBlock.IntermediateEndpointData { mode = ColorEndpointMode.kLdrRgbDirect });
            data.endpoints.Add(new IntermediateAstcBlock.IntermediateEndpointData { mode = ColorEndpointMode.kLdrRgbDirect });

            data.weightGridX = 8;
            data.weightGridY = 8;
            Assert.Equal(-2, IntermediateAstcBlock.EndpointRangeForBlock(data));

            // Resize weights to match grid
            data.weights = Enumerable.Repeat(0, 64).ToList();
            var err = IntermediateAstcBlock.Pack(data, out var dummy);
            Assert.NotNull(err);
            Assert.Contains("illegal color range", err);
        }

        [Fact]
        public void TestEndpointRangeForBlock()
        {
            var data = new IntermediateAstcBlock.IntermediateBlockData();
            data.weightRange = 2;
            data.endpoints = new List<IntermediateAstcBlock.IntermediateEndpointData> { new IntermediateAstcBlock.IntermediateEndpointData(), new IntermediateAstcBlock.IntermediateEndpointData() };
            data.dualPlaneChannel = null;
            foreach (var ep in data.endpoints) ep.mode = ColorEndpointMode.kLdrRgbDirect;

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
                int color_range = IntermediateAstcBlock.EndpointRangeForBlock(data);
                Assert.True(color_range <= last_color_range);
                last_color_range = Math.Min(color_range, last_color_range);
            }
            Assert.True(last_color_range < 255);
        }

        [Fact]
        public void TestUnpackNonVoidExtentBlock()
        {
            var blk = new PhysicalAstcBlock(new UInt128Ex(0x0000000001FE000173UL));
            var b = IntermediateAstcBlock.UnpackIntermediateBlock(blk);
            Assert.NotNull(b);
            var data = b!;
            Assert.Equal(6, data.weightGridX);
            Assert.Equal(5, data.weightGridY);
            Assert.Equal(7, data.weightRange);
            Assert.Null(data.partitionId);
            Assert.Null(data.dualPlaneChannel);
            Assert.Equal(30, data.weights.Count);
            foreach (var w in data.weights) Assert.Equal(0, w);
            Assert.Single(data.endpoints);
            var ep = data.endpoints[0];
            Assert.Equal(ColorEndpointMode.kLdrLumaDirect, ep.mode);
            Assert.Equal(2, ep.colors.Count);
            Assert.Equal(0, ep.colors[0]);
            Assert.Equal(255, ep.colors[1]);
        }

        [Fact]
        public void TestPackNonVoidExtentBlock()
        {
            var data = new IntermediateAstcBlock.IntermediateBlockData();
            data.weightGridX = 6;
            data.weightGridY = 5;
            data.weightRange = 7;
            data.partitionId = null;
            data.dualPlaneChannel = null;
            data.weights = Enumerable.Repeat(0, 30).ToList();
            var ep = new IntermediateAstcBlock.IntermediateEndpointData { mode = ColorEndpointMode.kLdrLumaDirect };
            ep.colors.Add(0); ep.colors.Add(255);
            data.endpoints.Add(ep);

            var err = IntermediateAstcBlock.Pack(data, out var packed);
            Assert.Null(err);
            Assert.Equal(new UInt128Ex(0x0000000001FE000173UL), packed);
        }

        [Fact]
        public void TestUnpackVoidExtentBlock()
        {
            var void_blk = new PhysicalAstcBlock(new UInt128Ex(0xFFFFFFFFFFFFFDFCUL));
            var b = IntermediateAstcBlock.UnpackVoidExtent(void_blk);
            Assert.NotNull(b);
            var data = b.Value;
            Assert.Equal((ushort)0, data.r);
            Assert.Equal((ushort)0, data.g);
            Assert.Equal((ushort)0, data.b);
            Assert.Equal((ushort)0, data.a);
            foreach (var c in data.coords) Assert.Equal((1 << 13) - 1, c);

            var more_interesting = new UInt128Ex(0xFFF8003FFE000DFCUL, 0xdeadbeefdeadbeefUL);
            b = IntermediateAstcBlock.UnpackVoidExtent(new PhysicalAstcBlock(more_interesting));
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
            var data = new IntermediateAstcBlock.VoidExtentData();
            data.r = 0; data.g = 0; data.b = 0; data.a = 0;
            data.coords = new ushort[4];
            for (int i = 0; i < 4; ++i) data.coords[i] = (ushort)((1 << 13) - 1);

            var err = IntermediateAstcBlock.Pack(data, out var packed);
            Assert.Null(err);
            Assert.Equal(new UInt128Ex(0xFFFFFFFFFFFFFDFCUL), packed);

            data.r = 0xbeef; data.g = 0xdead; data.b = 0xbeef; data.a = 0xdead;
            data.coords = new ushort[4] { 0, 8191, 0, 8191 };
            err = IntermediateAstcBlock.Pack(data, out packed);
            Assert.Null(err);
            Assert.Equal(new UInt128Ex(0xFFF8003FFE000DFCUL, 0xdeadbeefdeadbeefUL), packed);
        }

        [Fact]
        public void TestPackUnpackWithSameCEM()
        {
            var orig = new UInt128Ex(0x20000200cb73f045UL, 0xe8e8eaea20000980UL);
            var b = IntermediateAstcBlock.UnpackIntermediateBlock(new PhysicalAstcBlock(orig));
            Assert.NotNull(b);
            var err = IntermediateAstcBlock.Pack(b!, out var repacked);
            Assert.Null(err);
            Assert.Equal(orig, repacked);

            orig = new UInt128Ex(0x0573907b8c0f6879UL, 0x3300c30700cb01c5UL);
            b = IntermediateAstcBlock.UnpackIntermediateBlock(new PhysicalAstcBlock(orig));
            Assert.NotNull(b);
            err = IntermediateAstcBlock.Pack(b!, out repacked);
            Assert.Null(err);
            Assert.Equal(orig, repacked);
        }

        [Fact]
        public void TestPackingWithLargeGap()
        {
            var orig = new UInt128Ex(0x0000000001FE032EUL, 0xBEDEAD0000000000UL);
            var b = IntermediateAstcBlock.UnpackIntermediateBlock(new PhysicalAstcBlock(orig));
            Assert.NotNull(b);
            var data = b!;
            Assert.Equal(2, data.weightGridX);
            Assert.Equal(3, data.weightGridY);
            Assert.Equal(15, data.weightRange);
            Assert.Null(data.partitionId);
            Assert.Null(data.dualPlaneChannel);
            Assert.Single(data.endpoints);
            Assert.Equal(ColorEndpointMode.kLdrLumaDirect, data.endpoints[0].mode);
            Assert.Equal(2, data.endpoints[0].colors.Count);
            Assert.Equal(255, data.endpoints[0].colors[0]);
            Assert.Equal(0, data.endpoints[0].colors[1]);

            var err = IntermediateAstcBlock.Pack(data, out var repacked);
            Assert.Null(err);
            Assert.Equal(orig, repacked);
        }

        [Fact]
        public void TestEndpointRange()
        {
            var blk = new PhysicalAstcBlock(new UInt128Ex(0x0000000001FE000173UL));
            Assert.NotNull(blk.ColorValuesRange());
            Assert.Equal(255, blk.ColorValuesRange().Value);

            var b = IntermediateAstcBlock.UnpackIntermediateBlock(blk);
            Assert.NotNull(b);
            var data = b!;
            Assert.Single(data.endpoints);
            Assert.Equal(ColorEndpointMode.kLdrLumaDirect, data.endpoints[0].mode);
            Assert.Equal(new List<int> { 0, 255 }, data.endpoints[0].colors);
            Assert.NotNull(data.endpoint_range);
            Assert.Equal(255, data.endpoint_range.Value);
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
            Assert.Equal(0, astc.Length % PhysicalAstcBlock.kSizeInBytes);
            for (int i = 0; i < numBlocks; ++i)
            {
                var slice = new ReadOnlySpan<byte>(astc, i * PhysicalAstcBlock.kSizeInBytes, PhysicalAstcBlock.kSizeInBytes);
                var block_bits = UInt128Ex.FromBytes(slice);
                var block = new PhysicalAstcBlock(block_bits);
                UInt128Ex repacked;
                if (block.IsVoidExtent())
                {
                    var vb = IntermediateAstcBlock.UnpackVoidExtent(block);
                    Assert.NotNull(vb);
                    var err = IntermediateAstcBlock.Pack(vb!.Value, out repacked);
                    Assert.Null(err);
                }
                else
                {
                    var ib = IntermediateAstcBlock.UnpackIntermediateBlock(block);
                    Assert.NotNull(ib);
                    var block_data = ib!;

                    // make sure endpoint_range was set to ColorValuesRange
                    Assert.Equal(block.ColorValuesRange(), block_data.endpoint_range);

                    block_data.endpoint_range = null;
                    var err = IntermediateAstcBlock.Pack(block_data, out repacked);
                    Assert.Null(err);
                }

                var pb = new PhysicalAstcBlock(repacked);
                Assert.Null(pb.IsIllegalEncoding());

                var pb_num_color_bits = pb.ColorBitCount().Value;
                var pb_color_mask = UInt128Ex.OnesMask(pb_num_color_bits);
                var pb_color_bits = (pb.GetBlockBits() >> pb.ColorStartBit().Value) & pb_color_mask;

                var b_num_color_bits = block.ColorBitCount().Value;
                var b_color_mask = UInt128Ex.OnesMask(b_num_color_bits);
                var b_color_bits = (block.GetBlockBits() >> block.ColorStartBit().Value) & b_color_mask;

                Assert.Equal(pb_color_mask, b_color_mask);
                Assert.Equal(pb_color_bits, b_color_bits);

                Assert.Equal(pb.IsVoidExtent(), block.IsVoidExtent());
                Assert.Equal(pb.VoidExtentCoords(), block.VoidExtentCoords());

                Assert.Equal(pb.WeightGridDimensions(), block.WeightGridDimensions());
                Assert.Equal(pb.WeightRange(), block.WeightRange());
                Assert.Equal(pb.WeightBitCount(), block.WeightBitCount());
                Assert.Equal(pb.WeightStartBit(), block.WeightStartBit());

                Assert.Equal(pb.IsDualPlane(), block.IsDualPlane());
                Assert.Equal(pb.DualPlaneChannel(), block.DualPlaneChannel());

                Assert.Equal(pb.PartitionsCount(), block.PartitionsCount());
                Assert.Equal(pb.PartitionId(), block.PartitionId());

                Assert.Equal(pb.ColorValuesCount(), block.ColorValuesCount());
                Assert.Equal(pb.ColorValuesRange(), block.ColorValuesRange());

                var numParts = pb.PartitionsCount().GetValueOrDefault(0);
                for (int j = 0; j < numParts; ++j)
                {
                    Assert.Equal(pb.GetEndpointMode(j), block.GetEndpointMode(j));
                }
            }
        }
    }
}
