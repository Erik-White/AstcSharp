using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using AstcSharp.Reference;

namespace AstcSharp.Tests
{
    public class CodecTests
    {
        [Fact]
        public void InvalidInput()
        {
            const int valid_width = 16;
            const int valid_height = 16;
            const int valid_stride = valid_width * 4;

            var data = new byte[256];
            var output = new byte[valid_width * valid_height * 4];

            // Invalid footprint
            Assert.False(Codec.ASTCDecompressToRGBA(data, valid_width, valid_height, FootprintType.kCount, output, valid_stride));

            // Fail for 0 width or height
            Assert.False(Codec.ASTCDecompressToRGBA(data, 0, valid_height, FootprintType.k4x4, output, valid_stride));
            Assert.False(Codec.ASTCDecompressToRGBA(data, valid_width, 0, FootprintType.k4x4, output, valid_stride));

            // Fail for data size that's not a multiple of block size
            Assert.False(Codec.ASTCDecompressToRGBA(data.AsSpan(0, data.Length - 1).ToArray(), valid_width, valid_height, FootprintType.k4x4, output, valid_stride));
            // Fail for data size that doesn't match block count
            Assert.False(Codec.ASTCDecompressToRGBA(data.AsSpan(0, data.Length - PhysicalAstcBlock.kSizeInBytes).ToArray(), valid_width, valid_height, FootprintType.k4x4, output, valid_stride));

            // Fail for invalid stride
            Assert.False(Codec.ASTCDecompressToRGBA(data, valid_width, valid_height, FootprintType.k4x4, output, valid_stride - 1));

            // Fail for invalid output size
            Assert.False(Codec.ASTCDecompressToRGBA(data, valid_width, valid_height, FootprintType.k4x4, output.AsSpan(0, output.Length - 1).ToArray(), valid_stride));
        }

        private static (string image_name, FootprintType footprint, int width, int height)[] GetTransparentImageTestParams()
            => new[] {
                ("atlas_small_4x4", FootprintType.k4x4, 256, 256),
                ("atlas_small_5x5", FootprintType.k5x5, 256, 256),
                ("atlas_small_6x6", FootprintType.k6x6, 256, 256),
                ("atlas_small_8x8", FootprintType.k8x8, 256, 256),
            };

        [Theory]
        [MemberData(nameof(PublicApiParams))]
        public void PublicAPI(string image_name, FootprintType footprint, int width, int height)
        {
            var astc = FileBasedHelpers.LoadASTCFile(image_name);

            var maybeFp = Footprint.FromFootprintType(footprint);
            Assert.NotNull(maybeFp);
            var fp = maybeFp.Value;
            int block_width = fp.Width();
            int block_height = fp.Height();
            int blocks_wide = (width + block_width - 1) / block_width;
            int blocks_high = (height + block_height - 1) / block_height;
            int expected_block_count = blocks_wide * blocks_high;

            // diagnostics removed

            Assert.True(astc.Length % PhysicalAstcBlock.kSizeInBytes == 0, "astc byte length not multiple of block size");
            Assert.True(astc.Length / PhysicalAstcBlock.kSizeInBytes == expected_block_count, $"ASTC block count mismatch: {astc.Length / PhysicalAstcBlock.kSizeInBytes} != {expected_block_count}");

            var our_decoded = new ImageBuffer();
            our_decoded.Allocate(width, height, 4);

            // Diagnostic: check per-block unpacking to find failing block
            for (int i = 0; i < astc.Length; i += PhysicalAstcBlock.kSizeInBytes)
            {
                int block_index = i / PhysicalAstcBlock.kSizeInBytes;
                var blkSpan = astc.AsSpan(i, PhysicalAstcBlock.kSizeInBytes).ToArray();
                var pb = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blkSpan, 0), BitConverter.ToUInt64(blkSpan, 8)));
                var lb = LogicalAstcBlock.UnpackLogicalBlock(fp, pb);
                if (lb == null)
                {
                    // diagnostic removed
                    var pb_alt = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blkSpan, 8), BitConverter.ToUInt64(blkSpan, 0)));
                    var lb_alt = LogicalAstcBlock.UnpackLogicalBlock(fp, pb_alt);
                    Assert.True(lb_alt != null, "Block failed to unpack in both canonical and alternate byte orders");
                }
                Assert.NotNull(lb);
            }

            Assert.True(Codec.ASTCDecompressToRGBA(astc, width, height, footprint, our_decoded.Data(), our_decoded.Stride()));

            var decoded_image = FileBasedHelpers.LoadGoldenImageWithAlpha(image_name);
            ImageUtils.CompareSumOfSquaredDifferences(decoded_image, our_decoded, 0.1);
        }

        public static IEnumerable<object[]> PublicApiParams()
        {
            foreach (var p in GetTransparentImageTestParams())
                yield return new object[] { p.image_name, p.footprint, p.width, p.height };
        }

        [Theory]
        [MemberData(nameof(PublicApiParams))]
        public void DecompressToImageTest(string image_name, FootprintType footprint, int width, int height)
        {
            var astcBytes = File.ReadAllBytes(Path.Combine("AstcSharp.Reference", "astc-codec", "src", "decoder", "testdata", image_name + ".astc"));

            var file = AstcFile.LoadFromMemory(astcBytes, out var err);
            Assert.Null(err);
            Assert.NotNull(file);
            Assert.True(file.GetFootprint().HasValue);
            Assert.Equal(footprint, file.GetFootprint().Value.Type());
            // Ensure the header matches the expected dimensions from the test data
            Assert.Equal(width, file.GetWidth());
            Assert.Equal(height, file.GetHeight());

            var our_decoded = new ImageBuffer();
            our_decoded.Allocate(file.GetWidth(), file.GetHeight(), 4);

            Assert.True(Codec.DecompressToImage(file, our_decoded.Data(), our_decoded.DataSize(), our_decoded.Stride()));

            var decoded_image = FileBasedHelpers.LoadGoldenImageWithAlpha(image_name);
            ImageUtils.CompareSumOfSquaredDifferences(decoded_image, our_decoded, 0.1);
        }
    }
}
