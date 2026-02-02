using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.TexelBlock;
using FluentAssertions;

namespace AstcSharp.Tests;

public class IntermediateAstcBlockTests
{
    private static readonly UInt128 ErrorBlock = UInt128.Zero;

    private static byte[] LoadASTCFile(string basename)
    {
        var filename = Path.Combine("TestData", "Input", basename + ".astc");
        File.Exists(filename).Should().BeTrue($"Testdata missing: {filename}");
        var data = File.ReadAllBytes(filename);
        data.Length.Should().BeGreaterOrEqualTo(16, "ASTC file too small");
        return data.Skip(16).ToArray();
    }

    #region Unpack Error Tests

    [Fact]
    public void UnpackVoidExtent_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var errorBlock = PhysicalBlock.Create(ErrorBlock);

        // Act
        var result = IntermediateBlock.UnpackVoidExtent(errorBlock);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void UnpackIntermediateBlock_WithErrorBlock_ShouldReturnNull()
    {
        // Arrange
        var errorBlock = PhysicalBlock.Create(ErrorBlock);

        // Act
        var result = IntermediateBlock.UnpackIntermediateBlock(errorBlock);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Endpoint Range Tests

    [Fact]
    public void EndpointRangeForBlock_WithoutWeights_ShouldReturnNegativeOne()
    {
        // Arrange
        var data = new IntermediateBlock.IntermediateBlockData
        {
            weightRange = 15,
            weightGridX = 6,
            weightGridY = 6
        };

        // Act
        var result = IntermediateBlock.EndpointRangeForBlock(data);

        // Assert
        result.Should().Be(-1);
    }

    [Fact]
    public void Pack_WithIncorrectNumberOfWeights_ShouldReturnError()
    {
        // Arrange
        var data = new IntermediateBlock.IntermediateBlockData
        {
            weightRange = 15,
            weightGridX = 6,
            weightGridY = 6
        };

        // Act
        var (error, _) = IntermediateBlock.Pack(data);

        // Assert
        error.Should().NotBeNull();
        error.Should().Contain("Incorrect number of weights");
    }

    [Fact]
    public void EndpointRangeForBlock_WithNotEnoughBits_ShouldReturnNegativeTwo()
    {
        // Arrange
        var data = new IntermediateBlock.IntermediateBlockData
        {
            weightRange = 1,
            partitionId = 0,
            weightGridX = 8,
            weightGridY = 8,
            endpoints = new List<IntermediateBlock.IntermediateEndpointData>
            {
                new() { mode = ColorEndpointMode.LdrRgbDirect },
                new() { mode = ColorEndpointMode.LdrRgbDirect },
                new() { mode = ColorEndpointMode.LdrRgbDirect }
            }
        };

        // Act
        var result = IntermediateBlock.EndpointRangeForBlock(data);

        // Assert
        result.Should().Be(-2);
    }

    [Fact]
    public void Pack_WithNotEnoughBitsForColors_ShouldReturnError()
    {
        // Arrange
        var data = new IntermediateBlock.IntermediateBlockData
        {
            weightRange = 1,
            partitionId = 0,
            weightGridX = 8,
            weightGridY = 8,
            weights = new int[64],
            endpoints = new List<IntermediateBlock.IntermediateEndpointData>
            {
                new() { mode = ColorEndpointMode.LdrRgbDirect },
                new() { mode = ColorEndpointMode.LdrRgbDirect },
                new() { mode = ColorEndpointMode.LdrRgbDirect }
            }
        };

        // Act
        var (error, _) = IntermediateBlock.Pack(data);

        // Assert
        error.Should().NotBeNull();
        error.Should().Contain("illegal color range");
    }

    [Fact]
    public void EndpointRangeForBlock_WithIncreasingWeightGrid_ShouldDecreaseColorRange()
    {
        // Arrange
        var data = new IntermediateBlock.IntermediateBlockData
        {
            weightRange = 2,
            dualPlaneChannel = null,
            endpoints = new List<IntermediateBlock.IntermediateEndpointData>
            {
                new() { mode = ColorEndpointMode.LdrRgbDirect },
                new() { mode = ColorEndpointMode.LdrRgbDirect }
            }
        };

        var weightParams = new List<(int w, int h)>();
        for (int y = 2; y < 8; ++y)
            for (int x = 2; x < 8; ++x)
                weightParams.Add((x, y));

        weightParams.Sort((a, b) => (a.w * a.h).CompareTo(b.w * b.h));

        // Act & Assert
        int lastColorRange = 255;
        foreach (var (w, h) in weightParams)
        {
            data.weightGridX = w;
            data.weightGridY = h;
            int colorRange = IntermediateBlock.EndpointRangeForBlock(data);

            colorRange.Should().BeLessOrEqualTo(lastColorRange);
            lastColorRange = Math.Min(colorRange, lastColorRange);
        }

        lastColorRange.Should().BeLessThan(255);
    }

    [Fact]
    public void EndpointRange_WithStandardBlock_ShouldBe255()
    {
        // Arrange
        var block = PhysicalBlock.Create((UInt128)0x0000000001FE000173UL);

        // Act
        var data = IntermediateBlock.UnpackIntermediateBlock(block);

        // Assert
        block.GetColorValuesRange().Should().Be(255);
        data.Should().NotBeNull();
        data!.endpoints.Should().ContainSingle();
        data.endpoints[0].mode.Should().Be(ColorEndpointMode.LdrLumaDirect);
        data.endpoints[0].colors.Should().Equal(new List<int> { 0, 255 });
        data.endpointRange.Should().Be(255);
    }

    #endregion

    #region Unpack Non-Void Extent Tests

    [Fact]
    public void UnpackIntermediateBlock_WithStandardBlock_ShouldReturnCorrectData()
    {
        // Arrange
        var block = PhysicalBlock.Create((UInt128)0x0000000001FE000173UL);

        // Act
        var result = IntermediateBlock.UnpackIntermediateBlock(block);

        // Assert
        result.Should().NotBeNull();
        var data = result!;

        data.weightGridX.Should().Be(6);
        data.weightGridY.Should().Be(5);
        data.weightRange.Should().Be(7);
        data.partitionId.Should().BeNull();
        data.dualPlaneChannel.Should().BeNull();

        data.weights.Should().HaveCount(30);
        data.weights.Should().AllBeEquivalentTo(0);

        data.endpoints.Should().ContainSingle();
        var endpoint = data.endpoints[0];
        endpoint.mode.Should().Be(ColorEndpointMode.LdrLumaDirect);
        endpoint.colors.Should().HaveCount(2);
        endpoint.colors[0].Should().Be(0);
        endpoint.colors[1].Should().Be(255);
    }

    #endregion

    #region Pack Non-Void Extent Tests

    [Fact]
    public void Pack_WithStandardBlockData_ShouldProduceExpectedBits()
    {
        // Arrange
        var data = new IntermediateBlock.IntermediateBlockData
        {
            weightGridX = 6,
            weightGridY = 5,
            weightRange = 7,
            partitionId = null,
            dualPlaneChannel = null,
            weights = new int[30]
        };

        var endpoint = new IntermediateBlock.IntermediateEndpointData
        {
            mode = ColorEndpointMode.LdrLumaDirect
        };
        endpoint.colors.Add(0);
        endpoint.colors.Add(255);
        data.endpoints.Add(endpoint);

        // Act
        var (error, packed) = IntermediateBlock.Pack(data);

        // Assert
        error.Should().BeNull();
        packed.Should().Be((UInt128)0x0000000001FE000173UL);
    }

    [Fact]
    public void Pack_WithLargeGapInBits_ShouldPreserveOriginalEncoding()
    {
        // Arrange
        var original = new UInt128(0xBEDEAD0000000000UL, 0x0000000001FE032EUL);
        var block = PhysicalBlock.Create(original);
        var data = IntermediateBlock.UnpackIntermediateBlock(block);

        // Act
        data.Should().NotBeNull();
        var intermediate = data!;

        // Assert unpacked values
        intermediate.weightGridX.Should().Be(2);
        intermediate.weightGridY.Should().Be(3);
        intermediate.weightRange.Should().Be(15);
        intermediate.partitionId.Should().BeNull();
        intermediate.dualPlaneChannel.Should().BeNull();
        intermediate.endpoints.Should().ContainSingle();
        intermediate.endpoints[0].mode.Should().Be(ColorEndpointMode.LdrLumaDirect);
        intermediate.endpoints[0].colors.Should().Equal(new List<int> { 255, 0 });

        // Act - repack
        var (error, repacked) = IntermediateBlock.Pack(intermediate);

        // Assert
        error.Should().BeNull();
        repacked.Should().Be(original);
    }

    #endregion

    #region Unpack Void Extent Tests

    [Fact]
    public void UnpackVoidExtent_WithAllOnesPattern_ShouldReturnZeroColors()
    {
        // Arrange
        var block = PhysicalBlock.Create((UInt128)0xFFFFFFFFFFFFFDFCUL);

        // Act
        var result = IntermediateBlock.UnpackVoidExtent(block);

        // Assert
        result.Should().NotBeNull();
        var data = result!.Value;

        data.r.Should().Be(0);
        data.g.Should().Be(0);
        data.b.Should().Be(0);
        data.a.Should().Be(0);

        data.coords.Should().AllSatisfy(c => c.Should().Be((1 << 13) - 1));
    }

    [Fact]
    public void UnpackVoidExtent_WithColorData_ShouldReturnCorrectColors()
    {
        // Arrange
        var blockBits = new UInt128(0xdeadbeefdeadbeefUL, 0xFFF8003FFE000DFCUL);
        var block = PhysicalBlock.Create(blockBits);

        // Act
        var result = IntermediateBlock.UnpackVoidExtent(block);

        // Assert
        result.Should().NotBeNull();
        var data = result!.Value;

        data.r.Should().Be(0xbeef);
        data.g.Should().Be(0xdead);
        data.b.Should().Be(0xbeef);
        data.a.Should().Be(0xdead);

        data.coords[0].Should().Be(0);
        data.coords[1].Should().Be(8191);
        data.coords[2].Should().Be(0);
        data.coords[3].Should().Be(8191);
    }

    #endregion

    #region Pack Void Extent Tests

    [Fact]
    public void Pack_WithZeroColorVoidExtent_ShouldProduceAllOnesPattern()
    {
        // Arrange
        var data = new IntermediateBlock.VoidExtentData
        {
            r = 0,
            g = 0,
            b = 0,
            a = 0,
            coords = new ushort[4]
        };

        for (int i = 0; i < 4; ++i)
            data.coords[i] = (ushort)((1 << 13) - 1);

        // Act
        var (error, packed) = IntermediateBlock.Pack(data);

        // Assert
        error.Should().BeNull();
        packed.Should().Be((UInt128)0xFFFFFFFFFFFFFDFCUL);
    }

    [Fact]
    public void Pack_WithColorVoidExtent_ShouldProduceExpectedBits()
    {
        // Arrange
        var data = new IntermediateBlock.VoidExtentData
        {
            r = 0xbeef,
            g = 0xdead,
            b = 0xbeef,
            a = 0xdead,
            coords = new ushort[4] { 0, 8191, 0, 8191 }
        };

        // Act
        var (error, packed) = IntermediateBlock.Pack(data);

        // Assert
        error.Should().BeNull();
        packed.Should().Be(new UInt128(0xdeadbeefdeadbeefUL, 0xFFF8003FFE000DFCUL));
    }

    #endregion

    #region Round-Trip Tests

    [Theory]
    [InlineData(0xe8e8eaea20000980UL, 0x20000200cb73f045UL)]
    [InlineData(0x3300c30700cb01c5UL, 0x0573907b8c0f6879UL)]
    public void PackUnpack_WithSameCEM_ShouldRoundTripCorrectly(ulong high, ulong low)
    {
        // Arrange
        var original = new UInt128(high, low);
        var block = PhysicalBlock.Create(original);

        // Act
        var unpacked = IntermediateBlock.UnpackIntermediateBlock(block);

        unpacked.Should().NotBeNull();

        var (error, repacked) = IntermediateBlock.Pack(unpacked!);

        // Assert
        error.Should().BeNull();
        repacked.Should().Be(original);
    }

    #endregion

    #region Integration Tests with Test Data

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
    public void PackUnpack_WithTestDataBlocks_ShouldPreserveBlockProperties(string imageName, int checkeredDim)
    {
        // Arrange
        const int astcDim = 8;
        int imgDim = checkeredDim * astcDim;
        var astcData = LoadASTCFile(imageName);
        int numBlocks = (imgDim / astcDim) * (imgDim / astcDim);

        (astcData.Length % PhysicalBlock.SizeInBytes).Should().Be(0);

        // Act & Assert - test each block
        for (int i = 0; i < numBlocks; ++i)
        {
            var slice = new ReadOnlySpan<byte>(astcData, i * PhysicalBlock.SizeInBytes, PhysicalBlock.SizeInBytes);
            var blockBits = new UInt128(
                BitConverter.ToUInt64(slice.Slice(8, 8)),
                BitConverter.ToUInt64(slice.Slice(0, 8)));
            var originalBlock = PhysicalBlock.Create(blockBits);

            // Unpack and repack
            UInt128 repacked;
            if (originalBlock.IsVoidExtent)
            {
                var voidData = IntermediateBlock.UnpackVoidExtent(originalBlock);
                voidData.Should().NotBeNull();

                var (error, packed) = IntermediateBlock.Pack(voidData!.Value);
                error.Should().BeNull();
                repacked = packed;
            }
            else
            {
                var intermediateData = IntermediateBlock.UnpackIntermediateBlock(originalBlock);
                intermediateData.Should().NotBeNull();

                // Verify endpoint range was set
                intermediateData!.endpointRange.Should().Be(originalBlock.GetColorValuesRange());

                // Clear endpoint range before repacking (to test calculation)
                intermediateData.endpointRange = null;
                var (error, packed) = IntermediateBlock.Pack(intermediateData);
                error.Should().BeNull();
                repacked = packed;
            }

            // Verify repacked block
            var repackedBlock = PhysicalBlock.Create(repacked);
            VerifyBlockPropertiesMatch(repackedBlock, originalBlock);
        }
    }

    private static void VerifyBlockPropertiesMatch(PhysicalBlock repacked, PhysicalBlock original)
    {
        repacked.IsIllegalEncoding.Should().BeFalse();

        // Verify color bits match
        var repackedColorBitCount = repacked.GetColorBitCount().Value;
        var repackedColorMask = UInt128Extensions.OnesMask(repackedColorBitCount);
        var repackedColorBits = (repacked.BlockBits >> repacked.GetColorStartBit().Value) & repackedColorMask;

        var originalColorBitCount = original.GetColorBitCount().Value;
        var originalColorMask = UInt128Extensions.OnesMask(originalColorBitCount);
        var originalColorBits = (original.BlockBits >> original.GetColorStartBit().Value) & originalColorMask;

        repackedColorMask.Should().Be(originalColorMask);
        repackedColorBits.Should().Be(originalColorBits);

        // Verify void extent properties
        repacked.IsVoidExtent.Should().Be(original.IsVoidExtent);
        repacked.GetVoidExtentCoordinates().Should().Equal(original.GetVoidExtentCoordinates());

        // Verify weight properties
        repacked.GetWeightGridDimensions().Should().Be(original.GetWeightGridDimensions());
        repacked.GetWeightRange().Should().Be(original.GetWeightRange());
        repacked.GetWeightBitCount().Should().Be(original.GetWeightBitCount());
        repacked.GetWeightStartBit().Should().Be(original.GetWeightStartBit());

        // Verify dual plane properties
        repacked.IsDualPlane.Should().Be(original.IsDualPlane);
        repacked.GetDualPlaneChannel().Should().Be(original.GetDualPlaneChannel());

        // Verify partition properties
        repacked.GetPartitionsCount().Should().Be(original.GetPartitionsCount());
        repacked.GetPartitionId().Should().Be(original.GetPartitionId());

        // Verify color value properties
        repacked.GetColorValuesCount().Should().Be(original.GetColorValuesCount());
        repacked.GetColorValuesRange().Should().Be(original.GetColorValuesRange());

        // Verify endpoint modes for all partitions
        var numParts = repacked.GetPartitionsCount().GetValueOrDefault(0);
        for (int j = 0; j < numParts; ++j)
        {
            repacked.GetEndpointMode(j).Should().Be(original.GetEndpointMode(j));
        }
    }

    #endregion
}
