using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests.HDR;

/// <summary>
/// Verifies the FP16 (<see cref="Half"/>) HDR output path. The <see cref="Half"/> result is
/// produced by narrowing the validated float HDR output, so it must equal <c>(Half)</c> of the
/// float output channel-for-channel — exactly, no tolerance (ASTC spec §C.2.15, §C.2.23).
/// </summary>
public class HdrHalfOutputTests
{
    [Theory]
    [InlineData(TestData.Astc.Hdr.Hdr_A_1x1)]
    [InlineData(TestData.Astc.Hdr.Hdr_Tile)]
    [InlineData(TestData.Astc.Hdr.Ldr_A_1x1)]
    [InlineData(TestData.Astc.Hdr.Ldr_Tile)]
    [InlineData(TestData.Astc.Hdr.Hdr_Mixed_256_4x4)]
    [InlineData(TestData.Astc.Hdr.Hdr_Mixed_256_8x8)]
    public void DecompressHdrImageHalf_MatchesNarrowedFloatOutput(string inputFile)
    {
        byte[] astcData = File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Astc", inputFile)));
        AstcFile astcFile = AstcFile.FromMemory(astcData);

        Span<float> floatResult = AstcDecoder.DecompressHdrImage(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);
        Span<Half> halfResult = AstcDecoder.DecompressHdrImageHalf(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);

        Assert.Equal(floatResult.Length, halfResult.Length);
        for (int i = 0; i < floatResult.Length; i++)
        {
            // Bit-exact: narrowing is the only operation between the two outputs.
            Assert.Equal(BitConverter.HalfToUInt16Bits((Half)floatResult[i]), BitConverter.HalfToUInt16Bits(halfResult[i]));
        }
    }

    [Fact]
    public void DecompressHdrImageHalf_StreamOverload_ShouldMatchSpanOverload()
    {
        byte[] astcData = File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Astc", TestData.Astc.Hdr.Hdr_Tile)));
        AstcFile astcFile = AstcFile.FromMemory(astcData);

        Span<Half> expected = AstcDecoder.DecompressHdrImageHalf(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);
        Assert.False(expected.IsEmpty);

        using MemoryStream stream = new(astcFile.Blocks.ToArray());
        Span<Half> actual = AstcDecoder.DecompressHdrImageHalf(stream, astcFile.Width, astcFile.Height, astcFile.Footprint);

        Assert.Equal(expected.ToArray(), actual.ToArray());
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void DecompressHdrImageHalf_StreamOverloadIntoBuffer_ShouldMatchSpanOverload()
    {
        byte[] astcData = File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Astc", TestData.Astc.Hdr.Hdr_Tile)));
        AstcFile astcFile = AstcFile.FromMemory(astcData);

        var expected = new Half[astcFile.Width * astcFile.Height * 4];
        Assert.True(AstcDecoder.DecompressHdrImageHalf(astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint, expected));

        var actual = new Half[expected.Length];
        using MemoryStream stream = new(astcFile.Blocks.ToArray());
        Assert.True(AstcDecoder.DecompressHdrImageHalf(stream, astcFile.Width, astcFile.Height, astcFile.Footprint, actual));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecompressHdrImageHalf_StreamOverload_WithNullStream_ShouldThrow()
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        Assert.Throws<ArgumentNullException>(() =>
            AstcDecoder.DecompressHdrImageHalf((Stream)null!, 4, 4, footprint).ToArray());
    }

    [Fact]
    public void DecompressHdrImageHalf_StreamOverload_WithTruncatedStream_ShouldThrow()
    {
        using MemoryStream stream = new(new byte[8]);
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        Assert.Throws<EndOfStreamException>(() =>
            AstcDecoder.DecompressHdrImageHalf(stream, 4, 4, footprint).ToArray());
    }

    [Fact]
    public void DecompressHdrImageHalf_TooSmallBuffer_Throws()
    {
        byte[] astcData = File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Astc", TestData.Astc.Hdr.Hdr_Tile)));
        AstcFile astcFile = AstcFile.FromMemory(astcData);
        var undersized = new Half[(astcFile.Width * astcFile.Height * 4) - 1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AstcDecoder.DecompressHdrImageHalf(astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint, undersized));
    }

    [Fact]
    public void DecompressHdrImageHalf_MismatchedBlockCount_ReturnsEmpty()
    {
        byte[] astcData = File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Astc", TestData.Astc.Hdr.Hdr_Tile)));
        AstcFile astcFile = AstcFile.FromMemory(astcData);

        // Truncate the block stream by one block so the layout check rejects it.
        ReadOnlySpan<byte> truncated = astcFile.Blocks[..^16];
        Span<Half> result = AstcDecoder.DecompressHdrImageHalf(truncated, astcFile.Width, astcFile.Height, astcFile.Footprint);

        Assert.True(result.IsEmpty);
    }
}
