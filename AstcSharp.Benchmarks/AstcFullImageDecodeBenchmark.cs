using BenchmarkDotNet.Attributes;
using AstcSharp;
using System.IO;

namespace AstcSharp.Benchmarks
{
    [MemoryDiagnoser]
    public class AstcFullImageDecodeBenchmark
    {
        private byte[]? astcData;
        private AstcFile? astcFile;
        private Footprint? footprint;

        [GlobalSetup]
        public void Setup()
        {
            var path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Input", "atlas_small_4x4.astc"));
            astcData = File.ReadAllBytes(path);
            astcFile = AstcFile.LoadFromMemory(astcData, out var error);
            if (astcFile == null)
                throw new InvalidOperationException($"Failed to load ASTC file: {error}");
            footprint = astcFile.GetFootprint();
            if (!footprint.HasValue)
                throw new InvalidOperationException("Could not determine ASTC footprint");
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
                var block = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blockBytes), BitConverter.ToUInt64(blockBytes.Slice(8))));
                var ib = IntermediateAstcBlock.UnpackIntermediateBlock(block);
            }
        }
    }
}
