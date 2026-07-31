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

        this.constant = TestImage.Solid(w, h, 73, 140, 200, 255);
        this.gradient = TestImage.ChromaticGradient(w, h);
        this.fourQuadrant = TestImage.FourQuadrant(w, h);
        this.decorrelatedAlpha = TestImage.DecorrelatedAlpha(w, h);
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
}
