using System.Buffers.Binary;
using AstcSharp;
using AstcSharp.Core;

namespace AstcSharp.Tests.Utils;

/// <summary>
/// Test/benchmark adapter that drives the streaming <see cref="AstcDecoder"/> and
/// <see cref="AstcEncoder"/> APIs through in-memory streams and returns whole arrays, so existing
/// comparison logic can stay array-based. The library itself no longer exposes in-memory entry
/// points — this helper is the single place that bridges them for callers that need the full
/// result materialised. HDR float/Half outputs are read back from the little-endian byte stream
/// the decoder emits.
/// </summary>
internal static class StreamCodec
{
    public static byte[] DecodeLdr(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        using var source = new MemoryStream(astcData.ToArray());
        using var destination = new MemoryStream();
        AstcDecoder.DecompressImage(source, destination, width, height, footprint, mode);
        return destination.ToArray();
    }

    public static byte[] DecodeLdr(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint, LdrDecodeMode mode = LdrDecodeMode.Linear)
        => DecodeLdr(astcData, width, height, Footprint.FromFootprintType(footprint), mode);

    public static float[] DecodeHdr(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        using var source = new MemoryStream(astcData.ToArray());
        using var destination = new MemoryStream();
        AstcDecoder.DecompressHdrImage(source, destination, width, height, footprint);
        return ToFloats(destination.GetBuffer(), (int)destination.Length);
    }

    public static float[] DecodeHdr(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
        => DecodeHdr(astcData, width, height, Footprint.FromFootprintType(footprint));

    public static Half[] DecodeHdrHalf(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        using var source = new MemoryStream(astcData.ToArray());
        using var destination = new MemoryStream();
        AstcDecoder.DecompressHdrImageHalf(source, destination, width, height, footprint);
        return ToHalves(destination.GetBuffer(), (int)destination.Length);
    }

    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, Footprint footprint)
    {
        using var source = new MemoryStream(rgba.ToArray());
        using var destination = new MemoryStream();
        AstcEncoder.CompressImage(source, destination, width, height, footprint);
        return destination.ToArray();
    }

    /// <summary>
    /// Decodes raw UASTC blocks (always 4x4) to RGBA32 bytes.
    /// </summary>
    public static byte[] DecodeUastc(ReadOnlySpan<byte> uastcData, int width, int height, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        using var source = new MemoryStream(uastcData.ToArray());
        using var destination = new MemoryStream();
        UastcDecoder.DecompressImage(source, destination, width, height, mode);
        return destination.ToArray();
    }

    /// <summary>
    /// Reads the first <paramref name="byteCount"/> bytes of <paramref name="bytes"/> as
    /// little-endian <see cref="float"/> channels — the inverse of the decoder's float output layout.
    /// </summary>
    public static float[] ToFloats(byte[] bytes, int byteCount)
    {
        float[] values = new float[byteCount / sizeof(float)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
        }

        return values;
    }

    /// <summary>
    /// Reads the first <paramref name="byteCount"/> bytes of <paramref name="bytes"/> as
    /// little-endian <see cref="Half"/> channels — the inverse of the decoder's FP16 output layout.
    /// </summary>
    public static Half[] ToHalves(byte[] bytes, int byteCount)
    {
        Half[] values = new Half[byteCount / sizeof(ushort)];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(i * sizeof(ushort), sizeof(ushort)));
        }

        return values;
    }
}
