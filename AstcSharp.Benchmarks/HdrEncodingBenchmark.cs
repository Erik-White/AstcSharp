using AstcSharp.Core;
using AstcSharp.Tests.Utils;
using BenchmarkDotNet.Attributes;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Per-block HDR encode benchmarks, the HDR counterpart to <see cref="EncodingBenchmark"/>. Each
/// benchmark encodes a single footprint-sized block of an HDR content archetype that drives a
/// specific code path: constant (void-extent fast path), smooth gradient above 1.0 (single-partition
/// search), four bright regions (multi-partition search), and a colour ramp with decorrelated alpha
/// (dual-plane / CEM 15 search). Source is FP16 <see cref="Half"/> RGBA fed through the streaming
/// HDR encoder.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class HdrEncodingBenchmark
{
    [Params(FootprintType.Footprint4x4, FootprintType.Footprint8x8, FootprintType.Footprint12x12)]
    public FootprintType FootprintType { get; set; }

    private Footprint footprint;
    private Half[] constant = [];
    private Half[] gradient = [];
    private Half[] fourQuadrant = [];
    private Half[] decorrelatedAlpha = [];

    [GlobalSetup]
    public void Setup()
    {
        footprint = Footprint.FromFootprintType(FootprintType);
        int w = footprint.Width;
        int h = footprint.Height;

        constant = Solid(w, h);
        gradient = Gradient(w, h);
        fourQuadrant = FourQuadrant(w, h);
        decorrelatedAlpha = DecorrelatedAlpha(w, h);
    }

    // Constant block -> void-extent fast path (no search).
    [Benchmark(Baseline = true)]
    public int EncodeConstant()
        => StreamCodec.EncodeHdr(constant, footprint.Width, footprint.Height, footprint).Length;

    // Smooth HDR gradient -> single-partition path (one endpoint line fits well).
    [Benchmark]
    public int EncodeGradient()
        => StreamCodec.EncodeHdr(gradient, footprint.Width, footprint.Height, footprint).Length;

    // Four distinct bright regions -> exercises the multi-partition seed search.
    [Benchmark]
    public int EncodeFourQuadrant()
        => StreamCodec.EncodeHdr(fourQuadrant, footprint.Width, footprint.Height, footprint).Length;

    // Colour ramp with anti-correlated alpha -> exercises the CEM 15 / dual-plane search.
    [Benchmark]
    public int EncodeDecorrelatedAlpha()
        => StreamCodec.EncodeHdr(decorrelatedAlpha, footprint.Width, footprint.Height, footprint).Length;

    private static Half[] Solid(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (Half)2.5f; pixels[i + 1] = (Half)1.25f; pixels[i + 2] = (Half)3.75f; pixels[i + 3] = (Half)1.0f;
        }

        return pixels;
    }

    private static Half[] Gradient(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)x / Math.Max(1, width - 1);
                pixels[idx] = (Half)(4.0f * t); pixels[idx + 1] = (Half)(2.0f * (1.0f - t)); pixels[idx + 2] = (Half)(1.0f + (2.0f * t)); pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    private static Half[] FourQuadrant(int width, int height)
    {
        (float R, float G, float B)[] quadrant = [(4.0f, 0.25f, 0.25f), (0.25f, 4.0f, 0.25f), (0.25f, 0.25f, 4.0f), (4.0f, 4.0f, 0.25f)];
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                int cell = ((y < height / 2) ? 0 : 2) + ((x < width / 2) ? 0 : 1);
                (float r, float g, float b) = quadrant[cell];
                pixels[idx] = (Half)r; pixels[idx + 1] = (Half)g; pixels[idx + 2] = (Half)b; pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    private static Half[] DecorrelatedAlpha(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                Half up = (Half)(4.0f * t);
                pixels[idx] = up; pixels[idx + 1] = up; pixels[idx + 2] = up; pixels[idx + 3] = (Half)(4.0f * (1.0f - t));
            }
        }

        return pixels;
    }
}
