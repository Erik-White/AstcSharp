using BenchmarkDotNet.Attributes;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;

namespace AstcSharp.Benchmarks
{
    [MemoryDiagnoser]
    public class AstcDecodingBenchmarks
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
        public void ParseBlock()
        {
            var blocks = astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var block = new PhysicalBlock(new UInt128Ex(BitConverter.ToUInt64(blockBytes), BitConverter.ToUInt64(blockBytes.Slice(8))));
        }

        [Benchmark]
        public void DecodeEndpoints()
        {
            var blocks = astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var block = new PhysicalBlock(new UInt128Ex(BitConverter.ToUInt64(blockBytes), BitConverter.ToUInt64(blockBytes.Slice(8))));
            var ib = IntermediateBlock.UnpackIntermediateBlock(block);
        }

        [Benchmark]
        public void Partitioning()
        {
            var blocks = astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var block = new PhysicalBlock(new UInt128Ex(BitConverter.ToUInt64(blockBytes), BitConverter.ToUInt64(blockBytes.Slice(8))));
            var ib = IntermediateBlock.UnpackIntermediateBlock(block);
            var logical = ib is not null
                ? new LogicalBlock(Footprint.Get4x4(), ib)
                : throw new InvalidOperationException("Failed to unpack intermediate block");
        }
    }

}
