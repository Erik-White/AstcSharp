using AstcEncoder;
using AstcSharp.IO;
using BenchmarkDotNet.Attributes;

namespace AstcSharp.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class ReferenceDecoderBenchmark
{
    private AstcFile? _astcFile;

    private static readonly AstcencSwizzle IdentitySwizzle = new()
    {
        r = AstcencSwz.AstcencSwzR,
        g = AstcencSwz.AstcencSwzG,
        b = AstcencSwz.AstcencSwzB,
        a = AstcencSwz.AstcencSwzA,
    };

    private AstcencContext _armLdrContext;
    private AstcencContext _armHdrContext;
    private byte[]? _armLdrOutput;
    private byte[]? _armHdrOutput;
    private byte[]? _armBlocksCopy;
    private byte[]? _astcSharpLdrOutput;
    private float[]? _astcSharpHdrOutput;

    [Params("rgba-4x4", "rgba-8x8", "footprint-4x4", "footprint-12x12")]
    public string FileName { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", FileName + ".astc"));
        var rawFile = File.ReadAllBytes(path);
        _astcFile = AstcFile.FromMemory(rawFile);

        var footprint = _astcFile.Footprint;
        int w = _astcFile.Width;
        int h = _astcFile.Height;
        int pixelCount = w * h;

        // Pre-allocate output buffers
        _armLdrOutput = new byte[pixelCount * 4];
        _armHdrOutput = new byte[pixelCount * 4 * sizeof(ushort)]; // FP16 = 2 bytes per channel
        _armBlocksCopy = _astcFile.Blocks.ToArray();
        _astcSharpLdrOutput = new byte[pixelCount * 4];
        _astcSharpHdrOutput = new float[pixelCount * 4];

        // Pre-allocate LDR context
        var error = Astcenc.AstcencConfigInit(
            AstcencProfile.AstcencPrfLdr,
            (uint)footprint.Width, (uint)footprint.Height, blockZ: 1,
            Astcenc.AstcencPreFastest,
            AstcencFlags.DecompressOnly,
            out var ldrConfig);
        ThrowOnError(error, "ConfigInit(LDR)");

        error = Astcenc.AstcencContextAlloc(ref ldrConfig, threadCount: 1, out _armLdrContext);
        ThrowOnError(error, "ContextAlloc(LDR)");

        // Pre-allocate HDR context
        error = Astcenc.AstcencConfigInit(
            AstcencProfile.AstcencPrfHdr,
            (uint)footprint.Width, (uint)footprint.Height, blockZ: 1,
            Astcenc.AstcencPreFastest,
            AstcencFlags.DecompressOnly,
            out var hdrConfig);
        ThrowOnError(error, "ConfigInit(HDR)");

        error = Astcenc.AstcencContextAlloc(ref hdrConfig, threadCount: 1, out _armHdrContext);
        ThrowOnError(error, "ContextAlloc(HDR)");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Astcenc.AstcencContextFree(_armLdrContext);
        Astcenc.AstcencContextFree(_armHdrContext);
    }

    [Benchmark]
    public bool AstcSharp_DecompressLdr()
    {
        var file = _astcFile!;
        return AstcDecoder.DecompressImage(file.Blocks, file.Width, file.Height, file.Footprint, _astcSharpLdrOutput);
    }

    [Benchmark]
    public bool AstcSharp_DecompressHdr()
    {
        var file = _astcFile!;
        return AstcDecoder.DecompressHdrImage(file.Blocks, file.Width, file.Height, file.Footprint, _astcSharpHdrOutput);
    }

    [Benchmark]
    public byte[] ArmReference_DecompressLdr()
    {
        var file = _astcFile!;
        var image = new AstcencImage
        {
            dimX = (uint)file.Width,
            dimY = (uint)file.Height,
            dimZ = 1,
            dataType = AstcencType.AstcencTypeU8,
            data = _armLdrOutput!,
        };

        var error = Astcenc.AstcencDecompressImage(_armLdrContext, _armBlocksCopy!, ref image, IdentitySwizzle, 0);
        ThrowOnError(error, "DecompressImage(LDR)");

        error = Astcenc.AstcencDecompressReset(_armLdrContext);
        ThrowOnError(error, "DecompressReset(LDR)");

        return _armLdrOutput!;
    }

    [Benchmark]
    public byte[] ArmReference_DecompressHdr()
    {
        var file = _astcFile!;
        var image = new AstcencImage
        {
            dimX = (uint)file.Width,
            dimY = (uint)file.Height,
            dimZ = 1,
            dataType = AstcencType.AstcencTypeF16,
            data = _armHdrOutput!,
        };

        var error = Astcenc.AstcencDecompressImage(_armHdrContext, _armBlocksCopy!, ref image, IdentitySwizzle, 0);
        ThrowOnError(error, "DecompressImage(HDR)");

        error = Astcenc.AstcencDecompressReset(_armHdrContext);
        ThrowOnError(error, "DecompressReset(HDR)");

        return _armHdrOutput!;
    }

    private static void ThrowOnError(AstcencError error, string operation)
    {
        if (error != AstcencError.AstcencSuccess)
        {
            var message = Astcenc.GetErrorString(error) ?? error.ToString();
            throw new InvalidOperationException($"ARM ASTC encoder {operation} failed: {message}");
        }
    }
}
