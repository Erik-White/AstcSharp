using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;
using FluentAssertions;

namespace AstcSharp.Tests;

public class CodecTests
{
    #region Invalid Input Tests

    [Fact]
    public void ASTCDecompressToRGBA_WithZeroWidth_ShouldReturnEmpty()
    {
        // Arrange
        var data = new byte[256];
        const int height = 16;

        // Act
        var result = AstcDecoder.ASTCDecompressToRGBA(data, 0, height, FootprintType.Footprint4x4);

        // Assert
        result.ToArray().Should().BeEmpty();
    }

    [Fact]
    public void ASTCDecompressToRGBA_WithZeroHeight_ShouldReturnEmpty()
    {
        // Arrange
        var data = new byte[256];
        const int width = 16;

        // Act
        var result = AstcDecoder.ASTCDecompressToRGBA(data, width, 0, FootprintType.Footprint4x4);

        // Assert
        result.ToArray().Should().BeEmpty();
    }

    [Fact]
    public void ASTCDecompressToRGBA_WithDataSizeNotMultipleOfBlockSize_ShouldReturnEmpty()
    {
        // Arrange
        var data = new byte[256];
        const int width = 16;
        const int height = 16;
        var invalidData = data.AsSpan(0, data.Length - 1).ToArray();

        // Act
        var result = AstcDecoder.ASTCDecompressToRGBA(invalidData, width, height, FootprintType.Footprint4x4);

        // Assert
        result.ToArray().Should().BeEmpty();
    }

    [Fact]
    public void ASTCDecompressToRGBA_WithMismatchedBlockCount_ShouldReturnEmpty()
    {
        // Arrange
        var data = new byte[256];
        const int width = 16;
        const int height = 16;
        var mismatchedData = data.AsSpan(0, data.Length - PhysicalBlock.SizeInBytes).ToArray();

        // Act
        var result = AstcDecoder.ASTCDecompressToRGBA(mismatchedData, width, height, FootprintType.Footprint4x4);

        // Assert
        result.ToArray().Should().BeEmpty();
    }

    #endregion

    #region Public API Tests

    private static (string ImageName, FootprintType Footprint, int Width, int Height)[] GetTransparentImageTestParams()
        => new[]
        {
            ("atlas_small_4x4", FootprintType.Footprint4x4, 256, 256),
            ("atlas_small_5x5", FootprintType.Footprint5x5, 256, 256),
            ("atlas_small_6x6", FootprintType.Footprint6x6, 256, 256),
            ("atlas_small_8x8", FootprintType.Footprint8x8, 256, 256),
        };

    public static IEnumerable<object[]> PublicApiParams()
    {
        foreach (var p in GetTransparentImageTestParams())
            yield return new object[] { p.ImageName, p.Footprint, p.Width, p.Height };
    }

    [Theory]
    [MemberData(nameof(PublicApiParams))]
    public void ASTCDecompressToRGBA_WithValidData_ShouldMatchExpected(
        string imageName, FootprintType footprintType, int width, int height)
    {
        // Arrange
        var astcData = FileBasedHelpers.LoadASTCFile(imageName);
        var footprint = Footprint.FromFootprintType(footprintType);
        int blockWidth = footprint.Width;
        int blockHeight = footprint.Height;
        int blocksWide = (width + blockWidth - 1) / blockWidth;
        int blocksHigh = (height + blockHeight - 1) / blockHeight;
        int expectedBlockCount = blocksWide * blocksHigh;

        // Assert ASTC data structure
        (astcData.Length % PhysicalBlock.SizeInBytes).Should().Be(0, "astc byte length must be multiple of block size");
        (astcData.Length / PhysicalBlock.SizeInBytes).Should().Be(expectedBlockCount, $"ASTC block count should match expected");

        // Verify all blocks can be unpacked
        for (int i = 0; i < astcData.Length; i += PhysicalBlock.SizeInBytes)
        {
            var block = astcData.AsSpan(i, PhysicalBlock.SizeInBytes).ToArray();
            var physicalBlock = PhysicalBlock.Create(BitConverter.ToUInt64(block, 0), BitConverter.ToUInt64(block, 8));
            var logicalBlock = LogicalBlock.UnpackLogicalBlock(footprint, physicalBlock);

            logicalBlock.Should().NotBeNull("all blocks should unpack successfully");
        }

        // Act
        var decodedPixels = AstcDecoder.ASTCDecompressToRGBA(astcData, width, height, footprintType);
        var actualImage = new ImageBuffer(decodedPixels.ToArray(), width, height, 4);

        // Assert
        var expectedImagePath = Path.Combine("TestData", "Expected", imageName + ".bmp");
        var expectedImage = FileBasedHelpers.LoadExpectedImage(expectedImagePath);
        ImageUtils.CompareSumOfSquaredDifferences(expectedImage, actualImage, 0.1);
    }

    [Theory]
    [MemberData(nameof(PublicApiParams))]
    public void DecompressToImage_WithAstcFile_ShouldMatchExpected(
        string imageName, FootprintType footprint, int width, int height)
    {
        // Arrange
        var astcPath = Path.Combine("TestData", "Input", imageName + ".astc");
        var astcBytes = File.ReadAllBytes(astcPath);
        var file = AstcFile.FromMemory(astcBytes);

        // Assert file header
        file.Footprint.Type.Should().Be(footprint);
        file.Width.Should().Be(width);
        file.Height.Should().Be(height);

        // Act
        var decodedPixels = AstcDecoder.DecompressToImage(file);
        var actualImage = new ImageBuffer(decodedPixels.ToArray(), width, height, 4);

        // Assert
        var expectedImagePath = Path.Combine("TestData", "Expected", imageName + ".bmp");
        var expectedImage = FileBasedHelpers.LoadExpectedImage(expectedImagePath);
        ImageUtils.CompareSumOfSquaredDifferences(expectedImage, actualImage, 0.1);
    }

    #endregion
}
