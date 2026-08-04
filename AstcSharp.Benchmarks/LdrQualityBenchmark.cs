using System.Collections.Concurrent;
using AstcSharp.Core;
using AstcSharp.Tests.Utils;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Combined quality-and-time report of the LDR encoder versus the ARM reference, across footprints.
/// </summary>
[Config(typeof(QualityConfig))]
public class LdrQualityBenchmark
{
    private static readonly ConcurrentDictionary<FootprintType, QualityResult> QualityByFootprint = new();

    private sealed class QualityConfig : ManualConfig
    {
#pragma warning disable S1144 // Not invoked directly, but used by BenchmarkDotNet via the [Config] attribute
        public QualityConfig()
        {
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
    private byte[] source = [];

    /// <summary>
    /// Returns the cached quality for <paramref name="footprintType"/>, measuring it on first request.
    /// Read by the custom quality columns, which run outside the benchmark's timed body.
    /// </summary>
    public static QualityResult QualityFor(FootprintType footprintType)
        => QualityByFootprint.GetOrAdd(footprintType, LdrQuality.Measure);

    [GlobalSetup]
    public void Setup()
    {
        footprint = Footprint.FromFootprintType(FootprintType);
        source = LdrQuality.LoadFixtureCrop();
        QualityFor(FootprintType);
    }

    [Benchmark]
    public int EncodeLdrCrop()
        => StreamCodec.Encode(source, LdrQuality.CropSize, LdrQuality.CropSize, footprint).Length;
}
