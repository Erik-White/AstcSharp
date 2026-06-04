using AstcSharp.Core;
using AstcSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AstcSharp.Benchmarks;

/// <summary>
/// End-to-end LDR encode of a real multi-block tile: a fixture is decoded to RGBA8 once in setup and
/// a small <see cref="TileSize"/>×<see cref="TileSize"/> crop is re-encoded per iteration. This is
/// the representative whole-image throughput measure (the counterpart to
/// <see cref="AstcFullImageDecodeBenchmark"/>), exercising the encoder across the mix of block
/// content natural image data contains. A crop is used rather than the full 256×256 fixture because
/// the encoder's exhaustive per-block search makes a full image take tens of seconds per iteration —
/// far too long for a routine benchmark; the per-tile time scales linearly with block count.
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

    [GlobalSetup]
    public void Setup()
    {
        string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "rgba-4x4.astc"));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(path));
        this.footprint = file.Footprint;

        byte[] full = AstcDecoder.DecompressImage(file.Blocks, file.Width, file.Height, file.Footprint).ToArray();
        this.pixels = BenchmarkImage.CropTopLeft(full, file.Width, TileSize, TileSize);
    }

    [Benchmark]
    public int CompressImage()
        => AstcEncoder.CompressImage(this.pixels, TileSize, TileSize, this.footprint).Length;
}
