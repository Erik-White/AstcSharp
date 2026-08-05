using AstcSharp.Core;
using AstcSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AstcSharp.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class AstcFullImageDecodeBenchmark
{
    private byte[] ldrBlocks = [];
    private int ldrWidth;
    private int ldrHeight;
    private Footprint ldrFootprint;

    private byte[] hdrBlocks = [];
    private int hdrWidth;
    private int hdrHeight;
    private Footprint hdrFootprint;

    // Reused across iterations so the benchmark measures decode work, not allocation. The source
    // stream is rewound and the destination truncated before each decode.
    private MemoryStream ldrSource = null!;
    private MemoryStream hdrSource = null!;
    private MemoryStream output = null!;

    [GlobalSetup]
    public void Setup()
    {
        string ldrPath = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "rgba-4x4.astc"));
        AstcFile ldr = AstcFile.FromMemory(File.ReadAllBytes(ldrPath));
        this.ldrBlocks = ldr.Blocks.ToArray();
        this.ldrWidth = ldr.Width;
        this.ldrHeight = ldr.Height;
        this.ldrFootprint = ldr.Footprint;

        string hdrPath = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "Hdr", "hdr-tile.astc"));
        AstcFile hdr = AstcFile.FromMemory(File.ReadAllBytes(hdrPath));
        this.hdrBlocks = hdr.Blocks.ToArray();
        this.hdrWidth = hdr.Width;
        this.hdrHeight = hdr.Height;
        this.hdrFootprint = hdr.Footprint;

        this.ldrSource = new MemoryStream(this.ldrBlocks);
        this.hdrSource = new MemoryStream(this.hdrBlocks);
        this.output = new MemoryStream(Math.Max(this.ldrWidth * this.ldrHeight, this.hdrWidth * this.hdrHeight) * 4 * sizeof(float));
    }

    [Benchmark]
    public long DecompressLdrImage()
    {
        Reset(this.ldrSource);
        AstcDecoder.DecompressImage(this.ldrSource, this.output, this.ldrWidth, this.ldrHeight, this.ldrFootprint);
        return this.output.Length;
    }

    [Benchmark]
    public long DecompressLdrImageSrgb()
    {
        Reset(this.ldrSource);
        AstcDecoder.DecompressImage(this.ldrSource, this.output, this.ldrWidth, this.ldrHeight, this.ldrFootprint, LdrDecodeMode.Srgb);
        return this.output.Length;
    }

    [Benchmark]
    public long DecompressHdrImage()
    {
        Reset(this.hdrSource);
        AstcDecoder.DecompressHdrImage(this.hdrSource, this.output, this.hdrWidth, this.hdrHeight, this.hdrFootprint);
        return this.output.Length;
    }

    [Benchmark]
    public long DecompressHdrImageHalf()
    {
        Reset(this.hdrSource);
        AstcDecoder.DecompressHdrImageHalf(this.hdrSource, this.output, this.hdrWidth, this.hdrHeight, this.hdrFootprint);
        return this.output.Length;
    }

    private void Reset(MemoryStream source)
    {
        source.Position = 0;
        this.output.SetLength(0);
    }
}
