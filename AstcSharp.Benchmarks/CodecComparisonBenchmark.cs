using AstcEncoder;
using AstcSharp.Core;
using AstcSharp.IO;
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

    private static readonly AstcencSwizzle IdentitySwizzle = new()
    {
        r = AstcencSwz.AstcencSwzR,
        g = AstcencSwz.AstcencSwzG,
        b = AstcencSwz.AstcencSwzB,
        a = AstcencSwz.AstcencSwzA,
    };

    // One entry per fixture, holding everything both codecs need for that tile.
    private sealed class Image
    {
        public Footprint Footprint;
        public byte[] Pixels = [];        // RGBA8 source for the encode benchmarks
        public byte[] Blocks = [];        // ASTC blocks for the decode benchmarks (ARM-encoded ground truth)
        public byte[] DecodeOutput = [];  // reused RGBA8 decode target
        public AstcencContext ArmContext; // footprint-specific, so one per image
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

            byte[] full = AstcDecoder.DecompressImage(file.Blocks, file.Width, file.Height, footprint).ToArray();
            int blocksWide = (TileSize + footprint.Width - 1) / footprint.Width;
            int blocksHigh = (TileSize + footprint.Height - 1) / footprint.Height;

            var image = new Image
            {
                Footprint = footprint,
                Pixels = CropTopLeft(full, file.Width, TileSize, TileSize),
                Blocks = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes],
                DecodeOutput = new byte[TileSize * TileSize * 4],
                ArmContext = CreateArmContext(footprint),
            };

            // Encode the tile with ARM once to get spec-legal blocks for the decode benchmarks to read.
            ArmEncode(image);
            Astcenc.AstcencCompressReset(image.ArmContext);
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
            ThrowOnError(Astcenc.AstcencDecompressImage(image.ArmContext, image.Blocks, ref output, IdentitySwizzle, 0), "Decompress");
            ThrowOnError(Astcenc.AstcencDecompressReset(image.ArmContext), "DecompressReset");
        }
    }

    [Benchmark, BenchmarkCategory(DecodeCategory)]
    public void AstcSharp_Decode()
    {
        foreach (Image image in this.images)
        {
            AstcDecoder.DecompressImage(image.Blocks, TileSize, TileSize, image.Footprint, image.DecodeOutput);
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory(EncodeCategory)]
    public void Arm_Encode()
    {
        foreach (Image image in this.images)
        {
            ArmEncode(image);
            ThrowOnError(Astcenc.AstcencCompressReset(image.ArmContext), "CompressReset");
        }
    }

    [Benchmark, BenchmarkCategory(EncodeCategory)]
    public void AstcSharp_Encode()
    {
        foreach (Image image in this.images)
        {
            AstcEncoder.CompressImage(image.Pixels, TileSize, TileSize, image.Footprint);
        }
    }

    private static AstcencContext CreateArmContext(Footprint footprint)
    {
        AstcencError error = Astcenc.AstcencConfigInit(
            AstcencProfile.AstcencPrfLdr,
            (uint)footprint.Width, (uint)footprint.Height, blockZ: 1,
            Astcenc.AstcencPreMedium, flags: 0, out AstcencConfig config);
        ThrowOnError(error, "ConfigInit");
        error = Astcenc.AstcencContextAlloc(ref config, threadCount: 1, out AstcencContext context);
        ThrowOnError(error, "ContextAlloc");
        return context;
    }

    private static void ArmEncode(Image image)
    {
        var input = new AstcencImage { dimX = TileSize, dimY = TileSize, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = image.Pixels };
        ThrowOnError(Astcenc.AstcencCompressImage(image.ArmContext, ref input, IdentitySwizzle, image.Blocks, 0), "Compress");
    }

    private static byte[] CropTopLeft(byte[] source, int sourceWidth, int cropWidth, int cropHeight)
    {
        const int bpp = 4;
        byte[] crop = new byte[cropWidth * cropHeight * bpp];
        for (int y = 0; y < cropHeight; y++)
        {
            source.AsSpan(y * sourceWidth * bpp, cropWidth * bpp).CopyTo(crop.AsSpan(y * cropWidth * bpp, cropWidth * bpp));
        }

        return crop;
    }

    private static void ThrowOnError(AstcencError error, string operation)
    {
        if (error != AstcencError.AstcencSuccess)
        {
            throw new InvalidOperationException($"ARM ASTC {operation} failed: {Astcenc.GetErrorString(error) ?? error.ToString()}");
        }
    }
}
