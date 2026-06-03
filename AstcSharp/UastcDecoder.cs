using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.Uastc;

namespace AstcSharp;

/// <summary>
/// Decodes UASTC (Universal ASTC) LDR texture data to uncompressed RGBA32. UASTC is a Basis
/// Universal encoding that is a constrained subset of LDR ASTC 4x4; blocks are always 4x4 and
/// 16 bytes. Sibling to <see cref="AstcDecoder"/> — standard ASTC decoding is unaffected.
/// </summary>
/// <remarks>
/// The decoder returns raw decoded values and does not apply an sRGB-to-linear transform; see
/// <see cref="LdrDecodeMode"/>. All 19 UASTC LDR modes are supported; a block using the reserved
/// mode produces the error colour (magenta) for that block.
/// </remarks>
public static class UastcDecoder
{
    private const int BlockSizeBytes = 16;
    private const int BlockDim = 4;
    private const int ChannelsPerPixel = 4;
    private const int DecodedBlockBytes = BlockDim * BlockDim * ChannelsPerPixel;

    /// <summary>
    /// Decompresses a single 16-byte UASTC block to a 4x4 RGBA32 image (64 bytes).
    /// </summary>
    /// <param name="blockData">The 16-byte UASTC block.</param>
    /// <param name="buffer">Output buffer, at least 64 bytes.</param>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    public static void DecompressBlock(ReadOnlySpan<byte> blockData, Span<byte> buffer, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        if (blockData.Length < BlockSizeBytes)
        {
            throw new ArgumentException($"UASTC block data must be at least {BlockSizeBytes} bytes.", nameof(blockData));
        }

        if (buffer.Length < DecodedBlockBytes)
        {
            throw new ArgumentException($"Output buffer must be at least {DecodedBlockBytes} bytes.", nameof(buffer));
        }

        DecodeBlock(blockData, buffer, mode);
    }

    /// <summary>
    /// Decompresses UASTC data to RGBA32 (4 bytes per pixel) into a newly allocated array.
    /// </summary>
    /// <param name="uastcData">The UASTC block data (16 bytes per 4x4 block, row-major).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    /// <returns>RGBA32 bytes (width * height * 4), or an empty span if the input is too small.</returns>
    public static Span<byte> DecompressImage(ReadOnlySpan<byte> uastcData, int width, int height, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / ChannelsPerPixel);

        byte[] imageBuffer = new byte[(int)(totalPixels * ChannelsPerPixel)];
        return DecompressImage(uastcData, width, height, imageBuffer, mode)
            ? imageBuffer
            : [];
    }

    /// <summary>
    /// Decompresses UASTC data to RGBA32 into a caller-provided buffer.
    /// </summary>
    /// <returns>True if the input was large enough and decoding ran; false otherwise.</returns>
    public static bool DecompressImage(ReadOnlySpan<byte> uastcData, int width, int height, Span<byte> imageBuffer, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        int blocksWide = (width + BlockDim - 1) / BlockDim;
        int blocksHigh = (height + BlockDim - 1) / BlockDim;

        long required = (long)blocksWide * blocksHigh * BlockSizeBytes;
        if (uastcData.Length < required)
        {
            return false;
        }

        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        Span<byte> blockPixels = stackalloc byte[DecodedBlockBytes];

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int blockIndex = (by * blocksWide) + bx;
                ReadOnlySpan<byte> block = uastcData.Slice(blockIndex * BlockSizeBytes, BlockSizeBytes);
                DecodeBlock(block, blockPixels, mode);

                BlockDestination dest = BlockImageWriter.ComputeBlockDestination(bx, by, footprint, width, height);
                BlockImageWriter.CopyBlockRect<byte>(
                    blockPixels, imageBuffer, BlockDim, dest.CopyWidth, dest.CopyHeight, dest.DstBaseX, dest.DstBaseY, width);
            }
        }

        return true;
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> blockPixels, LdrDecodeMode mode)
    {
        bool decoded = mode == LdrDecodeMode.Srgb
            ? UastcBlockDecoder.TryDecode<SrgbMode>(block, blockPixels)
            : UastcBlockDecoder.TryDecode<LinearMode>(block, blockPixels);

        if (!decoded)
        {
            BlockImageWriter.FillErrorColor(blockPixels);
        }
    }
}
