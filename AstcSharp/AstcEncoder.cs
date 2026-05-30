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
/// Constant-colour blocks are encoded as void-extent blocks (spec §C.2.23). Blocks whose texels
/// are not all identical are not yet supported and throw <see cref="NotSupportedException"/>.
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
    /// Encodes a single LDR block at (<paramref name="blockX"/>, <paramref name="blockY"/>).
    /// Only constant-colour blocks are supported, emitted as void-extent blocks (spec §C.2.23).
    /// </summary>
    private static UInt128 EncodeLdrBlock(ReadOnlySpan<byte> rgba, int width, int height, Footprint footprint, int blockX, int blockY)
    {
        int baseX = blockX * footprint.Width;
        int baseY = blockY * footprint.Height;

        // The block's first in-image texel sets the candidate constant colour; edge blocks clip to
        // the image bounds (the decoder ignores texels outside the image).
        int firstOffset = ((baseY * width) + baseX) * BlockInfo.ChannelsPerPixel;
        byte r = rgba[firstOffset];
        byte g = rgba[firstOffset + 1];
        byte b = rgba[firstOffset + 2];
        byte a = rgba[firstOffset + 3];

        if (!BlockIsConstant(rgba, width, height, footprint, baseX, baseY, r, g, b, a))
        {
            throw new NotSupportedException(
                "AstcEncoder currently supports only constant-colour blocks (void-extent). General block encoding is not yet implemented.");
        }

        return VoidExtentEncoder.EncodeLdr(r, g, b, a);
    }

    private static bool BlockIsConstant(
        ReadOnlySpan<byte> rgba, int width, int height, Footprint footprint, int baseX, int baseY, byte r, byte g, byte b, byte a)
    {
        int maxY = Math.Min(baseY + footprint.Height, height);
        int maxX = Math.Min(baseX + footprint.Width, width);

        for (int y = baseY; y < maxY; y++)
        {
            for (int x = baseX; x < maxX; x++)
            {
                int offset = ((y * width) + x) * BlockInfo.ChannelsPerPixel;
                if (rgba[offset] != r || rgba[offset + 1] != g || rgba[offset + 2] != b || rgba[offset + 3] != a)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
