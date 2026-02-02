using AstcSharp.Core;
using AstcSharp.ColorEncoding;
using AstcSharp.TexelBlock;
using FluentAssertions;

namespace AstcSharp.Tests;

public class PhysicalAstcBlockTests
{
    private static readonly UInt128 ErrorBlock = UInt128.Zero;

    #region Constructor Tests

    [Fact]
    public void Create_WithUInt64_ShouldRoundTripBlockBits()
    {
        // Arrange
        const ulong expectedLow = 0x0000000001FE000173UL;

        // Act
        var block = PhysicalBlock.Create(expectedLow);

        // Assert
        block.BlockBits.Should().Be((UInt128)expectedLow);
    }

    [Fact]
    public void Create_WithUInt128_ShouldRoundTripBlockBits()
    {
        // Arrange
        var expected = (UInt128)0x12345678ABCDEF00UL | ((UInt128)0xCAFEBABEDEADBEEFUL << 64);

        // Act
        var block = PhysicalBlock.Create(expected);

        // Assert
        block.BlockBits.Should().Be(expected);
    }

    [Fact]
    public void Create_WithMatchingUInt64AndUInt128_ShouldProduceIdenticalBlocks()
    {
        // Arrange
        const ulong value = 0x0000000001FE000173UL;

        // Act
        var block1 = PhysicalBlock.Create(value);
        var block2 = PhysicalBlock.Create((UInt128)value);

        // Assert
        block1.BlockBits.Should().Be(block2.BlockBits);
    }

    #endregion

    #region Void Extent Tests

    [Fact]
    public void IsVoidExtent_WithKnownVoidExtentPattern_ShouldReturnTrue()
    {
        // Arrange
        var block = PhysicalBlock.Create((UInt128)0xFFFFFFFFFFFFFDFCUL);

        // Act & Assert
        block.IsVoidExtent.Should().BeTrue();
    }

    [Fact]
    public void IsVoidExtent_WithStandardBlock_ShouldReturnFalse()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act & Assert
        block.IsVoidExtent.Should().BeFalse();
    }

    [Fact]
    public void IsVoidExtent_WithErrorBlock_ShouldReturnFalse()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act & Assert
        block.IsVoidExtent.Should().BeFalse();
    }

    [Fact]
    public void GetVoidExtentCoordinates_WithValidVoidExtentBlock_ShouldReturnExpectedCoordinates()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var coords = block.GetVoidExtentCoordinates();

        // Assert
        coords.Should().NotBeNull();
        coords.Should().HaveCount(4);
        coords![0].Should().Be(0);
        coords[1].Should().Be(8191);
        coords[2].Should().Be(0);
        coords[3].Should().Be(8191);
    }

    [Fact]
    public void GetVoidExtentCoordinates_WithAllOnesPattern_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFFFFFFFFFFFFDFCUL);

        // Act
        var coords = block.GetVoidExtentCoordinates();

        // Assert
        block.IsVoidExtent.Should().BeTrue();
        coords.Should().BeNull();
    }

    [Fact]
    public void Create_WithInvalidVoidExtentCoordinates_ShouldBeIllegalEncoding()
    {
        // Arrange & Act
        var block1 = PhysicalBlock.Create(0x0008004002001DFCUL);
        var block2 = PhysicalBlock.Create(0x0007FFC001FFFDFCUL);

        // Assert
        block1.IsIllegalEncoding.Should().BeTrue();
        block2.IsIllegalEncoding.Should().BeTrue();
    }

    [Fact]
    public void Create_WithModifiedHighBitsOnVoidExtent_ShouldStillBeValid()
    {
        // Arrange & Act
        var original = PhysicalBlock.Create(0xFFF8003FFE000DFCUL, 0UL);
        var modified = PhysicalBlock.Create(0xFFF8003FFE000DFCUL, 0xdeadbeefdeadbeef);

        // Assert
        original.IsIllegalEncoding.Should().BeFalse();
        original.IsVoidExtent.Should().BeTrue();
        modified.IsIllegalEncoding.Should().BeFalse();
        modified.IsVoidExtent.Should().BeTrue();
    }

    #endregion

    #region Weight Range Tests

    [Fact]
    public void GetWeightRange_WithValidBlock_ShouldReturn7()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act
        var weightRange = block.GetWeightRange();

        // Assert
        weightRange.Should().HaveValue();
        weightRange.Should().Be(7);
    }

    [Fact]
    public void GetWeightRange_WithTooManyBits_ShouldReturnNull()
    {
        // Arrange - Flip high bit to get range of 31, but too many bits
        var block = PhysicalBlock.Create(0x0000000001FE000373UL);

        // Act
        var weightRange = block.GetWeightRange();

        // Assert
        weightRange.Should().BeNull();
    }

    [Fact]
    public void GetWeightRange_WithOneBitPerWeight_ShouldReturn1()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x4000000000800D44UL);

        // Act
        var weightRange = block.GetWeightRange();

        // Assert
        weightRange.Should().HaveValue();
        weightRange.Should().Be(1);
    }

    [Fact]
    public void GetWeightRange_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var weightRange = block.GetWeightRange();

        // Assert
        weightRange.Should().BeNull();
    }

    #endregion

    #region Weight Grid Dimensions Tests

    [Fact]
    public void GetWeightGridDimensions_WithValidBlock_ShouldReturn6x5()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act
        var dims = block.GetWeightGridDimensions();

        // Assert
        dims.Should().NotBeNull();
        dims!.Value.Width.Should().Be(6);
        dims.Value.Height.Should().Be(5);
    }

    [Fact]
    public void GetWeightGridDimensions_WithTooManyBitsForGrid_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000373UL);

        // Act
        var dims = block.GetWeightGridDimensions();

        // Assert
        dims.Should().BeNull();
        var error = block.IdentifyInvalidEncodingIssues();
        error.Should().Contain("Too many bits");
    }

    [Fact]
    public void GetWeightGridDimensions_WithDualPlaneBlock_ShouldReturn3x5()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE0005FFUL);

        // Act
        var dims = block.GetWeightGridDimensions();

        // Assert
        dims.Should().NotBeNull();
        dims!.Value.Width.Should().Be(3);
        dims.Value.Height.Should().Be(5);
    }

    [Fact]
    public void GetWeightGridDimensions_WithNonSharedCEM_ShouldReturn8x8()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x4000000000800D44UL);

        // Act
        var dims = block.GetWeightGridDimensions();

        // Assert
        dims.Should().NotBeNull();
        dims!.Value.Width.Should().Be(8);
        dims.Value.Height.Should().Be(8);
    }

    [Fact]
    public void GetWeightGridDimensions_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var dims = block.GetWeightGridDimensions();

        // Assert
        dims.Should().BeNull();
    }

    #endregion

    #region Dual Plane Tests

    [Fact]
    public void IsDualPlane_WithSinglePlaneBlock_ShouldReturnFalse()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act & Assert
        block.IsDualPlane.Should().BeFalse();
    }

    [Fact]
    public void IsDualPlane_WithDualPlaneBlock_ShouldReturnTrue()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE0005FFUL);

        // Act & Assert
        block.IsDualPlane.Should().BeTrue();
    }

    [Fact]
    public void IsDualPlane_WithErrorBlock_ShouldReturnFalse()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act & Assert
        block.IsDualPlane.Should().BeFalse();
    }

    [Fact]
    public void IsDualPlane_WithInvalidEncoding_ShouldReturnFalse()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000573UL);

        // Act & Assert
        block.IsDualPlane.Should().BeFalse();
        block.GetWeightGridDimensions().Should().BeNull();
        block.IdentifyInvalidEncodingIssues().Should().Contain("Too many bits");
    }

    [Fact]
    public void IsDualPlane_WithValidSinglePlaneBlock_ShouldHaveValidEncoding()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000108UL);

        // Act & Assert
        block.IsDualPlane.Should().BeFalse();
        block.IsIllegalEncoding.Should().BeFalse();
    }

    #endregion

    #region Weight Bit Count Tests

    [Fact]
    public void GetWeightBitCount_WithStandardBlock_ShouldReturn90()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act
        var bitCount = block.GetWeightBitCount();

        // Assert
        bitCount.Should().Be(90);
    }

    [Fact]
    public void GetWeightBitCount_WithDualPlaneBlock_ShouldReturn90()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE0005FFUL);

        // Act
        var bitCount = block.GetWeightBitCount();

        // Assert
        bitCount.Should().Be(90);
    }

    [Fact]
    public void GetWeightBitCount_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var bitCount = block.GetWeightBitCount();

        // Assert
        bitCount.Should().BeNull();
    }

    [Fact]
    public void GetWeightBitCount_WithVoidExtent_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var bitCount = block.GetWeightBitCount();

        // Assert
        bitCount.Should().BeNull();
    }

    [Fact]
    public void GetWeightBitCount_WithInvalidBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000573UL);

        // Act
        var bitCount = block.GetWeightBitCount();

        // Assert
        bitCount.Should().BeNull();
    }

    #endregion

    #region Weight Start Bit Tests

    [Fact]
    public void GetWeightStartBit_WithNonSharedCEM_ShouldReturn64()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x4000000000800D44UL);

        // Act
        var startBit = block.GetWeightStartBit();

        // Assert
        startBit.Should().Be(64);
    }

    [Fact]
    public void GetWeightStartBit_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var startBit = block.GetWeightStartBit();

        // Assert
        startBit.Should().BeNull();
    }

    [Fact]
    public void GetWeightStartBit_WithVoidExtent_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var startBit = block.GetWeightStartBit();

        // Assert
        startBit.Should().BeNull();
    }

    #endregion

    #region Error Block Tests

    [Fact]
    public void IsIllegalEncoding_WithValidBlocks_ShouldReturnFalse()
    {
        // Arrange & Act & Assert
        PhysicalBlock.Create(0x0000000001FE000173UL).IsIllegalEncoding.Should().BeFalse();
        PhysicalBlock.Create(0x0000000001FE0005FFUL).IsIllegalEncoding.Should().BeFalse();
        PhysicalBlock.Create(0x0000000001FE000108UL).IsIllegalEncoding.Should().BeFalse();
    }

    [Fact]
    public void IdentifyInvalidEncodingIssues_WithZeroBlock_ShouldReturnReservedBlockModeError()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var error = block.IdentifyInvalidEncodingIssues();

        // Assert
        error.Should().NotBeNull();
        error.Should().Contain("Reserved block mode");
    }

    [Fact]
    public void IdentifyInvalidEncodingIssues_WithTooManyWeightBits_ShouldReturnError()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000573UL);

        // Act
        var error = block.IdentifyInvalidEncodingIssues();

        // Assert
        error.Should().NotBeNull();
        error.Should().Contain("Too many bits required for weight grid");
    }

    [Theory]
    [InlineData(0x0000000001FE0005A8UL)]
    [InlineData(0x0000000001FE000588UL)]
    [InlineData(0x0000000001FE00002UL)]
    public void IdentifyInvalidEncodingIssues_WithInvalidBlocks_ShouldReturnError(ulong blockBits)
    {
        // Arrange
        var block = PhysicalBlock.Create(blockBits);

        // Act
        var error = block.IdentifyInvalidEncodingIssues();

        // Assert
        error.Should().NotBeNull();
    }

    [Fact]
    public void IdentifyInvalidEncodingIssues_WithDualPlaneFourPartitions_ShouldReturnError()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x000000000000001D1FUL);

        // Act
        var error = block.IdentifyInvalidEncodingIssues();

        // Assert
        block.GetPartitionsCount().Should().BeNull();
        error.Should().NotBeNull();
        error.Should().Contain("Both four partitions");
    }

    [Theory]
    [InlineData(0x000000000000000973UL)]
    [InlineData(0x000000000000001173UL)]
    [InlineData(0x000000000000001973UL)]
    public void GetPartitionsCount_WithInvalidPartitionConfig_ShouldReturnNull(ulong blockBits)
    {
        // Arrange
        var block = PhysicalBlock.Create(blockBits);

        // Act
        var partitions = block.GetPartitionsCount();

        // Assert
        partitions.Should().BeNull();
    }

    #endregion

    #region Partition Tests

    [Theory]
    [InlineData(0x0000000001FE000173UL, 1)]
    [InlineData(0x0000000001FE0005FFUL, 1)]
    [InlineData(0x0000000001FE000108UL, 1)]
    [InlineData(0x4000000000800D44UL, 2)]
    public void GetPartitionsCount_WithValidBlock_ShouldReturnExpectedCount(ulong blockBits, int expectedCount)
    {
        // Arrange
        var block = PhysicalBlock.Create(blockBits);

        // Act
        var count = block.GetPartitionsCount();

        // Assert
        count.Should().Be(expectedCount);
    }

    [Theory]
    [InlineData(0x4000000000FFED44UL, 0x3FF)]
    [InlineData(0x4000000000AAAD44UL, 0x155)]
    public void GetPartitionId_WithValidMultiPartitionBlock_ShouldReturnExpectedId(ulong blockBits, int expectedId)
    {
        // Arrange
        var block = PhysicalBlock.Create(blockBits);

        // Act
        var partitionId = block.GetPartitionId();

        // Assert
        partitionId.Should().Be(expectedId);
    }

    [Fact]
    public void GetPartitionId_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var partitionId = block.GetPartitionId();

        // Assert
        partitionId.Should().BeNull();
    }

    [Fact]
    public void GetPartitionId_WithVoidExtent_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var partitionId = block.GetPartitionId();

        // Assert
        partitionId.Should().BeNull();
    }

    #endregion

    #region Endpoint Mode Tests

    [Fact]
    public void GetEndpointMode_WithFourPartitionBlock_ShouldReturnSameModeForAll()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x000000000000001961UL);

        // Act & Assert
        for (int i = 0; i < 4; ++i)
        {
            var mode = block.GetEndpointMode(i);
            mode.Should().Be(ColorEndpointMode.LdrLumaDirect);
        }
    }

    [Fact]
    public void GetEndpointMode_WithNonSharedCEM_ShouldReturnDifferentModes()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x4000000000800D44UL);

        // Act
        var mode0 = block.GetEndpointMode(0);
        var mode1 = block.GetEndpointMode(1);

        // Assert
        mode0.Should().Be(ColorEndpointMode.LdrLumaDirect);
        mode1.Should().Be(ColorEndpointMode.LdrLumaBaseOffset);
    }

    [Fact]
    public void GetEndpointMode_WithVoidExtent_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var mode = block.GetEndpointMode(0);

        // Assert
        mode.Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(100)]
    public void GetEndpointMode_WithInvalidPartitionIndex_ShouldReturnNull(int index)
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act
        var mode = block.GetEndpointMode(index);

        // Assert
        mode.Should().BeNull();
    }

    #endregion

    #region Color Value Tests

    [Fact]
    public void GetColorValuesCount_WithStandardBlock_ShouldReturn2()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act
        var count = block.GetColorValuesCount();

        // Assert
        count.Should().Be(2);
    }

    [Fact]
    public void GetColorValuesCount_WithVoidExtent_ShouldReturn4()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var count = block.GetColorValuesCount();

        // Assert
        count.Should().Be(4);
    }

    [Fact]
    public void GetColorValuesCount_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var count = block.GetColorValuesCount();

        // Assert
        count.Should().BeNull();
    }

    [Fact]
    public void GetColorBitCount_WithStandardBlock_ShouldReturn16()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act
        var bitCount = block.GetColorBitCount();

        // Assert
        bitCount.Should().Be(16);
    }

    [Fact]
    public void GetColorBitCount_WithVoidExtent_ShouldReturn64()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var bitCount = block.GetColorBitCount();

        // Assert
        bitCount.Should().Be(64);
    }

    [Fact]
    public void GetColorBitCount_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var bitCount = block.GetColorBitCount();

        // Assert
        bitCount.Should().BeNull();
    }

    [Fact]
    public void GetColorValuesRange_WithStandardBlock_ShouldReturn255()
    {
        // Arrange
        var block = PhysicalBlock.Create(0x0000000001FE000173UL);

        // Act
        var range = block.GetColorValuesRange();

        // Assert
        range.Should().Be(255);
    }

    [Fact]
    public void GetColorValuesRange_WithVoidExtent_ShouldReturnMaxUInt16()
    {
        // Arrange
        var block = PhysicalBlock.Create(0xFFF8003FFE000DFCUL);

        // Act
        var range = block.GetColorValuesRange();

        // Assert
        range.Should().Be((1 << 16) - 1);
    }

    [Fact]
    public void GetColorValuesRange_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var range = block.GetColorValuesRange();

        // Assert
        range.Should().BeNull();
    }

    [Theory]
    [InlineData(0x0000000001FE000173UL, 17)]
    [InlineData(0x0000000001FE0005FFUL, 17)]
    [InlineData(0x0000000001FE000108UL, 17)]
    [InlineData(0x4000000000FFED44UL, 29)]
    [InlineData(0x4000000000AAAD44UL, 29)]
    [InlineData(0xFFF8003FFE000DFCUL, 64)]
    public void GetColorStartBit_WithVariousBlocks_ShouldReturnExpectedValue(ulong blockBits, int expectedStartBit)
    {
        // Arrange
        var block = PhysicalBlock.Create(blockBits);

        // Act
        var startBit = block.GetColorStartBit();

        // Assert
        startBit.Should().Be(expectedStartBit);
    }

    [Fact]
    public void GetColorStartBit_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var block = PhysicalBlock.Create(ErrorBlock);

        // Act
        var startBit = block.GetColorStartBit();

        // Assert
        startBit.Should().BeNull();
    }

    #endregion
}
