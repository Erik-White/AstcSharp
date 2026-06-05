using AstcEncoder;
using AstcSharp.IO;
using BenchmarkDotNet.Attributes;

namespace AstcSharp.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class ReferenceDecoderBenchmark
{
    private AstcFile? _astcFile;

    private AstcencContext _armLdrContext;
    private AstcencContext _armHdrContext;
    private byte[]? _armLdrOutput;
    private byte[]? _armHdrOutput;
    private byte[]? _armBlocksCopy;

    // Reused streams so the AstcSharp benchmarks measure decode work, not allocation.
    private MemoryStream _astcSharpSource = null!;
    private MemoryStream _astcSharpSink = null!;

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
        _astcSharpSource = new MemoryStream(_armBlocksCopy);
        _astcSharpSink = new MemoryStream(pixelCount * 4 * sizeof(float));

        _armLdrContext = ArmCodec.CreateContext(
            footprint, AstcencProfile.AstcencPrfLdr, Astcenc.AstcencPreFastest, AstcencFlags.DecompressOnly);
        _armHdrContext = ArmCodec.CreateContext(
            footprint, AstcencProfile.AstcencPrfHdr, Astcenc.AstcencPreFastest, AstcencFlags.DecompressOnly);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Astcenc.AstcencContextFree(_armLdrContext);
        Astcenc.AstcencContextFree(_armHdrContext);
    }

    [Benchmark]
    public long AstcSharp_DecompressLdr()
    {
        var file = _astcFile!;
        _astcSharpSource.Position = 0;
        _astcSharpSink.SetLength(0);
        AstcDecoder.DecompressImage(_astcSharpSource, _astcSharpSink, file.Width, file.Height, file.Footprint);
        return _astcSharpSink.Length;
    }

    [Benchmark]
    public long AstcSharp_DecompressHdr()
    {
        var file = _astcFile!;
        _astcSharpSource.Position = 0;
        _astcSharpSink.SetLength(0);
        AstcDecoder.DecompressHdrImage(_astcSharpSource, _astcSharpSink, file.Width, file.Height, file.Footprint);
        return _astcSharpSink.Length;
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

        var error = Astcenc.AstcencDecompressImage(_armLdrContext, _armBlocksCopy!, ref image, ArmCodec.IdentitySwizzle, 0);
        ArmCodec.ThrowOnError(error, "DecompressImage(LDR)");

        error = Astcenc.AstcencDecompressReset(_armLdrContext);
        ArmCodec.ThrowOnError(error, "DecompressReset(LDR)");

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

        var error = Astcenc.AstcencDecompressImage(_armHdrContext, _armBlocksCopy!, ref image, ArmCodec.IdentitySwizzle, 0);
        ArmCodec.ThrowOnError(error, "DecompressImage(HDR)");

        error = Astcenc.AstcencDecompressReset(_armHdrContext);
        ArmCodec.ThrowOnError(error, "DecompressReset(HDR)");

        return _armHdrOutput!;
    }
}
