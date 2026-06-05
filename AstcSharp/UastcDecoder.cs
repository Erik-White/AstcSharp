using System.Buffers;
using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.Uastc;

namespace AstcSharp;

/// <summary>
/// Decodes UASTC (Universal ASTC) LDR texture data to uncompressed RGBA32, streaming from a source
/// <see cref="Stream"/> of UASTC blocks to a destination <see cref="Stream"/> of pixels one
/// block-row band at a time. UASTC is a Basis Universal encoding that is a constrained subset of
/// LDR ASTC 4x4; blocks are always 4x4 and 16 bytes. Sibling to <see cref="AstcDecoder"/> —
/// standard ASTC decoding is unaffected.
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
    /// Decodes UASTC blocks read from <paramref name="source"/> and writes the RGBA32 result to
    /// <paramref name="destination"/>, one block-row band at a time (UASTC blocks are always 4x4).
    /// Only a single band of compressed blocks and decoded pixels is held in memory, so peak usage
    /// is independent of the image height.
    /// </summary>
    /// <param name="source">The stream containing UASTC block data (16 bytes per 4x4 block, row-major).</param>
    /// <param name="destination">The stream to write RGBA32 pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer blocks than the image requires.
    /// </exception>
    public static void DecompressImage(Stream source, Stream destination, int width, int height, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateArgs(width, height);

        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        int blocksWide = footprint.BlocksWide(width);
        int blocksHigh = footprint.BlocksHigh(height);
        int bandBlockBytes = blocksWide * BlockSizeBytes;
        int bandPixelBytes = BlockDim * width * ChannelsPerPixel;

        byte[] bandBlocks = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        byte[] bandPixels = ArrayPool<byte>.Shared.Rent(bandPixelBytes);
        byte[] blockPixels = ArrayPool<byte>.Shared.Rent(DecodedBlockBytes);
        try
        {
            for (int by = 0; by < blocksHigh; by++)
            {
                source.ReadExactly(bandBlocks.AsSpan(0, bandBlockBytes));

                int bandHeight = Math.Min(BlockDim, height - (by * BlockDim));
                DecodeBandRow(bandBlocks, blocksWide, width, bandHeight, mode, footprint, bandPixels, blockPixels);

                int validBytes = bandHeight * width * ChannelsPerPixel;
                destination.Write(bandPixels, 0, validBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bandBlocks);
            ArrayPool<byte>.Shared.Return(bandPixels);
            ArrayPool<byte>.Shared.Return(blockPixels);
        }
    }

    /// <summary>
    /// Asynchronously decodes UASTC blocks read from <paramref name="source"/> and writes the
    /// RGBA32 result to <paramref name="destination"/>, one block-row band at a time. Only a
    /// single band is held in memory.
    /// </summary>
    /// <param name="source">The stream containing UASTC block data (16 bytes per 4x4 block, row-major).</param>
    /// <param name="destination">The stream to write RGBA32 pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer blocks than the image requires.
    /// </exception>
    public static async Task DecompressImageAsync(
        Stream source, Stream destination, int width, int height, LdrDecodeMode mode = LdrDecodeMode.Linear, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateArgs(width, height);

        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        int blocksWide = footprint.BlocksWide(width);
        int blocksHigh = footprint.BlocksHigh(height);
        int bandBlockBytes = blocksWide * BlockSizeBytes;
        int bandPixelBytes = BlockDim * width * ChannelsPerPixel;

        byte[] bandBlocks = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        byte[] bandPixels = ArrayPool<byte>.Shared.Rent(bandPixelBytes);
        byte[] blockPixels = ArrayPool<byte>.Shared.Rent(DecodedBlockBytes);
        try
        {
            for (int by = 0; by < blocksHigh; by++)
            {
                await source.ReadExactlyAsync(bandBlocks.AsMemory(0, bandBlockBytes), cancellationToken).ConfigureAwait(false);

                int bandHeight = Math.Min(BlockDim, height - (by * BlockDim));
                DecodeBandRow(bandBlocks, blocksWide, width, bandHeight, mode, footprint, bandPixels, blockPixels);

                int validBytes = bandHeight * width * ChannelsPerPixel;
                await destination.WriteAsync(bandPixels.AsMemory(0, validBytes), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bandBlocks);
            ArrayPool<byte>.Shared.Return(bandPixels);
            ArrayPool<byte>.Shared.Return(blockPixels);
        }
    }

    private static void ValidateArgs(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / ChannelsPerPixel);
    }

    private static void DecodeBandRow(
        byte[] bandBlocks, int blocksWide, int width, int bandHeight, LdrDecodeMode mode, Footprint footprint, byte[] bandPixels, byte[] blockPixels)
    {
        Span<byte> blockPixelSpan = blockPixels.AsSpan(0, DecodedBlockBytes);

        for (int bx = 0; bx < blocksWide; bx++)
        {
            ReadOnlySpan<byte> block = bandBlocks.AsSpan(bx * BlockSizeBytes, BlockSizeBytes);
            DecodeBlock(block, blockPixelSpan, mode);

            BlockDestination dest = BlockImageWriter.ComputeBlockDestination(bx, 0, footprint, width, bandHeight);
            BlockImageWriter.CopyBlockRect<byte>(
                blockPixelSpan, bandPixels, BlockDim, dest.CopyWidth, dest.CopyHeight, dest.DstBaseX, dest.DstBaseY, width);
        }
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
