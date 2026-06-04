using AstcEncoder;
using AstcSharp.Core;
using AstcSharp.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Compares AstcSharp's LDR encoder against the ARM reference encoder (astcenc, "medium" quality) on
/// the same real image data, for speed. Both encode a small <see cref="TileSize"/>×<see cref="TileSize"/>
/// crop of a decoded fixture per iteration; a crop is used because AstcSharp's exhaustive per-block
/// search makes a full image take tens of seconds per iteration. ARM is a mature production encoder,
/// so it will be far faster — this benchmark quantifies that gap, not parity.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class EncoderComparisonBenchmark
{
    private const int TileSize = 32;

    private static readonly AstcencSwizzle IdentitySwizzle = new()
    {
        r = AstcencSwz.AstcencSwzR,
        g = AstcencSwz.AstcencSwzG,
        b = AstcencSwz.AstcencSwzB,
        a = AstcencSwz.AstcencSwzA,
    };

    private byte[] pixels = [];
    private Footprint footprint;

    private AstcencContext armContext;
    private byte[] armOutput = [];

    [GlobalSetup]
    public void Setup()
    {
        string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", "rgba-4x4.astc"));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(path));
        this.footprint = file.Footprint;

        byte[] full = AstcDecoder.DecompressImage(file.Blocks, file.Width, file.Height, file.Footprint).ToArray();
        this.pixels = CropTopLeft(full, file.Width, TileSize, TileSize);

        int blocksWide = (TileSize + this.footprint.Width - 1) / this.footprint.Width;
        int blocksHigh = (TileSize + this.footprint.Height - 1) / this.footprint.Height;
        this.armOutput = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];

        // Allocate the ARM encode context once (it is expensive); reuse it across iterations with a
        // reset between compresses, mirroring ReferenceDecoderBenchmark.
        AstcencError error = Astcenc.AstcencConfigInit(
            AstcencProfile.AstcencPrfLdr,
            (uint)this.footprint.Width, (uint)this.footprint.Height, blockZ: 1,
            Astcenc.AstcencPreMedium,
            flags: 0,
            out AstcencConfig config);
        ThrowOnError(error, "ConfigInit(LDR)");

        error = Astcenc.AstcencContextAlloc(ref config, threadCount: 1, out this.armContext);
        ThrowOnError(error, "ContextAlloc(LDR)");
    }

    [GlobalCleanup]
    public void Cleanup() => Astcenc.AstcencContextFree(this.armContext);

    [Benchmark(Baseline = true)]
    public int AstcSharp_CompressLdr()
        => AstcEncoder.CompressImage(this.pixels, TileSize, TileSize, this.footprint).Length;

    [Benchmark]
    public int ArmReference_CompressLdr()
    {
        var image = new AstcencImage
        {
            dimX = TileSize,
            dimY = TileSize,
            dimZ = 1,
            dataType = AstcencType.AstcencTypeU8,
            data = this.pixels,
        };

        AstcencError error = Astcenc.AstcencCompressImage(this.armContext, ref image, IdentitySwizzle, this.armOutput, 0);
        ThrowOnError(error, "CompressImage(LDR)");

        error = Astcenc.AstcencCompressReset(this.armContext);
        ThrowOnError(error, "CompressReset(LDR)");

        return this.armOutput.Length;
    }

    private static byte[] CropTopLeft(byte[] source, int sourceWidth, int cropWidth, int cropHeight)
    {
        const int bpp = 4;
        byte[] crop = new byte[cropWidth * cropHeight * bpp];
        for (int y = 0; y < cropHeight; y++)
        {
            int srcRow = y * sourceWidth * bpp;
            int dstRow = y * cropWidth * bpp;
            source.AsSpan(srcRow, cropWidth * bpp).CopyTo(crop.AsSpan(dstRow, cropWidth * bpp));
        }

        return crop;
    }

    private static void ThrowOnError(AstcencError error, string operation)
    {
        if (error != AstcencError.AstcencSuccess)
        {
            string message = Astcenc.GetErrorString(error) ?? error.ToString();
            throw new InvalidOperationException($"ARM ASTC encoder {operation} failed: {message}");
        }
    }
}
