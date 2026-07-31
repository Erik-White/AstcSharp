using AstcEncoder;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Head-to-head decode and encode of AstcSharp vs the ARM reference codec (astcenc) over a mixed set
/// of fixtures spanning footprints (4×4 … 12×12) and content (RGB and RGBA). Each benchmark processes
/// the whole set in one call, so the report is four rows — {ARM, AstcSharp} × {decode, encode}. A
/// small <see cref="TileSize"/>×<see cref="TileSize"/> tile per fixture keeps each iteration tractable,
/// since AstcSharp's exhaustive per-block encode search makes a full image take seconds.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.Declared)]
public class CodecComparisonBenchmark
{
    private const string DecodeCategory = "Decode";
    private const string EncodeCategory = "Encode";

    private const int TileSize = 32;

    private static readonly string[] Fixtures = ["rgb-4x4", "rgba-4x4", "rgba-8x8", "rgb-12x12"];

    // One entry per fixture, holding everything both codecs need for that tile.
    private sealed class Image
    {
        public Footprint Footprint;
        public byte[] Pixels = [];        // RGBA8 source for the encode benchmarks
        public byte[] Blocks = [];        // ASTC blocks for the decode benchmarks (ARM-encoded ground truth)
        public byte[] DecodeOutput = [];  // reused RGBA8 decode target (ARM)
        public AstcencContext ArmContext; // footprint-specific, so one per image

        // Reused streams for the AstcSharp streaming codec, so its benchmarks measure work not allocation.
        public MemoryStream BlockSource = null!;
        public MemoryStream PixelSource = null!;
        public MemoryStream Sink = null!;
    }

    private Image[] images = [];

    [GlobalSetup]
    public void Setup()
    {
        this.images = new Image[Fixtures.Length];
        for (int i = 0; i < Fixtures.Length; i++)
        {
            string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", Fixtures[i] + ".astc"));
            AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(path));
            Footprint footprint = file.Footprint;

            byte[] full = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, footprint);
            int blocksWide = (TileSize + footprint.Width - 1) / footprint.Width;
            int blocksHigh = (TileSize + footprint.Height - 1) / footprint.Height;

            byte[] pixels = ImageHelper.CropTopLeft(full, file.Width, TileSize, TileSize);
            var image = new Image
            {
                Footprint = footprint,
                Pixels = pixels,
                Blocks = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes],
                DecodeOutput = new byte[TileSize * TileSize * 4],
                ArmContext = ArmCodec.CreateContext(footprint, AstcencProfile.AstcencPrfLdr, Astcenc.AstcencPreMedium, flags: 0),
                PixelSource = new MemoryStream(pixels),
                Sink = new MemoryStream(TileSize * TileSize * 4),
            };

            // Encode the tile with ARM once to get spec-legal blocks for the decode benchmarks to read.
            ArmEncode(image);
            Astcenc.AstcencCompressReset(image.ArmContext);
            image.BlockSource = new MemoryStream(image.Blocks);
            this.images[i] = image;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (Image image in this.images)
        {
            Astcenc.AstcencContextFree(image.ArmContext);
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory(DecodeCategory)]
    public void Arm_Decode()
    {
        foreach (Image image in this.images)
        {
            var output = new AstcencImage { dimX = TileSize, dimY = TileSize, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = image.DecodeOutput };
            ArmCodec.ThrowOnError(Astcenc.AstcencDecompressImage(image.ArmContext, image.Blocks, ref output, ArmCodec.IdentitySwizzle, 0), "Decompress");
            ArmCodec.ThrowOnError(Astcenc.AstcencDecompressReset(image.ArmContext), "DecompressReset");
        }
    }

    [Benchmark, BenchmarkCategory(DecodeCategory)]
    public void AstcSharp_Decode()
    {
        foreach (Image image in this.images)
        {
            image.BlockSource.Position = 0;
            image.Sink.SetLength(0);
            AstcDecoder.DecompressImage(image.BlockSource, image.Sink, TileSize, TileSize, image.Footprint);
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory(EncodeCategory)]
    public void Arm_Encode()
    {
        foreach (Image image in this.images)
        {
            ArmEncode(image);
            ArmCodec.ThrowOnError(Astcenc.AstcencCompressReset(image.ArmContext), "CompressReset");
        }
    }

    [Benchmark, BenchmarkCategory(EncodeCategory)]
    public void AstcSharp_Encode()
    {
        foreach (Image image in this.images)
        {
            image.PixelSource.Position = 0;
            image.Sink.SetLength(0);
            AstcEncoder.CompressImage(image.PixelSource, image.Sink, TileSize, TileSize, image.Footprint);
        }
    }

    private static void ArmEncode(Image image)
    {
        var input = new AstcencImage { dimX = TileSize, dimY = TileSize, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = image.Pixels };
        ArmCodec.ThrowOnError(Astcenc.AstcencCompressImage(image.ArmContext, ref input, ArmCodec.IdentitySwizzle, image.Blocks, 0), "Compress");
    }
}
