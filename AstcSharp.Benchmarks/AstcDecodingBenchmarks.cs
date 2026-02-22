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
            var low = BitConverter.ToUInt64(blockBytes);
            var high = BitConverter.ToUInt64(blockBytes.Slice(8));
            var block = PhysicalBlock.Create(((UInt128)low | ((UInt128)high << 64)));
        }

        [Benchmark]
        public void DecodeEndpoints()
        {
            var blocks = astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var low = BitConverter.ToUInt64(blockBytes);
            var high = BitConverter.ToUInt64(blockBytes.Slice(8));
            var block = PhysicalBlock.Create(((UInt128)low | ((UInt128)high << 64)));
            var ib = IntermediateBlock.UnpackIntermediateBlock(block);
        }

        [Benchmark]
        public void Partitioning()
        {
            var blocks = astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var low = BitConverter.ToUInt64(blockBytes);
            var high = BitConverter.ToUInt64(blockBytes.Slice(8));
            var block = PhysicalBlock.Create(((UInt128)low | ((UInt128)high << 64)));
            var ibOpt = IntermediateBlock.UnpackIntermediateBlock(block);
            if (ibOpt is not { } ib)
                throw new InvalidOperationException("Failed to unpack intermediate block");
            var logical = new LogicalBlock(Footprint.Get4x4(), in ib);
        }
    }

}
