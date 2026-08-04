using System.Collections.Concurrent;
using AstcSharp.Core;
using AstcSharp.Tests.Utils;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Combined quality-and-time report of the HDR encoder versus the ARM reference, across footprints.
/// </summary>
[Config(typeof(QualityConfig))]
public class HdrQualityBenchmark
{
    private static readonly ConcurrentDictionary<FootprintType, QualityResult> QualityByFootprint = new();

    private sealed class QualityConfig : ManualConfig
    {
#pragma warning disable S1144 // Not invoked directly, but used by BenchmarkDotNet via the [Config] attribute.
        public QualityConfig()
        {
            // Quality (not time) is the point here, and it is computed exactly in GlobalSetup
            AddJob(Job.Dry.WithToolchain(InProcessEmitToolchain.Instance));
            AddColumn(QualityColumn.For(QualityFor));
        }
#pragma warning restore S1144
    }

    [Params(
        FootprintType.Footprint4x4,
        FootprintType.Footprint6x6,
        FootprintType.Footprint8x8,
        FootprintType.Footprint10x10,
        FootprintType.Footprint12x12)]
    public FootprintType FootprintType { get; set; }

    private Footprint footprint;
    private Half[] source = [];

    /// <summary>
    /// Returns the cached quality for <paramref name="footprintType"/>, measuring it on first request.
    /// Read by the custom quality columns, which run outside the benchmark's timed body.
    /// </summary>
    public static QualityResult QualityFor(FootprintType footprintType)
        => QualityByFootprint.GetOrAdd(footprintType, HdrQuality.Measure);

    [GlobalSetup]
    public void Setup()
    {
        footprint = Footprint.FromFootprintType(FootprintType);
        source = HdrQuality.LoadFixtureCropForFootprint();

        // Measure and cache the (deterministic) quality now, so the columns read it without the timing
        // sampler ever re-running the comparison.
        QualityFor(FootprintType);
    }

    [Benchmark]
    public int EncodeHdrCrop()
        => StreamCodec.EncodeHdr(source, HdrQuality.CropSize, HdrQuality.CropSize, footprint).Length;
}
