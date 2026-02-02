using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.TexelBlock;
using FluentAssertions;

namespace AstcSharp.Tests;

public class LogicalAstcBlockTests
{
    #region Constructor and Basic Property Tests

    [Fact]
    public void Constructor_WithFootprint_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Assert
        logicalBlock.Should().NotBeNull();
        logicalBlock.GetFootprint().Should().Be(Footprint.Get4x4());
        logicalBlock.IsDualPlane().Should().BeFalse();
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(8, 8)]
    [InlineData(10, 10)]
    [InlineData(12, 12)]
    public void Constructor_WithVariousFootprints_ShouldMatchFootprint(int width, int height)
    {
        // Arrange
        var footprint = width switch
        {
            4 => Footprint.Get4x4(),
            5 => Footprint.Get5x5(),
            8 => Footprint.Get8x8(),
            10 => Footprint.Get10x10(),
            12 => Footprint.Get12x12(),
            _ => throw new ArgumentException("Invalid footprint size")
        };

        // Act
        var logicalBlock = new LogicalBlock(footprint);

        // Assert
        logicalBlock.GetFootprint().Should().Be(footprint);
        logicalBlock.GetFootprint().Width.Should().Be(width);
        logicalBlock.GetFootprint().Height.Should().Be(height);
    }

    [Fact]
    public void GetFootprint_AfterConstruction_ShouldReturnOriginalFootprint()
    {
        // Arrange
        var footprint = Footprint.Get8x8();
        var logicalBlock = new LogicalBlock(footprint);

        // Act
        var result = logicalBlock.GetFootprint();

        // Assert
        result.Should().Be(footprint);
    }

    #endregion

    #region Weight Tests

    [Fact]
    public void SetWeightAt_WithValidWeight_ShouldStoreCorrectly()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        logicalBlock.SetWeightAt(2, 3, 42);

        // Assert
        logicalBlock.WeightAt(2, 3).Should().Be(42);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(64)]
    public void SetWeightAt_WithVariousValidWeights_ShouldStoreCorrectly(int weight)
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        logicalBlock.SetWeightAt(1, 1, weight);

        // Assert
        logicalBlock.WeightAt(1, 1).Should().Be(weight);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(100)]
    public void SetWeightAt_WithInvalidWeight_ShouldThrowArgumentOutOfRangeException(int weight)
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        var action = () => logicalBlock.SetWeightAt(0, 0, weight);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WeightAt_WithDefaultWeights_ShouldReturnZero()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        var weight = logicalBlock.WeightAt(2, 2);

        // Assert
        weight.Should().Be(0);
    }

    #endregion

    #region Dual Plane Tests

    [Fact]
    public void IsDualPlane_ByDefault_ShouldBeFalse()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        var result = logicalBlock.IsDualPlane();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void SetDualPlaneChannel_WithValidChannel_ShouldEnableDualPlane()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        logicalBlock.SetDualPlaneChannel(0);

        // Assert
        logicalBlock.IsDualPlane().Should().BeTrue();
    }

    [Fact]
    public void SetDualPlaneChannel_WithNegativeValue_ShouldDisableDualPlane()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        logicalBlock.SetDualPlaneChannel(0);

        // Act
        logicalBlock.SetDualPlaneChannel(-1);

        // Assert
        logicalBlock.IsDualPlane().Should().BeFalse();
    }

    [Fact]
    public void SetDualPlaneWeightAt_WhenNotDualPlane_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        var action = () => logicalBlock.SetDualPlaneWeightAt(0, 2, 3, 1);

        // Assert
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("Not a dual plane block");
    }

    [Fact]
    public void SetDualPlaneWeightAt_AfterEnablingDualPlane_ShouldPreserveOriginalWeight()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        logicalBlock.SetWeightAt(2, 3, 2);
        logicalBlock.SetDualPlaneChannel(0);

        // Act
        logicalBlock.SetDualPlaneWeightAt(0, 2, 3, 1);

        // Assert
        logicalBlock.WeightAt(2, 3).Should().Be(2);
        logicalBlock.DualPlaneWeightAt(0, 2, 3).Should().Be(1);
    }

    [Fact]
    public void DualPlaneWeightAt_ForNonDualPlaneChannel_ShouldReturnOriginalWeight()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        logicalBlock.SetWeightAt(2, 3, 2);
        logicalBlock.SetDualPlaneChannel(0);
        logicalBlock.SetDualPlaneWeightAt(0, 2, 3, 1);

        // Act & Assert
        for (int i = 1; i < 4; ++i)
        {
            logicalBlock.DualPlaneWeightAt(i, 2, 3).Should().Be(2);
        }
    }

    [Fact]
    public void DualPlaneWeightAt_WhenNotDualPlane_ShouldReturnWeightAt()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        logicalBlock.SetWeightAt(2, 3, 42);

        // Act
        var result = logicalBlock.DualPlaneWeightAt(0, 2, 3);

        // Assert
        result.Should().Be(42);
    }

    [Fact]
    public void SetDualPlaneWeightAt_ThenDisableDualPlane_ShouldResetToOriginalWeight()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        logicalBlock.SetWeightAt(2, 3, 2);
        logicalBlock.SetDualPlaneChannel(0);
        logicalBlock.SetDualPlaneWeightAt(0, 2, 3, 1);

        // Act
        logicalBlock.SetDualPlaneChannel(-1);

        // Assert
        logicalBlock.IsDualPlane().Should().BeFalse();
        logicalBlock.WeightAt(2, 3).Should().Be(2);
        for (int i = 0; i < 4; ++i)
        {
            logicalBlock.DualPlaneWeightAt(i, 2, 3).Should().Be(2);
        }
    }

    #endregion

    #region Endpoint and Color Tests

    [Fact]
    public void SetEndpoints_WithValidColors_ShouldStoreCorrectly()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        var color1 = new RgbaColor(255, 0, 0, 255);
        var color2 = new RgbaColor(0, 255, 0, 255);

        // Act
        logicalBlock.SetEndpoints(color1, color2, 0);

        // No direct getter, but we can verify through ColorAt
        logicalBlock.SetWeightAt(0, 0, 0);
        logicalBlock.SetWeightAt(1, 1, 64);

        // Assert
        var colorAtMinWeight = logicalBlock.ColorAt(0, 0);
        var colorAtMaxWeight = logicalBlock.ColorAt(1, 1);

        colorAtMinWeight.R.Should().Be(color1.R);
        colorAtMaxWeight.R.Should().BeCloseTo(color2.R, 1);
    }

    [Fact]
    public void ColorAt_WithCheckerboardWeights_ShouldInterpolateCorrectly()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get8x8());

        // Create checkerboard weight pattern
        for (int j = 0; j < 8; ++j)
        {
            for (int i = 0; i < 8; ++i)
            {
                if (((i ^ j) & 1) == 1)
                    logicalBlock.SetWeightAt(i, j, 0);
                else
                    logicalBlock.SetWeightAt(i, j, 64);
            }
        }

        var endpointA = new RgbaColor(123, 45, 67, 89);
        var endpointB = new RgbaColor(101, 121, 31, 41);
        logicalBlock.SetEndpoints(endpointA, endpointB, 0);

        // Act & Assert - verify checkerboard pattern
        for (int j = 0; j < 8; ++j)
        {
            for (int i = 0; i < 8; ++i)
            {
                var color = logicalBlock.ColorAt(i, j);
                if (((i ^ j) & 1) == 1)
                {
                    // Weight 0 = first endpoint
                    color.R.Should().Be(endpointA.R);
                    color.G.Should().Be(endpointA.G);
                    color.B.Should().Be(endpointA.B);
                    color.A.Should().Be(endpointA.A);
                }
                else
                {
                    // Weight 64 = second endpoint
                    color.R.Should().Be(endpointB.R);
                    color.G.Should().Be(endpointB.G);
                    color.B.Should().Be(endpointB.B);
                    color.A.Should().Be(endpointB.A);
                }
            }
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(4, 0)]
    [InlineData(0, 4)]
    public void ColorAt_WithOutOfBoundsCoordinates_ShouldThrowArgumentOutOfRangeException(int x, int y)
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());

        // Act
        var action = () => logicalBlock.ColorAt(x, y);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Partition Tests

    [Fact]
    public void SetPartition_WithValidPartition_ShouldUpdateCorrectly()
    {
        // Arrange
        var footprint = Footprint.Get8x8();
        var logicalBlock = new LogicalBlock(footprint);
        var newPartition = new Partition(footprint, 2, 5)
        {
            assignment = Enumerable.Repeat(0, footprint.PixelCount).ToList()
        };

        // Act
        logicalBlock.SetPartition(newPartition);

        // Assert - verify by setting endpoints for both partitions
        logicalBlock.SetEndpoints(new RgbaColor(255, 0, 0, 255), new RgbaColor(0, 0, 0, 255), 0);
        logicalBlock.SetEndpoints(new RgbaColor(0, 255, 0, 255), new RgbaColor(0, 0, 0, 255), 1);
    }

    [Fact]
    public void SetPartition_WithDifferentFootprint_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        var wrongPartition = new Partition(Footprint.Get8x8(), 1, 0)
        {
            assignment = Enumerable.Repeat(0, 64).ToList()
        };

        // Act
        var action = () => logicalBlock.SetPartition(wrongPartition);

        // Assert
        action.Should().Throw<InvalidOperationException>()
            .WithMessage("New partitions may not be for a different footprint");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void SetEndpoints_WithInvalidSubset_ShouldThrowArgumentOutOfRangeException(int subset)
    {
        // Arrange
        var logicalBlock = new LogicalBlock(Footprint.Get4x4());
        var color1 = new RgbaColor(255, 0, 0, 255);
        var color2 = new RgbaColor(0, 255, 0, 255);

        // Act
        var action = () => logicalBlock.SetEndpoints(color1, color2, subset);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Unpack Tests

    [Fact]
    public void UnpackLogicalBlock_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var errorBlock = PhysicalBlock.Create(UInt128.Zero);

        // Act
        var result = LogicalBlock.UnpackLogicalBlock(Footprint.Get8x8(), errorBlock);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void UnpackLogicalBlock_WithVoidExtentBlock_ShouldReturnLogicalBlock()
    {
        // Arrange
        var voidExtentBlock = PhysicalBlock.Create((UInt128)0xFFFFFFFFFFFFFDFCUL);

        // Act
        var result = LogicalBlock.UnpackLogicalBlock(Footprint.Get8x8(), voidExtentBlock);

        // Assert
        result.Should().NotBeNull();
        result!.GetFootprint().Should().Be(Footprint.Get8x8());
    }

    [Fact]
    public void UnpackLogicalBlock_WithStandardBlock_ShouldReturnLogicalBlock()
    {
        // Arrange
        var standardBlock = PhysicalBlock.Create((UInt128)0x0000000001FE000173UL);

        // Act
        var result = LogicalBlock.UnpackLogicalBlock(Footprint.Get6x5(), standardBlock);

        // Assert
        result.Should().NotBeNull();
        result!.GetFootprint().Should().Be(Footprint.Get6x5());
    }

    #endregion

    #region Integration Tests - Synthetic Images

    private static (string imageName, bool hasAlpha, Footprint fp, int width, int height)[] GetSyntheticImageTestParams()
        => new[] {
            ("footprint_4x4", false, Footprint.Get4x4(), 32, 32),
            ("footprint_5x4", false, Footprint.Get5x4(), 32, 32),
            ("footprint_5x5", false, Footprint.Get5x5(), 32, 32),
            ("footprint_6x5", false, Footprint.Get6x5(), 32, 32),
            ("footprint_6x6", false, Footprint.Get6x6(), 32, 32),
            ("footprint_8x5", false, Footprint.Get8x5(), 32, 32),
            ("footprint_8x6", false, Footprint.Get8x6(), 32, 32),
            ("footprint_10x5", false, Footprint.Get10x5(), 32, 32),
            ("footprint_10x6", false, Footprint.Get10x6(), 32, 32),
            ("footprint_8x8", false, Footprint.Get8x8(), 32, 32),
            ("footprint_10x8", false, Footprint.Get10x8(), 32, 32),
            ("footprint_10x10", false, Footprint.Get10x10(), 32, 32),
            ("footprint_12x10", false, Footprint.Get12x10(), 32, 32),
            ("footprint_12x12", false, Footprint.Get12x12(), 32, 32),
        };

    public static IEnumerable<object[]> SyntheticParams()
    {
        foreach (var p in GetSyntheticImageTestParams())
            yield return new object[] { p.imageName, p.hasAlpha, p.fp, p.width, p.height };
    }

    [Theory]
    [MemberData(nameof(SyntheticParams))]
    public void UnpackLogicalBlock_WithSyntheticImage_ShouldDecodeCorrectly(
        string imageName, bool hasAlpha, Footprint fp, int width, int height)
    {
        // Arrange
        var astc = FileBasedHelpers.LoadASTCFile(imageName);
        var decodedImage = ImageBuffer.Allocate(width, height, hasAlpha ? 4 : 3);

        int blockWidth = fp.Width;
        int blockHeight = fp.Height;

        // Act
        for (int i = 0; i < astc.Length; i += PhysicalBlock.SizeInBytes)
        {
            int blockIndex = i / PhysicalBlock.SizeInBytes;
            int blocksWide = (width + blockWidth - 1) / blockWidth;
            int blockX = blockIndex % blocksWide;
            int blockY = blockIndex / blocksWide;

            var blockSpan = astc.AsSpan(i, PhysicalBlock.SizeInBytes).ToArray();
            var physicalBlock = PhysicalBlock.Create(new UInt128(
                BitConverter.ToUInt64(blockSpan, 8),
                BitConverter.ToUInt64(blockSpan, 0)));

            var logicalBlock = LogicalBlock.UnpackLogicalBlock(fp, physicalBlock);
            logicalBlock.Should().NotBeNull();

            for (int y = 0; y < blockHeight; ++y)
            {
                for (int x = 0; x < blockWidth; ++x)
                {
                    int px = blockWidth * blockX + x;
                    int py = blockHeight * blockY + y;
                    if (px >= width || py >= height) continue;

                    var decoded = logicalBlock!.ColorAt(x, y);
                    int row = py * decodedImage.Stride();
                    int off = row + px * decodedImage.BytesPerPixel();
                    decodedImage.Data()[off + 0] = (byte)decoded.R;
                    decodedImage.Data()[off + 1] = (byte)decoded.G;
                    decodedImage.Data()[off + 2] = (byte)decoded.B;
                    if (hasAlpha) decodedImage.Data()[off + 3] = (byte)decoded.A;
                }
            }
        }

        // Assert
        var expectedPath = Path.Combine("TestData", "Expected", imageName + ".bmp");
        var expectedImage = FileBasedHelpers.LoadExpectedImage(expectedPath);
        ImageUtils.CompareSumOfSquaredDifferences(expectedImage, decodedImage, 0.1);
    }

    #endregion

    #region Integration Tests - Real World Images

    private static (string imageName, bool hasAlpha, Footprint fp, int width, int height)[] GetRealWorldImageTestParams()
        => new[] {
            ("rgb_4x4", false, Footprint.Get4x4(), 224, 288),
            ("rgb_6x6", false, Footprint.Get6x6(), 224, 288),
            ("rgb_8x8", false, Footprint.Get8x8(), 224, 288),
            ("rgb_12x12", false, Footprint.Get12x12(), 224, 288),
            ("rgb_5x4", false, Footprint.Get5x4(), 224, 288),
        };

    public static IEnumerable<object[]> RealWorldParams()
    {
        foreach (var p in GetRealWorldImageTestParams())
            yield return new object[] { p.imageName, p.hasAlpha, p.fp, p.width, p.height };
    }

    [Theory]
    [MemberData(nameof(RealWorldParams))]
    public void UnpackLogicalBlock_WithRealWorldImage_ShouldDecodeCorrectly(
        string imageName, bool hasAlpha, Footprint fp, int width, int height)
    {
        // Act & Assert - reuse synthetic test implementation
        UnpackLogicalBlock_WithSyntheticImage_ShouldDecodeCorrectly(imageName, hasAlpha, fp, width, height);
    }

    #endregion

    #region Integration Tests - Transparent Images

    private static (string imageName, bool hasAlpha, Footprint fp, int width, int height)[] GetTransparentImageTestParams()
        => new[] {
            ("atlas_small_4x4", true, Footprint.Get4x4(), 256, 256),
            ("atlas_small_5x5", true, Footprint.Get5x5(), 256, 256),
            ("atlas_small_6x6", true, Footprint.Get6x6(), 256, 256),
            ("atlas_small_8x8", true, Footprint.Get8x8(), 256, 256),
        };

    public static IEnumerable<object[]> TransparentParams()
    {
        foreach (var p in GetTransparentImageTestParams())
            yield return new object[] { p.imageName, p.hasAlpha, p.fp, p.width, p.height };
    }

    [Theory]
    [MemberData(nameof(TransparentParams))]
    public void UnpackLogicalBlock_WithTransparentImage_ShouldDecodeCorrectly(
        string imageName, bool hasAlpha, Footprint fp, int width, int height)
    {
        // Act & Assert - reuse synthetic test implementation
        UnpackLogicalBlock_WithSyntheticImage_ShouldDecodeCorrectly(imageName, hasAlpha, fp, width, height);
    }

    #endregion
}
