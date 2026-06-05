using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using AstcSharp.Core;
using AstcSharp.Encoding;

namespace AstcSharp;

/// <summary>
/// Encodes uncompressed RGBA pixel data into ASTC-compressed blocks, streaming from a source
/// <see cref="Stream"/> of RGBA32 pixels to a destination <see cref="Stream"/> of ASTC blocks one
/// block-row band at a time so peak memory is independent of the image height.
/// </summary>
/// <remarks>
/// <para>
/// The encoder is correctness-first: it produces spec-legal blocks that both this library's
/// decoder and conformant decoders (e.g. ARM's <c>astcenc</c>) read back to the original image
/// within ASTC's lossy tolerance. It does not target the rate-distortion quality or speed of a
/// production encoder.
/// </para>
/// <para>
/// Constant-colour blocks are encoded as void-extent blocks (spec §C.2.23). Other blocks are fit
/// per block: the endpoint colour mode (luminance, RGB, or RGBA — direct or base+offset) and the
/// partition count (1 to 4, spec §C.2.21, all partitions sharing one colour mode) are chosen by
/// search, with a weight grid (decimated below the footprint size as needed, spec §C.2.18) fitted
/// to the texels. Every 2D footprint is supported.
/// </para>
/// </remarks>
public static class AstcEncoder
{
    // At or above this block count in a band, the band's blocks are encoded in parallel
    private const int ParallelBlockThreshold = 2;

    /// <summary>
    /// Streams an LDR encode from <paramref name="source"/> to <paramref name="destination"/> one
    /// block-row band at a time: reads <c>footprint.Height</c> pixel rows of RGBA32, encodes that
    /// band's <c>ceil(width / footprint.Width)</c> blocks, and writes them out before reading the
    /// next band. Peak memory is one band of source pixels plus one band of output blocks,
    /// independent of the image height.
    /// </summary>
    /// <param name="source">Source RGBA32 pixels, 4 bytes per pixel in R, G, B, A order, row-major.</param>
    /// <param name="destination">The stream to write the ASTC block stream to (16 bytes per block, row-major block order).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint to encode with.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer than <c>width * height * 4</c> bytes.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A block has no legal encoding. This indicates an internal invariant violation rather than bad
    /// input — every supported 2D footprint admits a legal single-partition encoding — and should not
    /// occur in practice.
    /// </exception>
    public static void CompressImage(Stream source, Stream destination, int width, int height, Footprint footprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        int blocksWide = footprint.BlocksWide(width);
        int bandPixelBytes = footprint.Height * width * BlockInfo.ChannelsPerPixel;
        int bandBlockBytes = blocksWide * BlockInfo.SizeInBytes;

        byte[] sourceBand = ArrayPool<byte>.Shared.Rent(bandPixelBytes);
        byte[] outputBand = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        try
        {
            for (int baseY = 0; baseY < height; baseY += footprint.Height)
            {
                int bandHeight = Math.Min(footprint.Height, height - baseY);
                int validBytes = bandHeight * width * BlockInfo.ChannelsPerPixel;
                source.ReadExactly(sourceBand.AsSpan(0, validBytes));

                EncodeBand(sourceBand, bandHeight, width, footprint, blocksWide, outputBand);
                destination.Write(outputBand, 0, bandBlockBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBand);
            ArrayPool<byte>.Shared.Return(outputBand);
        }
    }

    /// <summary>
    /// Asynchronously streams an LDR encode from <paramref name="source"/> to
    /// <paramref name="destination"/> one block-row band at a time. The per-block search is
    /// synchronous (CPU-bound); only the source read and destination write are awaited, so the
    /// rented buffers persist across awaits.
    /// </summary>
    /// <param name="source">Source RGBA32 pixels, 4 bytes per pixel in R, G, B, A order, row-major.</param>
    /// <param name="destination">The stream to write the ASTC block stream to (16 bytes per block, row-major block order).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint to encode with.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer than <c>width * height * 4</c> bytes.
    /// </exception>
    public static async Task CompressImageAsync(
        Stream source, Stream destination, int width, int height, Footprint footprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        int blocksWide = footprint.BlocksWide(width);
        int bandPixelBytes = footprint.Height * width * BlockInfo.ChannelsPerPixel;
        int bandBlockBytes = blocksWide * BlockInfo.SizeInBytes;

        byte[] sourceBand = ArrayPool<byte>.Shared.Rent(bandPixelBytes);
        byte[] outputBand = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        try
        {
            for (int baseY = 0; baseY < height; baseY += footprint.Height)
            {
                int bandHeight = Math.Min(footprint.Height, height - baseY);
                int validBytes = bandHeight * width * BlockInfo.ChannelsPerPixel;
                await source.ReadExactlyAsync(sourceBand.AsMemory(0, validBytes), cancellationToken).ConfigureAwait(false);

                EncodeBand(sourceBand, bandHeight, width, footprint, blocksWide, outputBand);
                await destination.WriteAsync(outputBand.AsMemory(0, bandBlockBytes), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBand);
            ArrayPool<byte>.Shared.Return(outputBand);
        }
    }

    /// <summary>
    /// Encodes one block-row band — <paramref name="blocksWide"/> blocks fitted to the
    /// <paramref name="bandHeight"/> pixel rows held in the first <c>bandHeight * width * 4</c>
    /// bytes of <paramref name="bandPixels"/> — into <paramref name="output"/> as 16-byte
    /// little-endian blocks. Right-edge overhang clamps to the nearest in-image texel; bottom
    /// overhang on the final band repeats the last band row. Bands with at least
    /// <see cref="ParallelBlockThreshold"/> blocks encode their blocks in parallel: each block
    /// writes a disjoint 16-byte output slot and only reads the shared (immutable) band pixels.
    /// </summary>
    private static void EncodeBand(
        byte[] bandPixels, int bandHeight, int width, Footprint footprint, int blocksWide, byte[] output)
    {
        if (blocksWide >= ParallelBlockThreshold)
        {
            try
            {
                Parallel.For(0, blocksWide, blockX =>
                {
                    Span<RgbaColor> texels = stackalloc RgbaColor[footprint.PixelCount];
                    GatherBandTexels(bandPixels, bandHeight, width, footprint, blockX, texels);
                    UInt128 block = EncodeTexels(texels, footprint);
                    BinaryPrimitives.WriteUInt128LittleEndian(
                        output.AsSpan(blockX * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes), block);
                });
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
            {
                // Surface a per-block encode failure as the same exception type the serial path (and
                // the documented contract) throws, rather than the AggregateException Parallel.For wraps.
                ExceptionDispatchInfo.Throw(ex.InnerException!);
            }

            return;
        }

        Span<RgbaColor> texels = stackalloc RgbaColor[footprint.PixelCount];
        for (int blockX = 0; blockX < blocksWide; blockX++)
        {
            GatherBandTexels(bandPixels, bandHeight, width, footprint, blockX, texels);
            UInt128 block = EncodeTexels(texels, footprint);
            BinaryPrimitives.WriteUInt128LittleEndian(output.AsSpan(blockX * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes), block);
        }
    }

    /// <summary>
    /// Gathers the footprint-sized texel block at column <paramref name="blockX"/> from a band of
    /// <paramref name="bandHeight"/> pixel rows, in raster order. At right/bottom edges where the
    /// footprint overhangs the band, the nearest in-band texel is clamped into the padding
    /// positions — the decoder discards those texels, so any in-range fill is valid, and clamping
    /// keeps the endpoint fit representative.
    /// </summary>
    private static void GatherBandTexels(
        byte[] bandPixels, int bandHeight, int width, Footprint footprint, int blockX, Span<RgbaColor> texels)
    {
        int baseX = blockX * footprint.Width;

        for (int y = 0; y < footprint.Height; y++)
        {
            int srcY = Math.Min(y, bandHeight - 1);
            for (int x = 0; x < footprint.Width; x++)
            {
                int srcX = Math.Min(baseX + x, width - 1);
                int offset = ((srcY * width) + srcX) * BlockInfo.ChannelsPerPixel;
                texels[(y * footprint.Width) + x] = new RgbaColor(bandPixels[offset], bandPixels[offset + 1], bandPixels[offset + 2], bandPixels[offset + 3]);
            }
        }
    }

    /// <summary>
    /// Encodes one block's gathered texels: a void-extent block when all texels are identical,
    /// otherwise a single-partition, RGBA-direct block whose weight grid is fitted to the texels.
    /// </summary>
    private static UInt128 EncodeTexels(ReadOnlySpan<RgbaColor> texels, Footprint footprint)
    {
        if (IsConstant(texels, out RgbaColor constant))
        {
            return VoidExtentEncoder.EncodeLdr(constant.R, constant.G, constant.B, constant.A);
        }

        return LdrBlockEncoder.Encode(texels, footprint);
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
