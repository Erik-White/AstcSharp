using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;
using BenchmarkDotNet.Attributes;

namespace AstcSharp.Benchmarks;

/// <summary>
/// End-to-end encode of a real multi-block tile: a fixture is decoded once in setup and a small
/// <see cref="TileSize"/>×<see cref="TileSize"/> crop is re-encoded per iteration. This is the
/// representative whole-image throughput measure (the counterpart to
/// <see cref="AstcFullImageDecodeBenchmark"/>), exercising the encoder across the mix of block
/// content natural image data contains, for both the LDR (RGBA8) and HDR (FP16) paths. A crop is used
/// rather than the full 256×256 fixture because the encoder's exhaustive per-block search makes a
/// full image take tens of seconds per iteration — far too long for a routine benchmark; the per-tile
/// time scales linearly with block count.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class AstcFullImageEncodeBenchmark
{
    // A 32×32 crop = 64 blocks at the 4×4 footprint — enough block-content variety to be
    // representative while keeping each iteration in the millisecond range.
    private const int TileSize = 32;

    private byte[] pixels = [];
    private Footprint footprint;

    private Half[] hdrPixels = [];
    private Footprint hdrFootprint;

    [GlobalSetup]
    public void Setup()
    {
        string ldrPath = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "rgba-4x4.astc"));
        AstcFile ldrFile = AstcFile.FromMemory(File.ReadAllBytes(ldrPath));
        this.footprint = ldrFile.Footprint;
        byte[] ldrFull = StreamCodec.DecodeLdr(ldrFile.Blocks, ldrFile.Width, ldrFile.Height, ldrFile.Footprint);
        this.pixels = TestImage.CropTopLeft(ldrFull, ldrFile.Width, TileSize, TileSize);

        string hdrPath = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "HdrPipeline", "mixed-256-4x4.astc"));
        AstcFile hdrFile = AstcFile.FromMemory(File.ReadAllBytes(hdrPath));
        this.hdrFootprint = hdrFile.Footprint;
        Half[] hdrFull = StreamCodec.DecodeHdrHalf(hdrFile.Blocks, hdrFile.Width, hdrFile.Height, hdrFile.Footprint);
        this.hdrPixels = CropTopLeftHalf(hdrFull, hdrFile.Width, TileSize, TileSize);
    }

    [Benchmark(Baseline = true)]
    public int CompressImage()
        => StreamCodec.Encode(this.pixels, TileSize, TileSize, this.footprint).Length;

    [Benchmark]
    public int CompressHdrImage()
        => StreamCodec.EncodeHdr(this.hdrPixels, TileSize, TileSize, this.hdrFootprint).Length;

    /// <summary>
    /// Crops the top-left <paramref name="cropWidth"/>×<paramref name="cropHeight"/> region of an
    /// FP16 RGBA image — the <see cref="Half"/> analogue of <see cref="TestImage.CropTopLeft"/>.
    /// </summary>
    private static Half[] CropTopLeftHalf(ReadOnlySpan<Half> rgba, int sourceWidth, int cropWidth, int cropHeight)
    {
        const int channels = 4;
        Half[] cropped = new Half[cropWidth * cropHeight * channels];
        for (int y = 0; y < cropHeight; y++)
        {
            ReadOnlySpan<Half> sourceRow = rgba.Slice(y * sourceWidth * channels, cropWidth * channels);
            sourceRow.CopyTo(cropped.AsSpan(y * cropWidth * channels, cropWidth * channels));
        }

        return cropped;
    }
}
