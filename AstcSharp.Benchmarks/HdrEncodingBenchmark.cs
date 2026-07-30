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

        constant = TestImages.SolidHdr(w, h, (Half)2.5f, (Half)1.25f, (Half)3.75f, (Half)1.0f);
        gradient = TestImages.ChromaticGradientHdr(w, h);
        fourQuadrant = TestImages.FourQuadrantHdr(w, h);
        decorrelatedAlpha = TestImages.DecorrelatedAlphaHdr(w, h);
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
}
