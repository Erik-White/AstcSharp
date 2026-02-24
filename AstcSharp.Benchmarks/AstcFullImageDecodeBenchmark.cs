
using AstcSharp.IO;
using AstcSharp.TexelBlock;
using BenchmarkDotNet.Attributes;

namespace AstcSharp.Benchmarks;

[MemoryDiagnoser]
public class AstcFullImageDecodeBenchmark
{
    private AstcFile? _astcFile;

    [GlobalSetup]
    public void Setup()
    {
        var path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Input", "atlas_small_4x4.astc"));
        var astcData = File.ReadAllBytes(path);
        _astcFile = AstcFile.FromMemory(astcData);
    }

    [Benchmark]
    public void FullImageDecode()
    {
        var blocks = _astcFile!.Blocks;
        int numBlocks = blocks.Length / 16;
        Span<byte> blockBytes = stackalloc byte[16];
        for (int i = 0; i < numBlocks; ++i)
        {
            blocks.Slice(i * 16, 16).CopyTo(blockBytes);
            var low = BitConverter.ToUInt64(blockBytes);
            var high = BitConverter.ToUInt64(blockBytes.Slice(8));
            var block = PhysicalBlock.Create((UInt128)low | ((UInt128)high << 64));
            var _ = IntermediateBlock.UnpackIntermediateBlock(block);
        }
    }
}
