using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Reference.Tests.Utils;
using AstcSharp.Tests.Utils;
using AwesomeAssertions;

namespace AstcSharp.Reference.Tests;

/// <summary>
/// Cross-decoder validity tests for <see cref="AstcEncoder"/>: blocks we produce must be
/// spec-legal, i.e. ARM's reference decoder must read them back to the original image (within the
/// ±1 UNORM8 conformance tolerance). This is the key guard that the encoded bitstream is correct,
/// not merely self-consistent with our own decoder.
/// </summary>
public class EncoderReferenceTests
{
    private const int Ldr8BitTolerance = 1;

    // The encoder runs a full per-block search (~1 ms/block), so re-encoding a whole 256×256 fixture
    // costs seconds. A 64×64 crop still covers hundreds of varied real blocks per fixture, and 64 is
    // not a multiple of the 6/12 footprints so edge blocks are exercised too.
    private const int CropSize = 64;

    public static TheoryData<FootprintType> AllFootprintTypes =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x4, FootprintType.Footprint5x5,
        FootprintType.Footprint6x5, FootprintType.Footprint6x6, FootprintType.Footprint8x5,
        FootprintType.Footprint8x6, FootprintType.Footprint8x8, FootprintType.Footprint10x5,
        FootprintType.Footprint10x6, FootprintType.Footprint10x8, FootprintType.Footprint10x10,
        FootprintType.Footprint12x10, FootprintType.Footprint12x12,
    ];

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedVoidExtent_DecodesUnderArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX;
        int height = blockY;

        byte[] pixels = TestImages.Solid(width, height, 0x80, 0x40, 0xC0, 0xFF);
        byte[] encoded = StreamCodec.Encode(pixels, width, height, footprint);

        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, width, height, blockX, blockY);

        CompareRgba8(armDecoded, pixels, $"VoidExtent_{footprintType}");
    }

    // Footprints large enough to host distinct colour regions, where the encoder may pick a
    // multi-partition encoding.
    public static TheoryData<FootprintType> PartitionableFootprints =>
    [
        FootprintType.Footprint6x6, FootprintType.Footprint8x6, FootprintType.Footprint8x8,
        FootprintType.Footprint10x8, FootprintType.Footprint10x10, FootprintType.Footprint12x12,
    ];

    [Theory]
    [MemberData(nameof(PartitionableFootprints))]
    public void EncodedTwoRegion_DecodesUnderArmReference(FootprintType footprintType)
    {
        // Two-region content the encoder may encode with multiple partitions (spec §C.2.21). The
        // resulting block — partition seed, shared CEM, and concatenated per-partition colour
        // values — must be spec-legal: ARM's decoder must read it back in agreement with ours.
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        byte[] pixels = TestImages.TwoRegion(width, height);

        byte[] encoded = StreamCodec.Encode(pixels, width, height, footprint);
        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, width, height, blockX, blockY);
        Span<byte> ourDecoded = StreamCodec.DecodeLdr(encoded, width, height, footprint);

        CompareRgba8(armDecoded, ourDecoded.ToArray(), $"TwoRegion_{footprintType}");
    }

    [Fact]
    public void EncodedMultiPartition_DecodesUnderArmReference()
    {
        // Four saturated quadrant colours drive the encoder to a multi-partition shared-CEM block
        // (the path enabled by lifting the 2-partition cap). Its bitstream layout — shared-CEM
        // marker, colour values concatenated across subsets — is what we must prove spec-legal:
        // ARM's decoder reads it back in agreement with ours. We assert it really is multi-partition
        // (not a 1-partition fallback) so the cross-check exercises that layout; the exact count is a
        // search-tuning detail, not a contract.
        var footprintType = FootprintType.Footprint12x12;
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        byte[] pixels = TestImages.FourQuadrant(blockX, blockY);

        byte[] encoded = StreamCodec.Encode(pixels, blockX, blockY, footprint);

        UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        info.PartitionCount.Should().BeGreaterThan(1, because: "the four-quadrant block should encode with multiple partitions");

        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, blockX, blockY, blockX, blockY);
        Span<byte> ourDecoded = StreamCodec.DecodeLdr(encoded, blockX, blockY, footprint);

        CompareRgba8(armDecoded, ourDecoded.ToArray(), "MultiPartition");
    }

    [Fact]
    public void EncodedDualPlane_DecodesUnderArmReference()
    {
        // Anti-correlated RGB/alpha drives the encoder to a single-partition dual-plane block
        // (spec §C.2.20). Its bitstream — the dual-plane block mode, the 2-bit colour-component
        // selector in the high bits, and the two interleaved weight planes — must be spec-legal:
        // ARM's decoder reads it in agreement with ours. Assert dual-plane was actually selected.
        var footprintType = FootprintType.Footprint6x6;
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        byte[] pixels = TestImages.DecorrelatedAlpha(blockX, blockY);

        byte[] encoded = StreamCodec.Encode(pixels, blockX, blockY, footprint);

        UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        info.DualPlane.Enabled.Should().BeTrue(because: "anti-correlated RGB/alpha should encode as a dual-plane block");

        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, blockX, blockY, blockX, blockY);
        Span<byte> ourDecoded = StreamCodec.DecodeLdr(encoded, blockX, blockY, footprint);

        CompareRgba8(armDecoded, ourDecoded.ToArray(), "DualPlane");
    }

    [Theory]
    [InlineData(0, 0, 0, 255)]
    [InlineData(255, 255, 255, 255)]
    [InlineData(200, 100, 50, 128)]
    public void EncodedVoidExtent_VariousColors_DecodeUnderArmReference(byte r, byte g, byte b, byte a)
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        int width = footprint.Width;
        int height = footprint.Height;

        byte[] pixels = TestImages.Solid(width, height, r, g, b, a);
        byte[] encoded = StreamCodec.Encode(pixels, width, height, footprint);

        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, width, height, 6, 6);

        CompareRgba8(armDecoded, pixels, $"VoidExtentColor_{r}_{g}_{b}_{a}");
    }

    /// <summary>
    /// Content patterns that exercise the different endpoint-mode choices the encoder makes.
    /// </summary>
    public enum Content
    {
        /// <summary>
        /// Chromatic RGB gradient, opaque — exercises the RGB endpoint modes.
        /// </summary>
        Color,

        /// <summary>
        /// Grey ramp, opaque — exercises the luminance endpoint modes.
        /// </summary>
        Grayscale,
    }

    public static TheoryData<FootprintType, Content> FootprintsByContent
    {
        get
        {
            var data = new TheoryData<FootprintType, Content>();
            foreach (FootprintType footprint in EnumerateFootprints())
            {
                data.Add(footprint, Content.Color);
                data.Add(footprint, Content.Grayscale);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FootprintsByContent))]
    public void Encoded_DecodesUnderArmReference(FootprintType footprintType, Content content)
    {
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        byte[] pixels = ContentImage(content, width, height);

        byte[] encoded = StreamCodec.Encode(pixels, width, height, footprint);

        // Our blocks must be spec-legal: ARM's decoder reads them, and its reconstruction must
        // agree with ours (both decode the same legal bitstream).
        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, width, height, blockX, blockY);
        Span<byte> ourDecoded = StreamCodec.DecodeLdr(encoded, width, height, footprint);

        CompareRgba8(armDecoded, ourDecoded.ToArray(), $"{content}_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedGradient_QualityWithinMarginOfReferenceEncoder(FootprintType footprintType)
    {
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        byte[] pixels = TestImages.ChromaticGradient(width, height);

        // Our encoder, decoded by our decoder.
        byte[] ourEncoded = StreamCodec.Encode(pixels, width, height, footprint);
        Span<byte> ourDecoded = StreamCodec.DecodeLdr(ourEncoded, width, height, footprint);
        double ourPsnr = Psnr(pixels, ourDecoded);

        // ARM's encoder, decoded by ARM's decoder.
        byte[] armEncoded = ReferenceDecoder.CompressLdr(pixels, width, height, blockX, blockY);
        byte[] armDecoded = ReferenceDecoder.DecompressLdr(armEncoded, width, height, blockX, blockY);
        double armPsnr = Psnr(pixels, armDecoded);

        // The encoder targets quality within 3 dB of the reference, not bit parity.
        ourPsnr.Should().BeGreaterThanOrEqualTo(
            armPsnr - 3.0,
            because: $"[{footprintType}] our PSNR {ourPsnr:F2} dB should be within 3 dB of ARM's {armPsnr:F2} dB");
    }

    // A representative subset spanning the footprint range and both opaque/alpha content: the
    // smallest and largest RGB footprints and the smallest and largest RGBA footprints.
    [Theory]
    [InlineData("rgb-4x4")]
    [InlineData("rgb-12x12")]
    [InlineData("rgba-4x4")]
    [InlineData("rgba-8x8")]
    public void ReencodedRealImage_DecodesUnderArmReference(string basename)
    {
        // Decode a real multi-block fixture to RGBA8, re-encode a crop with our encoder, and confirm
        // the re-encoded bitstream is spec-legal: ARM's decoder must read every block back in
        // agreement with ours (±1 UNORM8). This is the real-content counterpart to the synthetic
        // single-/two-region cases above.
        var filePath = Path.Combine("TestData", "Input", "Astc", basename + ".astc");
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(filePath));
        Footprint footprint = file.Footprint;
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprint.Type);

        byte[] decoded = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, footprint);
        byte[] source = TestImage.CropTopLeft(decoded, file.Width, CropSize, CropSize);

        byte[] reencoded = StreamCodec.Encode(source, CropSize, CropSize, footprint);
        byte[] armDecoded = ReferenceDecoder.DecompressLdr(reencoded, CropSize, CropSize, blockX, blockY);
        Span<byte> ourDecoded = StreamCodec.DecodeLdr(reencoded, CropSize, CropSize, footprint);

        CompareRgba8(armDecoded, ourDecoded.ToArray(), $"Reencode_{basename}");
    }

    private static byte[] ContentImage(Content content, int width, int height) => content switch
    {
        Content.Color => TestImages.ChromaticGradient(width, height),
        Content.Grayscale => TestImages.GrayscaleRamp(width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, null),
    };

    private static IEnumerable<FootprintType> EnumerateFootprints() => Enum.GetValues<FootprintType>();

    private static double Psnr(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded)
    {
        double sumSquaredError = 0;
        for (int i = 0; i < original.Length; i++)
        {
            int diff = decoded[i] - original[i];
            sumSquaredError += (double)diff * diff;
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        double meanSquaredError = sumSquaredError / original.Length;
        return 10.0 * Math.Log10((255.0 * 255.0) / meanSquaredError);
    }

    private static void CompareRgba8(byte[] actual, byte[] expected, string label)
    {
        actual.Length.Should().Be(expected.Length, because: $"decoded size should match for {label}");

        for (int i = 0; i < expected.Length; i++)
        {
            int diff = Math.Abs(actual[i] - expected[i]);
            diff.Should().BeLessThanOrEqualTo(
                Ldr8BitTolerance,
                because: $"[{label}] channel {i} (pixel {i / 4}) should be within ±{Ldr8BitTolerance}: ARM={actual[i]} expected={expected[i]}");
        }
    }
}
