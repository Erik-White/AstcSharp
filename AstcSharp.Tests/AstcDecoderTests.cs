using System.Buffers.Binary;

using AstcSharp;
using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests;

#nullable enable

public class AstcDecoderTests
{
    [Theory]
    [InlineData(TestData.Astc.Rgba_4x4)]
    [InlineData(TestData.Astc.Rgba_5x5)]
    [InlineData(TestData.Astc.Rgba_6x6)]
    [InlineData(TestData.Astc.Rgba_8x8)]
    [InlineData(TestData.Astc.Checkerboard)]
    [InlineData(TestData.Astc.Checkered_4)]
    [InlineData(TestData.Astc.Checkered_5)]
    [InlineData(TestData.Astc.Checkered_6)]
    [InlineData(TestData.Astc.Checkered_7)]
    [InlineData(TestData.Astc.Checkered_8)]
    [InlineData(TestData.Astc.Checkered_9)]
    [InlineData(TestData.Astc.Checkered_10)]
    [InlineData(TestData.Astc.Checkered_11)]
    [InlineData(TestData.Astc.Checkered_12)]
    [InlineData(TestData.Astc.Footprint_4x4)]
    [InlineData(TestData.Astc.Footprint_5x4)]
    [InlineData(TestData.Astc.Footprint_5x5)]
    [InlineData(TestData.Astc.Footprint_6x5)]
    [InlineData(TestData.Astc.Footprint_6x6)]
    [InlineData(TestData.Astc.Footprint_8x5)]
    [InlineData(TestData.Astc.Footprint_8x6)]
    [InlineData(TestData.Astc.Footprint_8x8)]
    [InlineData(TestData.Astc.Footprint_10x5)]
    [InlineData(TestData.Astc.Footprint_10x6)]
    [InlineData(TestData.Astc.Footprint_10x8)]
    [InlineData(TestData.Astc.Footprint_10x10)]
    [InlineData(TestData.Astc.Footprint_12x10)]
    [InlineData(TestData.Astc.Footprint_12x12)]
    [InlineData(TestData.Astc.Rgb_4x4)]
    [InlineData(TestData.Astc.Rgb_5x4)]
    [InlineData(TestData.Astc.Rgb_6x6)]
    [InlineData(TestData.Astc.Rgb_8x8)]
    [InlineData(TestData.Astc.Rgb_12x12)]
    public void DecompressImage_WithTestdataFile_ShouldReturnExpectedByteCount(string inputFile)
    {
        string filePath = TestFile.GetInputFileFullPath(Path.Combine("Astc", inputFile));
        AstcFile astc = AstcFile.FromMemory(File.ReadAllBytes(filePath));

        byte[] result = StreamCodec.DecodeLdr(astc.Blocks, astc.Width, astc.Height, astc.Footprint);

        Assert.Equal(astc.Width * astc.Height * BlockInfo.ChannelsPerPixel, result.Length);
    }

    [Theory]
    [InlineData(TestData.Astc.Rgba_4x4, FootprintType.Footprint4x4, 256, 256)]
    [InlineData(TestData.Astc.Rgba_5x5, FootprintType.Footprint5x5, 256, 256)]
    [InlineData(TestData.Astc.Rgba_6x6, FootprintType.Footprint6x6, 256, 256)]
    [InlineData(TestData.Astc.Rgba_8x8, FootprintType.Footprint8x8, 256, 256)]
    public void DecompressImage_WithValidData_ShouldDecodeAllBlocks(
        string inputFile,
        FootprintType footprintType,
        int width,
        int height)
    {
        byte[] astcData = TestFile.Create(Path.Combine("Astc", inputFile)).Bytes[16..];
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int blockWidth = footprint.Width;
        int blockHeight = footprint.Height;
        int blocksWide = (width + blockWidth - 1) / blockWidth;
        int blocksHigh = (height + blockHeight - 1) / blockHeight;
        int expectedBlockCount = blocksWide * blocksHigh;

        // Check ASTC data structure
        Assert.Equal(0, astcData.Length % BlockInfo.SizeInBytes);
        Assert.Equal(expectedBlockCount, astcData.Length / BlockInfo.SizeInBytes);

        // Verify every block has a valid block-mode encoding.
        for (int i = 0; i < astcData.Length; i += BlockInfo.SizeInBytes)
        {
            byte[] block = astcData.AsSpan(i, BlockInfo.SizeInBytes).ToArray();
            UInt128 bits = new(BitConverter.ToUInt64(block, 8), BitConverter.ToUInt64(block, 0));
            BlockInfo info = BlockModeDecoder.Decode(bits);

            Assert.True(info.IsValid);
        }
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void DecompressImage_WithInvalidDimensions_ShouldThrowArgumentOutOfRangeException(int width, int height)
    {
        using MemoryStream source = new(new byte[16]);
        using MemoryStream destination = new();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AstcDecoder.DecompressImage(source, destination, width, height, footprint));
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void DecompressHdrImage_WithInvalidDimensions_ShouldThrowArgumentOutOfRangeException(int width, int height)
    {
        using MemoryStream source = new(new byte[16]);
        using MemoryStream destination = new();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AstcDecoder.DecompressHdrImage(source, destination, width, height, footprint));
    }

    [Fact]
    public void DecompressImage_WhenCalledFromManyThreads_ShouldProduceIdenticalOutput()
    {
        // Smoke test for accidental shared mutable state in the decode pipeline. Each
        // thread decodes the same input into its own streams; every result must match the
        // single-threaded reference byte-for-byte.
        string filePath = TestFile.GetInputFileFullPath(Path.Combine("Astc", TestData.Astc.Rgba_6x6));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(filePath));

        byte[] reference = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint);
        Assert.NotEmpty(reference);

        const int threadCount = 8;
        const int iterationsPerThread = 4;
        byte[][] results = new byte[threadCount][];
        byte[] blocks = file.Blocks.ToArray();

        Parallel.For(0, threadCount, i =>
        {
            byte[]? last = null;
            for (int j = 0; j < iterationsPerThread; j++)
            {
                last = StreamCodec.DecodeLdr(blocks, file.Width, file.Height, file.Footprint);
            }

            results[i] = last!;
        });

        foreach (byte[] result in results)
        {
            Assert.Equal(reference, result);
        }
    }

    [Fact]
    public void Rgba4x4Fixture_HasExpectedBlockTypeDistribution()
    {
        // Regression guard on block-mode parsing: rgba_4x4.astc contains a known mix of
        // void-extent, multi-partition, and dual-plane blocks. A change in these counts means
        // BlockModeDecoder started classifying blocks differently.
        string filePath = TestFile.GetInputFileFullPath(Path.Combine("Astc", TestData.Astc.Rgba_4x4));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(filePath));

        int blockCount = file.Blocks.Length / BlockInfo.SizeInBytes;
        int totalValid = 0;
        int voidExtent = 0;
        int singlePartition = 0;
        int twoPartition = 0;
        int threePartition = 0;
        int fourPartition = 0;
        int dualPlane = 0;

        for (int blockIdx = 0; blockIdx < blockCount; blockIdx++)
        {
            ReadOnlySpan<byte> blockSpan = file.Blocks.Slice(blockIdx * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes);
            UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(blockSpan);
            BlockInfo info = BlockModeDecoder.Decode(bits);
            Assert.True(info.IsValid, $"Block {blockIdx} of rgba_4x4.astc must decode as a valid block.");

            totalValid++;
            if (info.IsVoidExtent)
            {
                voidExtent++;
                continue;
            }

            _ = info.PartitionCount switch
            {
                1 => singlePartition++,
                2 => twoPartition++,
                3 => threePartition++,
                4 => fourPartition++,
                _ => 0,
            };

            if (info.DualPlane.Enabled)
            {
                dualPlane++;
            }
        }

        Assert.Equal(4096, totalValid);
        Assert.Equal(142, voidExtent);
        Assert.Equal(2528, singlePartition);
        Assert.Equal(1184, twoPartition);
        Assert.Equal(231, threePartition);
        Assert.Equal(11, fourPartition);
        Assert.Equal(661, dualPlane);
    }
}
