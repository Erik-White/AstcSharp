using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AstcSharp.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class DecodingBenchmark
{
    private AstcFile? astcFile;

    [GlobalSetup]
    public void Setup()
    {
        string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "rgba-4x4.astc"));
        byte[] astcData = File.ReadAllBytes(path);
        this.astcFile = AstcFile.FromMemory(astcData);
    }

    [Benchmark]
    public bool DecodeBlockInfo()
    {
        ReadOnlySpan<byte> blocks = this.astcFile!.Blocks;
        Span<byte> blockBytes = stackalloc byte[16];
        blocks[..16].CopyTo(blockBytes);
        ulong low = BitConverter.ToUInt64(blockBytes);
        ulong high = BitConverter.ToUInt64(blockBytes[8..]);
        UInt128 bits = (UInt128)low | ((UInt128)high << 64);

        BlockInfo info = BlockModeDecoder.Decode(bits);

        return info.IsValid;
    }

    [Benchmark]
    public int Partitioning()
    {
        ReadOnlySpan<byte> blocks = this.astcFile!.Blocks;
        Span<byte> blockBytes = stackalloc byte[16];
        blocks[..16].CopyTo(blockBytes);
        ulong low = BitConverter.ToUInt64(blockBytes);
        ulong high = BitConverter.ToUInt64(blockBytes[8..]);
        UInt128 bits = (UInt128)low | ((UInt128)high << 64);
        BlockInfo info = BlockModeDecoder.Decode(bits);
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        Span<byte> pixels = stackalloc byte[footprint.PixelCount * 4];
        LogicalBlock.DecodeToBytes(bits, in info, footprint, pixels);
        return pixels[0];
    }
}
