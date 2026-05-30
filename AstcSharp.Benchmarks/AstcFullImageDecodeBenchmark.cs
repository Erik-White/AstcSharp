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
    private byte[] ldrOutput = [];

    private byte[] hdrBlocks = [];
    private int hdrWidth;
    private int hdrHeight;
    private Footprint hdrFootprint;
    private float[] hdrOutput = [];

    [GlobalSetup]
    public void Setup()
    {
        string ldrPath = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "rgba-4x4.astc"));
        AstcFile ldr = AstcFile.FromMemory(File.ReadAllBytes(ldrPath));
        this.ldrBlocks = ldr.Blocks.ToArray();
        this.ldrWidth = ldr.Width;
        this.ldrHeight = ldr.Height;
        this.ldrFootprint = ldr.Footprint;
        this.ldrOutput = new byte[ldr.Width * ldr.Height * 4];

        string hdrPath = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "HdrPipeline", "hdr-tile.astc"));
        AstcFile hdr = AstcFile.FromMemory(File.ReadAllBytes(hdrPath));
        this.hdrBlocks = hdr.Blocks.ToArray();
        this.hdrWidth = hdr.Width;
        this.hdrHeight = hdr.Height;
        this.hdrFootprint = hdr.Footprint;
        this.hdrOutput = new float[hdr.Width * hdr.Height * 4];
    }

    [Benchmark]
    public bool DecompressLdrImage()
        => AstcDecoder.DecompressImage(this.ldrBlocks, this.ldrWidth, this.ldrHeight, this.ldrFootprint, this.ldrOutput);

    [Benchmark]
    public bool DecompressLdrImageSrgb()
        => AstcDecoder.DecompressImage(this.ldrBlocks, this.ldrWidth, this.ldrHeight, this.ldrFootprint, this.ldrOutput, LdrDecodeMode.Srgb);

    [Benchmark]
    public bool DecompressHdrImage()
        => AstcDecoder.DecompressHdrImage(this.hdrBlocks, this.hdrWidth, this.hdrHeight, this.hdrFootprint, this.hdrOutput);
}
