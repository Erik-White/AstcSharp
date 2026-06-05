using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests;

/// <summary>
/// Verifies the stream-to-stream decode and encode entry points on <see cref="AstcDecoder"/> and
/// <see cref="AstcEncoder"/>. These process one block-row band at a time to bound memory; the
/// tests assert their output is byte-for-byte identical to the in-memory whole-image APIs, that
/// the sync and async paths agree, and that edge bands (image dimensions not a multiple of the
/// footprint) round-trip correctly.
/// </summary>
public class StreamingTests
{
    // Footprints whose dimensions do not divide the fixture dimensions exercise the right/bottom
    // edge-clamping in the band loop, not just the interior fast path.
    [Theory]
    [InlineData(TestData.Astc.Rgba_4x4)]
    [InlineData(TestData.Astc.Rgba_6x6)]
    [InlineData(TestData.Astc.Rgb_5x4)]
    [InlineData(TestData.Astc.Rgb_12x12)]
    public void DecompressImage_StreamToStream_MatchesInMemory(string inputFile)
    {
        AstcFile file = LoadFixture(inputFile);
        byte[] expected = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint);

        using var source = new MemoryStream(file.Blocks.ToArray());
        using var destination = new MemoryStream();
        AstcDecoder.DecompressImage(source, destination, file.Width, file.Height, file.Footprint);

        Assert.Equal(expected, destination.ToArray());
    }

    [Theory]
    [InlineData(LdrDecodeMode.Linear)]
    [InlineData(LdrDecodeMode.Srgb)]
    public void DecompressImage_StreamToStream_HonoursDecodeMode(LdrDecodeMode mode)
    {
        AstcFile file = LoadFixture(TestData.Astc.Rgba_4x4);
        byte[] expected = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint, mode);

        using var source = new MemoryStream(file.Blocks.ToArray());
        using var destination = new MemoryStream();
        AstcDecoder.DecompressImage(source, destination, file.Width, file.Height, file.Footprint, mode);

        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public async Task DecompressImageAsync_StreamToStream_MatchesSync()
    {
        AstcFile file = LoadFixture(TestData.Astc.Rgba_6x6);

        using var syncSource = new MemoryStream(file.Blocks.ToArray());
        using var syncDestination = new MemoryStream();
        AstcDecoder.DecompressImage(syncSource, syncDestination, file.Width, file.Height, file.Footprint);

        using var asyncSource = new MemoryStream(file.Blocks.ToArray());
        using var asyncDestination = new MemoryStream();
        await AstcDecoder.DecompressImageAsync(asyncSource, asyncDestination, file.Width, file.Height, file.Footprint);

        Assert.Equal(syncDestination.ToArray(), asyncDestination.ToArray());
    }

    [Fact]
    public void DecompressHdrImage_StreamToStream_MatchesInMemory()
    {
        AstcFile file = LoadFixture(TestData.Astc.Hdr.Hdr_Tile);
        float[] expected = StreamCodec.DecodeHdr(file.Blocks, file.Width, file.Height, file.Footprint);

        using var source = new MemoryStream(file.Blocks.ToArray());
        using var destination = new MemoryStream();
        AstcDecoder.DecompressHdrImage(source, destination, file.Width, file.Height, file.Footprint);

        byte[] actual = destination.ToArray();
        Assert.Equal(expected, StreamCodec.ToFloats(actual, actual.Length));
    }

    [Fact]
    public async Task DecompressHdrImageAsync_StreamToStream_MatchesSync()
    {
        AstcFile file = LoadFixture(TestData.Astc.Hdr.Hdr_Tile);

        using var syncSource = new MemoryStream(file.Blocks.ToArray());
        using var syncDestination = new MemoryStream();
        AstcDecoder.DecompressHdrImage(syncSource, syncDestination, file.Width, file.Height, file.Footprint);

        using var asyncSource = new MemoryStream(file.Blocks.ToArray());
        using var asyncDestination = new MemoryStream();
        await AstcDecoder.DecompressHdrImageAsync(asyncSource, asyncDestination, file.Width, file.Height, file.Footprint);

        Assert.Equal(syncDestination.ToArray(), asyncDestination.ToArray());
    }

    [Fact]
    public void DecompressHdrImageHalf_StreamToStream_MatchesInMemory()
    {
        AstcFile file = LoadFixture(TestData.Astc.Hdr.Hdr_Tile);
        Half[] expected = StreamCodec.DecodeHdrHalf(file.Blocks, file.Width, file.Height, file.Footprint);

        using var source = new MemoryStream(file.Blocks.ToArray());
        using var destination = new MemoryStream();
        AstcDecoder.DecompressHdrImageHalf(source, destination, file.Width, file.Height, file.Footprint);

        byte[] actual = destination.ToArray();
        Assert.Equal(expected, StreamCodec.ToHalves(actual, actual.Length));
    }

    [Fact]
    public async Task DecompressHdrImageHalfAsync_StreamToStream_MatchesSync()
    {
        AstcFile file = LoadFixture(TestData.Astc.Hdr.Hdr_Tile);

        using var syncSource = new MemoryStream(file.Blocks.ToArray());
        using var syncDestination = new MemoryStream();
        AstcDecoder.DecompressHdrImageHalf(syncSource, syncDestination, file.Width, file.Height, file.Footprint);

        using var asyncSource = new MemoryStream(file.Blocks.ToArray());
        using var asyncDestination = new MemoryStream();
        await AstcDecoder.DecompressHdrImageHalfAsync(asyncSource, asyncDestination, file.Width, file.Height, file.Footprint);

        Assert.Equal(syncDestination.ToArray(), asyncDestination.ToArray());
    }

    [Theory]
    [InlineData(TestData.Astc.Rgba_4x4)]
    [InlineData(TestData.Astc.Rgb_5x4)]
    [InlineData(TestData.Astc.Rgb_12x12)]
    public void CompressImage_StreamToStream_MatchesInMemory(string inputFile)
    {
        AstcFile file = LoadFixture(inputFile);
        byte[] pixels = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint);
        byte[] expected = StreamCodec.Encode(pixels, file.Width, file.Height, file.Footprint);

        using var source = new MemoryStream(pixels);
        using var destination = new MemoryStream();
        AstcEncoder.CompressImage(source, destination, file.Width, file.Height, file.Footprint);

        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public async Task CompressImageAsync_StreamToStream_MatchesSync()
    {
        AstcFile file = LoadFixture(TestData.Astc.Rgb_5x4);
        byte[] pixels = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint);

        using var syncSource = new MemoryStream(pixels);
        using var syncDestination = new MemoryStream();
        AstcEncoder.CompressImage(syncSource, syncDestination, file.Width, file.Height, file.Footprint);

        using var asyncSource = new MemoryStream(pixels);
        using var asyncDestination = new MemoryStream();
        await AstcEncoder.CompressImageAsync(asyncSource, asyncDestination, file.Width, file.Height, file.Footprint);

        Assert.Equal(syncDestination.ToArray(), asyncDestination.ToArray());
    }

    [Fact]
    public void EncodeThenDecode_StreamToStream_RoundTripsBlocks()
    {
        // Encoding to a stream then decoding that stream must reproduce what the in-memory
        // round-trip produces, confirming the two band loops agree on block layout end-to-end.
        AstcFile file = LoadFixture(TestData.Astc.Rgba_4x4);
        byte[] pixels = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint);

        using var blocks = new MemoryStream();
        using (var pixelSource = new MemoryStream(pixels))
        {
            AstcEncoder.CompressImage(pixelSource, blocks, file.Width, file.Height, file.Footprint);
        }

        blocks.Position = 0;
        using var decoded = new MemoryStream();
        AstcDecoder.DecompressImage(blocks, decoded, file.Width, file.Height, file.Footprint);

        byte[] reencoded = StreamCodec.Encode(pixels, file.Width, file.Height, file.Footprint);
        byte[] expected = StreamCodec.DecodeLdr(reencoded, file.Width, file.Height, file.Footprint);
        Assert.Equal(expected, decoded.ToArray());
    }

    [Fact]
    public void DecompressImage_StreamToStream_TruncatedSource_Throws()
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        using var source = new MemoryStream(new byte[8]); // one 4x4 block needs 16 bytes
        using var destination = new MemoryStream();

        Assert.Throws<EndOfStreamException>(() =>
            AstcDecoder.DecompressImage(source, destination, 4, 4, footprint));
    }

    [Fact]
    public void DecompressImage_StreamToStream_NullArgs_Throw()
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentNullException>(() => AstcDecoder.DecompressImage(null!, stream, 4, 4, footprint));
        Assert.Throws<ArgumentNullException>(() => AstcDecoder.DecompressImage(stream, null!, 4, 4, footprint));
    }

    [Fact]
    public void CompressImage_StreamToStream_TruncatedSource_Throws()
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        using var source = new MemoryStream(new byte[32]); // a 4x4 image needs 64 bytes
        using var destination = new MemoryStream();

        Assert.Throws<EndOfStreamException>(() =>
            AstcEncoder.CompressImage(source, destination, 4, 4, footprint));
    }

    [Fact]
    public void CompressImage_StreamToStream_NullArgs_Throw()
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentNullException>(() => AstcEncoder.CompressImage(null!, stream, 4, 4, footprint));
        Assert.Throws<ArgumentNullException>(() => AstcEncoder.CompressImage(stream, null!, 4, 4, footprint));
    }

    private static AstcFile LoadFixture(string inputFile)
        => AstcFile.FromMemory(File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Astc", inputFile))));
}
