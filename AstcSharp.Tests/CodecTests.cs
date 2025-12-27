using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using AstcSharp;

namespace AstcSharp.Tests
{
    public class CodecTests
    {
        [Fact]
        public void InvalidInput()
        {
            const int valid_width = 16;
            const int valid_height = 16;

            var data = new byte[256];
            var output = new byte[valid_width * valid_height * 4];

            // Invalid footprint
            Assert.Empty(Codec.ASTCDecompressToRGBA(data, valid_width, valid_height, FootprintType.kCount).ToArray());

            // Fail for 0 width or height
            Assert.Empty(Codec.ASTCDecompressToRGBA(data, 0, valid_height, FootprintType.k4x4).ToArray());
            Assert.Empty(Codec.ASTCDecompressToRGBA(data, valid_width, 0, FootprintType.k4x4).ToArray());

            // Fail for data size that's not a multiple of block size
            Assert.Empty(Codec.ASTCDecompressToRGBA(data.AsSpan(0, data.Length - 1).ToArray(), valid_width, valid_height, FootprintType.k4x4).ToArray());
            
            // Fail for data size that doesn't match block count
            Assert.Empty(Codec.ASTCDecompressToRGBA(data.AsSpan(0, data.Length - PhysicalAstcBlock.kSizeInBytes).ToArray(), valid_width, valid_height, FootprintType.k4x4).ToArray());
        }

        private static (string image_name, FootprintType footprint, int width, int height)[] GetTransparentImageTestParams()
            =>
                [
                    ("atlas_small_4x4", FootprintType.k4x4, 256, 256),
                    ("atlas_small_5x5", FootprintType.k5x5, 256, 256),
                    ("atlas_small_6x6", FootprintType.k6x6, 256, 256),
                    ("atlas_small_8x8", FootprintType.k8x8, 256, 256),
                ];

        [Theory]
        [MemberData(nameof(PublicApiParams))]
        public void PublicAPI(string imageName, FootprintType footprint, int width, int height)
        {
            var astc = FileBasedHelpers.LoadASTCFile(imageName);

            var maybeFp = Footprint.FromFootprintType(footprint);
            Assert.NotNull(maybeFp);
            var fp = maybeFp.Value;
            int block_width = fp.Width();
            int block_height = fp.Height();
            int blocks_wide = (width + block_width - 1) / block_width;
            int blocks_high = (height + block_height - 1) / block_height;
            int expected_block_count = blocks_wide * blocks_high;

            Assert.True(astc.Length % PhysicalAstcBlock.kSizeInBytes == 0, "astc byte length not multiple of block size");
            Assert.True(astc.Length / PhysicalAstcBlock.kSizeInBytes == expected_block_count, $"ASTC block count mismatch: {astc.Length / PhysicalAstcBlock.kSizeInBytes} != {expected_block_count}");

            var filePath = Path.Combine("TestData", "Expected", imageName + ".bmp");
            var expectedImage = FileBasedHelpers.LoadExpectedImage(filePath);

            // Diagnostic: check per-block unpacking to find failing block
            for (int i = 0; i < astc.Length; i += PhysicalAstcBlock.kSizeInBytes)
            {
                var block = astc.AsSpan(i, PhysicalAstcBlock.kSizeInBytes).ToArray();
                var physicalBlock = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(block, 0), BitConverter.ToUInt64(block, 8)));
                var logicalBlock = LogicalAstcBlock.UnpackLogicalBlock(fp, physicalBlock);
                if (logicalBlock is null)
                {
                    var physicalBlockRetry = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(block, 8), BitConverter.ToUInt64(block, 0)));
                    var logicalBlockRetry = LogicalAstcBlock.UnpackLogicalBlock(fp, physicalBlockRetry);
                    Assert.True(logicalBlockRetry is not null, "Block failed to unpack in both canonical and alternate byte orders");
                }
                Assert.NotNull(logicalBlock);
            }

            var decodedPixels = Codec.ASTCDecompressToRGBA(astc, width, height, footprint);
            var actualImage = new ImageBuffer(decodedPixels.ToArray(), width, height, 4);

            ImageUtils.CompareSumOfSquaredDifferences(expectedImage, actualImage, 0.1);
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
            var astcBytes = File.ReadAllBytes(Path.Combine("TestData", "Input", image_name + ".astc"));
            var file = AstcFile.LoadFromMemory(astcBytes, out var err);
            Assert.Null(err);
            Assert.NotNull(file);
            Assert.True(file.GetFootprint().HasValue);
            Assert.Equal(footprint, file.GetFootprint().Value.Type());
            // Ensure the header matches the expected dimensions from the test data
            Assert.Equal(width, file.GetWidth());
            Assert.Equal(height, file.GetHeight());
            
            var filePath = Path.Combine("TestData", "Expected", image_name + ".bmp");
            var expectedImage = FileBasedHelpers.LoadExpectedImage(filePath);

            var decodedPixels = Codec.DecompressToImage(file);
            var actualImage = new ImageBuffer(decodedPixels.ToArray(), width, height, 4);

            ImageUtils.CompareSumOfSquaredDifferences(expectedImage, actualImage, 0.1);
        }
    }
}
