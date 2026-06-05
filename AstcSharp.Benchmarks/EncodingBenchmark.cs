using AstcSharp.Core;
using AstcSharp.Tests.Utils;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Per-block LDR encode benchmarks. The encoder's cost is dominated by the per-block configuration
/// search (endpoint fit, the 1024-seed partition scan, and the dual-plane channel search), so each
/// benchmark encodes a single footprint-sized block of a content archetype that drives a specific
/// code path: constant (void-extent fast path), gradient (single-partition), four-quadrant
/// (multi-partition search), and decorrelated alpha (dual-plane search). The footprint param scales
/// the per-block texel count and the partition/dual-plane work.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class EncodingBenchmark
{
    [Params(FootprintType.Footprint4x4, FootprintType.Footprint8x8, FootprintType.Footprint12x12)]
    public FootprintType FootprintType { get; set; }

    private Footprint footprint;
    private byte[] constant = [];
    private byte[] gradient = [];
    private byte[] fourQuadrant = [];
    private byte[] decorrelatedAlpha = [];

    [GlobalSetup]
    public void Setup()
    {
        this.footprint = Footprint.FromFootprintType(this.FootprintType);
        int w = this.footprint.Width;
        int h = this.footprint.Height;

        this.constant = Solid(w, h);
        this.gradient = Gradient(w, h);
        this.fourQuadrant = FourQuadrant(w, h);
        this.decorrelatedAlpha = DecorrelatedAlpha(w, h);
    }

    // Constant block -> void-extent fast path (no search).
    [Benchmark(Baseline = true)]
    public int EncodeConstant()
        => StreamCodec.Encode(this.constant, this.footprint.Width, this.footprint.Height, this.footprint).Length;

    // Smooth gradient -> single-partition path (one endpoint line fits well).
    [Benchmark]
    public int EncodeGradient()
        => StreamCodec.Encode(this.gradient, this.footprint.Width, this.footprint.Height, this.footprint).Length;

    // Four distinct colour regions -> exercises the multi-partition seed search.
    [Benchmark]
    public int EncodeFourQuadrant()
        => StreamCodec.Encode(this.fourQuadrant, this.footprint.Width, this.footprint.Height, this.footprint).Length;

    // Anti-correlated RGB/alpha -> exercises the dual-plane channel search.
    [Benchmark]
    public int EncodeDecorrelatedAlpha()
        => StreamCodec.Encode(this.decorrelatedAlpha, this.footprint.Width, this.footprint.Height, this.footprint).Length;

    private static byte[] Solid(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 73; pixels[i + 1] = 140; pixels[i + 2] = 200; pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static byte[] Gradient(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                byte v = (byte)(255 * x / Math.Max(1, width - 1));
                pixels[idx] = v; pixels[idx + 1] = (byte)(255 - v); pixels[idx + 2] = (byte)(128 + (v / 2)); pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] FourQuadrant(int width, int height)
    {
        (byte R, byte G, byte B)[] quadrant = [(240, 20, 20), (20, 240, 20), (20, 20, 240), (240, 240, 20)];
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                int cell = ((y < height / 2) ? 0 : 2) + ((x < width / 2) ? 0 : 1);
                (byte r, byte g, byte b) = quadrant[cell];
                pixels[idx] = r; pixels[idx + 1] = g; pixels[idx + 2] = b; pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] DecorrelatedAlpha(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                byte up = (byte)(255 * (x + y) / Math.Max(1, width + height - 2));
                pixels[idx] = up; pixels[idx + 1] = up; pixels[idx + 2] = up; pixels[idx + 3] = (byte)(255 - up);
            }
        }

        return pixels;
    }
}
