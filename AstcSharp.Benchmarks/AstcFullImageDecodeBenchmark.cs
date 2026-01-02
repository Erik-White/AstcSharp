using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;
using BenchmarkDotNet.Attributes;

namespace AstcSharp.Benchmarks;

[MemoryDiagnoser]
public class AstcFullImageDecodeBenchmark
{
    private byte[]? astcData;
    private AstcFile? astcFile;

    [GlobalSetup]
    public void Setup()
    {
        var path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Input", "atlas_small_4x4.astc"));
        astcData = File.ReadAllBytes(path);
        astcFile = AstcFile.FromMemory(astcData);
    }

    [Benchmark]
    public void FullImageDecode()
    {
        var blocks = astcFile!.Blocks;
        int numBlocks = blocks.Length / 16;
        Span<byte> blockBytes = stackalloc byte[16];
        for (int i = 0; i < numBlocks; ++i)
        {
            blocks.Slice(i * 16, 16).CopyTo(blockBytes);
            var block = new PhysicalBlock(new UInt128Ex(BitConverter.ToUInt64(blockBytes), BitConverter.ToUInt64(blockBytes.Slice(8))));
            var ib = IntermediateBlock.UnpackIntermediateBlock(block);
        }
    }
}
