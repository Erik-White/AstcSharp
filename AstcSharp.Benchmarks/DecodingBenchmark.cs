using BenchmarkDotNet.Attributes;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;

namespace AstcSharp.Benchmarks
{
    [MemoryDiagnoser]
    public class DecodingBenchmark
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
        public bool ParseBlock()
        {
            var blocks = _astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var low = BitConverter.ToUInt64(blockBytes);
            var high = BitConverter.ToUInt64(blockBytes.Slice(8));
            var phyiscalBlock = PhysicalBlock.Create((UInt128)low | ((UInt128)high << 64));

            return !phyiscalBlock.IsIllegalEncoding;
        }

        [Benchmark]
        public bool DecodeEndpoints()
        {
            var blocks = _astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var low = BitConverter.ToUInt64(blockBytes);
            var high = BitConverter.ToUInt64(blockBytes.Slice(8));
            var physicalBlock = PhysicalBlock.Create((UInt128)low | ((UInt128)high << 64));

            var blockData = IntermediateBlock.UnpackIntermediateBlock(physicalBlock);

            return blockData is not null;
        }

        [Benchmark]
        public bool Partitioning()
        {
            var blocks = _astcFile!.Blocks;
            Span<byte> blockBytes = stackalloc byte[16];
            blocks.Slice(0, 16).CopyTo(blockBytes);
            var low = BitConverter.ToUInt64(blockBytes);
            var high = BitConverter.ToUInt64(blockBytes.Slice(8));
            var physicalBlock = PhysicalBlock.Create(((UInt128)low | ((UInt128)high << 64)));
            var intermediateBlockData = IntermediateBlock.UnpackIntermediateBlock(physicalBlock);
            var logicalBlock = intermediateBlockData is not null
                ? new LogicalBlock(Footprint.Get4x4(), intermediateBlockData.Value)
                : throw new InvalidOperationException("Failed to unpack intermediate block");

            return logicalBlock is not null;
        }
    }

}
