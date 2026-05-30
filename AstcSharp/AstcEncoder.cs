using System.Buffers.Binary;
using AstcSharp.Core;
using AstcSharp.Encoding;
using AstcSharp.IO;

namespace AstcSharp;

/// <summary>
/// Encodes uncompressed RGBA pixel data into ASTC-compressed blocks.
/// </summary>
/// <remarks>
/// <para>
/// The encoder is correctness-first: it produces spec-legal blocks that both this library's
/// decoder and conformant decoders (e.g. ARM's <c>astcenc</c>) read back to the original image
/// within ASTC's lossy tolerance. It does not target the rate-distortion quality or speed of a
/// production encoder.
/// </para>
/// <para>
/// Constant-colour blocks are encoded as void-extent blocks (spec §C.2.23);
/// other blocks use a single-partition, RGBA-direct encoding (mode 12) with a fitted weight grid
/// (decimated below the footprint size as needed), so every 2D footprint is supported.
/// </para>
/// </remarks>
public static class AstcEncoder
{
    /// <summary>
    /// Compresses an RGBA32 LDR image into ASTC blocks for the given footprint.
    /// </summary>
    /// <param name="rgba">Source pixels, 4 bytes per pixel in R, G, B, A order, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint to encode with.</param>
    /// <returns>The ASTC block stream (16 bytes per block, row-major block order).</returns>
    /// <exception cref="NotSupportedException">A block's texels are not all identical (general block encoding is not yet implemented).</exception>
    public static byte[] CompressImage(ReadOnlySpan<byte> rgba, int width, int height, Footprint footprint)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        long requiredBytes = totalPixels * BlockInfo.ChannelsPerPixel;
        ArgumentOutOfRangeException.ThrowIfLessThan(rgba.Length, requiredBytes);

        int blocksWide = (width + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
        byte[] output = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];

        int blockIndex = 0;
        for (int blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (int blockX = 0; blockX < blocksWide; blockX++)
            {
                UInt128 block = EncodeLdrBlock(rgba, width, height, footprint, blockX, blockY);
                BinaryPrimitives.WriteUInt128LittleEndian(
                    output.AsSpan(blockIndex * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes), block);
                blockIndex++;
            }
        }

        return output;
    }

    /// <summary>
    /// Compresses an RGBA32 LDR image and prepends the 16-byte <c>.astc</c> file header.
    /// </summary>
    public static byte[] CompressToAstcFile(ReadOnlySpan<byte> rgba, int width, int height, Footprint footprint)
    {
        byte[] blocks = CompressImage(rgba, width, height, footprint);

        byte[] output = new byte[AstcFileHeader.SizeInBytes + blocks.Length];
        var header = new AstcFileHeader(
            (byte)footprint.Width, (byte)footprint.Height, BlockDepth: 1, width, height, ImageDepth: 1);
        header.WriteTo(output);
        blocks.CopyTo(output.AsSpan(AstcFileHeader.SizeInBytes));

        return output;
    }

    /// <summary>
    /// Encodes a single LDR block at (<paramref name="blockX"/>, <paramref name="blockY"/>):
    /// a void-extent block when all texels are identical, otherwise a single-partition,
    /// RGBA-direct block whose weight grid is fitted (with decimation as needed) to the texels.
    /// </summary>
    private static UInt128 EncodeLdrBlock(ReadOnlySpan<byte> rgba, int width, int height, Footprint footprint, int blockX, int blockY)
    {
        Span<RgbaColor> texels = stackalloc RgbaColor[footprint.PixelCount];
        GatherBlockTexels(rgba, width, height, footprint, blockX, blockY, texels);

        if (IsConstant(texels, out RgbaColor constant))
        {
            return VoidExtentEncoder.EncodeLdr(constant.R, constant.G, constant.B, constant.A);
        }

        return LdrBlockEncoder.Encode(texels, footprint);
    }

    /// <summary>
    /// Copies a footprint-sized block of texels from the image into <paramref name="texels"/> in
    /// raster order. At right/bottom edges where the footprint overhangs the image, the nearest
    /// in-image texel is clamped into the padding positions — the decoder discards those texels,
    /// so any in-range fill is valid, and clamping keeps the endpoint fit representative.
    /// </summary>
    private static void GatherBlockTexels(
        ReadOnlySpan<byte> rgba, int width, int height, Footprint footprint, int blockX, int blockY, Span<RgbaColor> texels)
    {
        int baseX = blockX * footprint.Width;
        int baseY = blockY * footprint.Height;

        for (int y = 0; y < footprint.Height; y++)
        {
            int srcY = Math.Min(baseY + y, height - 1);
            for (int x = 0; x < footprint.Width; x++)
            {
                int srcX = Math.Min(baseX + x, width - 1);
                int offset = ((srcY * width) + srcX) * BlockInfo.ChannelsPerPixel;
                texels[(y * footprint.Width) + x] = new RgbaColor(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
            }
        }
    }

    private static bool IsConstant(ReadOnlySpan<RgbaColor> texels, out RgbaColor constant)
    {
        constant = texels[0];
        for (int i = 1; i < texels.Length; i++)
        {
            if (texels[i] != constant)
            {
                return false;
            }
        }

        return true;
    }
}
