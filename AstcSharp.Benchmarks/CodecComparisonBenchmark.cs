using AstcEncoder;
using AstcSharp.Core;
using AstcSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Head-to-head decode and encode of AstcSharp vs the ARM reference codec (astcenc) over a mixed set
/// of fixtures spanning footprints (4×4 … 12×12) and content (RGB and RGBA). For each fixture a small
/// <see cref="TileSize"/>×<see cref="TileSize"/> tile is used — a crop is required because AstcSharp's
/// exhaustive per-block encode search makes a full image take seconds per iteration. Produces one
/// compact table: four methods (AstcSharp/ARM × decode/encode) across the image set.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class CodecComparisonBenchmark
{
    private const int TileSize = 32;

    private static readonly AstcencSwizzle IdentitySwizzle = new()
    {
        r = AstcencSwz.AstcencSwzR,
        g = AstcencSwz.AstcencSwzG,
        b = AstcencSwz.AstcencSwzB,
        a = AstcencSwz.AstcencSwzA,
    };

    [Params("rgb-4x4", "rgba-4x4", "rgba-8x8", "rgb-12x12")]
    public string Image { get; set; } = string.Empty;

    private Footprint footprint;
    private byte[] tilePixels = [];     // RGBA8 source for the encode benchmarks
    private byte[] tileBlocks = [];     // ASTC blocks for the decode benchmarks (ARM-encoded ground truth)
    private byte[] decodeOutput = [];   // reused RGBA8 decode target
    private AstcencContext armContext;

    [GlobalSetup]
    public void Setup()
    {
        string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", this.Image + ".astc"));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(path));
        this.footprint = file.Footprint;

        byte[] full = AstcDecoder.DecompressImage(file.Blocks, file.Width, file.Height, file.Footprint).ToArray();
        this.tilePixels = CropTopLeft(full, file.Width, TileSize, TileSize);

        int blocksWide = (TileSize + this.footprint.Width - 1) / this.footprint.Width;
        int blocksHigh = (TileSize + this.footprint.Height - 1) / this.footprint.Height;
        this.tileBlocks = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];
        this.decodeOutput = new byte[TileSize * TileSize * 4];

        AstcencError error = Astcenc.AstcencConfigInit(
            AstcencProfile.AstcencPrfLdr,
            (uint)this.footprint.Width, (uint)this.footprint.Height, blockZ: 1,
            Astcenc.AstcencPreMedium, flags: 0, out AstcencConfig config);
        ThrowOnError(error, "ConfigInit");
        error = Astcenc.AstcencContextAlloc(ref config, threadCount: 1, out this.armContext);
        ThrowOnError(error, "ContextAlloc");

        // Encode the tile with ARM once to get spec-legal blocks for the decode benchmarks to read.
        ArmEncode();
        Astcenc.AstcencCompressReset(this.armContext);
    }

    [GlobalCleanup]
    public void Cleanup() => Astcenc.AstcencContextFree(this.armContext);

    [Benchmark]
    public bool AstcSharp_Decode()
        => AstcDecoder.DecompressImage(this.tileBlocks, TileSize, TileSize, this.footprint, this.decodeOutput);

    [Benchmark]
    public byte[] Arm_Decode()
    {
        var image = new AstcencImage { dimX = TileSize, dimY = TileSize, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = this.decodeOutput };
        ThrowOnError(Astcenc.AstcencDecompressImage(this.armContext, this.tileBlocks, ref image, IdentitySwizzle, 0), "Decompress");
        ThrowOnError(Astcenc.AstcencDecompressReset(this.armContext), "DecompressReset");
        return this.decodeOutput;
    }

    [Benchmark]
    public int AstcSharp_Encode()
        => AstcEncoder.CompressImage(this.tilePixels, TileSize, TileSize, this.footprint).Length;

    [Benchmark]
    public int Arm_Encode()
    {
        ArmEncode();
        ThrowOnError(Astcenc.AstcencCompressReset(this.armContext), "CompressReset");
        return this.tileBlocks.Length;
    }

    private void ArmEncode()
    {
        var image = new AstcencImage { dimX = TileSize, dimY = TileSize, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = this.tilePixels };
        ThrowOnError(Astcenc.AstcencCompressImage(this.armContext, ref image, IdentitySwizzle, this.tileBlocks, 0), "Compress");
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
