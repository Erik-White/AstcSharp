using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using AstcSharp.BlockDecoding;
using AstcSharp.Core;

namespace AstcSharp;

/// <summary>
/// Decodes ASTC-compressed texture data into uncompressed pixel formats, streaming from a source
/// <see cref="Stream"/> of ASTC blocks to a destination <see cref="Stream"/> of pixels one
/// block-row band at a time so peak memory is independent of the image height.
/// </summary>
/// <remarks>
/// The decoder returns raw decoded values and does not apply an sRGB-to-linear transform.
/// Passing <see cref="LdrDecodeMode.Srgb"/> to an LDR method selects the sRGB endpoint
/// expansion mandated by ASTC spec §C.2.19 (matching the <c>COMPRESSED_SRGB8_ALPHA8_ASTC_*</c>
/// formats) — the output is still sRGB-encoded 8-bit values. Callers loading ASTC data from an
/// sRGB-tagged container who need linear values are responsible for applying sRGB-to-linear
/// conversion downstream.
/// </remarks>
public static class AstcDecoder
{
    /// <summary>
    /// Decodes ASTC blocks read from <paramref name="source"/> and writes the RGBA32 result to
    /// <paramref name="destination"/>, one block-row band at a time. Only a single band of
    /// compressed blocks and decoded pixels is held in memory, so peak usage is independent of
    /// the image height.
    /// </summary>
    /// <param name="source">The stream containing ASTC-compressed block data.</param>
    /// <param name="destination">The stream to write RGBA32 pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer bytes than the footprint requires.
    /// </exception>
    public static void DecompressImage(Stream source, Stream destination, int width, int height, Footprint footprint, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateStreamDecodeArgs(width, height);

        if (mode == LdrDecodeMode.Srgb)
        {
            DecodeToStream<LdrPipeline<SrgbMode>, byte, ByteBandSerializer>(source, destination, width, height, footprint);
        }
        else
        {
            DecodeToStream<LdrPipeline<LinearMode>, byte, ByteBandSerializer>(source, destination, width, height, footprint);
        }
    }

    /// <summary>
    /// Asynchronously decodes ASTC blocks read from <paramref name="source"/> and writes the
    /// RGBA32 result to <paramref name="destination"/>, one block-row band at a time. Only a
    /// single band is held in memory; <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>
    /// and <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> drive the I/O.
    /// </summary>
    /// <param name="source">The stream containing ASTC-compressed block data.</param>
    /// <param name="destination">The stream to write RGBA32 pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer bytes than the footprint requires.
    /// </exception>
    public static Task DecompressImageAsync(
        Stream source, Stream destination, int width, int height, Footprint footprint, LdrDecodeMode mode = LdrDecodeMode.Linear, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateStreamDecodeArgs(width, height);

        return mode == LdrDecodeMode.Srgb
            ? DecodeToStreamAsync<LdrPipeline<SrgbMode>, byte, ByteBandSerializer>(source, destination, width, height, footprint, cancellationToken)
            : DecodeToStreamAsync<LdrPipeline<LinearMode>, byte, ByteBandSerializer>(source, destination, width, height, footprint, cancellationToken);
    }

    /// <summary>
    /// Decodes ASTC blocks read from <paramref name="source"/> and writes the RGBA float result
    /// to <paramref name="destination"/> as little-endian IEEE-754 values, one block-row band at
    /// a time. Only a single band of compressed blocks and decoded pixels is held in memory, so
    /// peak usage is independent of the image height. For HDR content, values may exceed 1.0.
    /// </summary>
    /// <param name="source">The stream containing ASTC-compressed block data.</param>
    /// <param name="destination">The stream to write little-endian RGBA float pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer bytes than the footprint requires.
    /// </exception>
    public static void DecompressHdrImage(Stream source, Stream destination, int width, int height, Footprint footprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateStreamDecodeArgs(width, height);

        DecodeToStream<HdrPipeline, float, FloatBandSerializer>(source, destination, width, height, footprint);
    }

    /// <summary>
    /// Asynchronously decodes ASTC blocks read from <paramref name="source"/> and writes the RGBA
    /// float result to <paramref name="destination"/> as little-endian IEEE-754 values, one
    /// block-row band at a time. Only a single band is held in memory.
    /// </summary>
    /// <param name="source">The stream containing ASTC-compressed block data.</param>
    /// <param name="destination">The stream to write little-endian RGBA float pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer bytes than the footprint requires.
    /// </exception>
    public static Task DecompressHdrImageAsync(
        Stream source, Stream destination, int width, int height, Footprint footprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateStreamDecodeArgs(width, height);

        return DecodeToStreamAsync<HdrPipeline, float, FloatBandSerializer>(source, destination, width, height, footprint, cancellationToken);
    }

    /// <summary>
    /// Decodes ASTC blocks read from <paramref name="source"/> and writes the FP16
    /// (<see cref="Half"/>) RGBA result to <paramref name="destination"/> as little-endian values,
    /// one block-row band at a time. Only a single band is held in memory, so peak usage is
    /// independent of the image height. For HDR-endpoint channels the values are full range; for
    /// LDR-endpoint channels they are the nearest <see cref="Half"/> to the [0,1] value.
    /// </summary>
    /// <param name="source">The stream containing ASTC-compressed block data.</param>
    /// <param name="destination">The stream to write little-endian RGBA <see cref="Half"/> pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer bytes than the footprint requires.
    /// </exception>
    public static void DecompressHdrImageHalf(Stream source, Stream destination, int width, int height, Footprint footprint)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateStreamDecodeArgs(width, height);

        DecodeToStream<HdrPipeline, float, HalfBandSerializer>(source, destination, width, height, footprint);
    }

    /// <summary>
    /// Asynchronously decodes ASTC blocks read from <paramref name="source"/> and writes the FP16
    /// (<see cref="Half"/>) RGBA result to <paramref name="destination"/> as little-endian values,
    /// one block-row band at a time. Only a single band is held in memory.
    /// </summary>
    /// <param name="source">The stream containing ASTC-compressed block data.</param>
    /// <param name="destination">The stream to write little-endian RGBA <see cref="Half"/> pixels to, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="EndOfStreamException">
    /// Thrown if <paramref name="source"/> contains fewer bytes than the footprint requires.
    /// </exception>
    public static Task DecompressHdrImageHalfAsync(
        Stream source, Stream destination, int width, int height, Footprint footprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateStreamDecodeArgs(width, height);

        return DecodeToStreamAsync<HdrPipeline, float, HalfBandSerializer>(source, destination, width, height, footprint, cancellationToken);
    }

    /// <summary>
    /// Decodes one block-row (a horizontal band of <paramref name="blocksWide"/> blocks) into
    /// <paramref name="destination"/>, a single-band pixel buffer whose first
    /// <paramref name="destinationHeight"/> rows are valid (the band is clipped to the image
    /// height at the bottom edge). <paramref name="bandBlocks"/> holds exactly the band's blocks,
    /// indexed from 0; the per-block decode writes through <paramref name="decodedPixels"/> scratch
    /// for blocks the fused fast path cannot place directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeBlockRow<TPipeline, T>(
        ReadOnlySpan<byte> bandBlocks,
        int blocksWide,
        Footprint footprint,
        int destinationWidth,
        int destinationHeight,
        Span<T> destination,
        Span<T> decodedPixels)
        where TPipeline : struct, IBlockPipeline<T>
        where T : unmanaged
    {
        TPipeline pipeline = default;

        for (int blockX = 0; blockX < blocksWide; blockX++)
        {
            UInt128 blockBits = ReadBlockBits(bandBlocks, blockX);

            BlockInfo info = BlockModeDecoder.Decode(blockBits);
            BlockDestination dest = BlockImageWriter.ComputeBlockDestination(blockX, 0, footprint, destinationWidth, destinationHeight);

            // Spec §C.2.19, §C.2.24, §C.2.25: illegal block encodings, and HDR endpoint modes
            // in the LDR profile, must produce the error colour (magenta) for every texel.
            if (!info.IsValid || !pipeline.IsBlockLegal(in info))
            {
                pipeline.WriteErrorColorClipped(
                    footprint, dest.DstBaseX, dest.DstBaseY, dest.CopyWidth, dest.CopyHeight, destinationWidth, destination);
                continue;
            }

            DecodeBlock<TPipeline, T>(blockBits, in info, footprint, dest, destinationWidth, destination, decodedPixels);
        }
    }

    /// <summary>
    /// Routes a single block to the best available path. Single-partition, single-plane,
    /// non-void-extent blocks (the common shape per ASTC spec §C.2.10, §C.2.20, §C.2.23) take
    /// the fused fast path — directly to the band buffer when the block fits entirely inside
    /// the band, or to a scratch buffer at edges that need cropping. Everything else
    /// (void-extent, multi-partition, dual-plane) falls through to the general
    /// <see cref="LogicalBlock"/> pipeline.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeBlock<TPipeline, T>(
        UInt128 blockBits,
        in BlockInfo info,
        Footprint footprint,
        BlockDestination dest,
        int imageWidth,
        Span<T> imageBuffer,
        Span<T> decodedPixels)
        where TPipeline : struct, IBlockPipeline<T>
        where T : unmanaged
    {
        TPipeline pipeline = default;

        if (info.IsFusable && dest.IsFullInterior)
        {
            pipeline.FusedToImage(blockBits, in info, footprint, dest.DstBaseX, dest.DstBaseY, imageWidth, imageBuffer);
            return;
        }

        if (info.IsFusable)
        {
            pipeline.FusedToScratch(blockBits, in info, footprint, decodedPixels);
        }
        else
        {
            pipeline.LogicalWrite(blockBits, in info, footprint, decodedPixels);
        }

        BlockImageWriter.CopyBlockRect(decodedPixels, imageBuffer, footprint.Width, dest.CopyWidth, dest.CopyHeight, dest.DstBaseX, dest.DstBaseY, imageWidth);
    }

    /// <summary>
    /// Reads the 16 bytes of the ASTC block at <paramref name="blockIndex"/> into a
    /// <see cref="UInt128"/> (little-endian). The caller is responsible for ensuring
    /// <paramref name="astcData"/> contains the requested block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 ReadBlockBits(ReadOnlySpan<byte> astcData, int blockIndex)
    {
        int offset = blockIndex * BlockInfo.SizeInBytes;
        return BinaryPrimitives.ReadUInt128LittleEndian(astcData.Slice(offset, BlockInfo.SizeInBytes));
    }

    /// <summary>
    /// Validates that <paramref name="width"/> and <paramref name="height"/> are positive and
    /// that <c>width × height × 4</c> does not overflow <see cref="int.MaxValue"/>. The
    /// stream-to-stream paths never materialise the whole image, but the per-band buffer offsets
    /// are computed with <see cref="int"/> arithmetic, so the total element count must still fit.
    /// </summary>
    private static void ValidateStreamDecodeArgs(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / BlockInfo.ChannelsPerPixel);
    }

    /// <summary>
    /// Serialises a decoded pixel band of element type <typeparamref name="TElement"/> into a
    /// destination byte buffer. Used by the stream-to-stream decode paths to choose the output
    /// byte layout (raw bytes, little-endian float, or little-endian <see cref="Half"/>) while
    /// the band loop stays generic.
    /// </summary>
    private interface IBandSerializer<TElement>
        where TElement : unmanaged
    {
        /// <summary>
        /// Bytes emitted per decoded element (RGBA channel).
        /// </summary>
        static abstract int BytesPerElement { get; }

        /// <summary>
        /// Writes <paramref name="source"/> (<paramref name="elementCount"/> decoded elements)
        /// into <paramref name="destination"/> as little-endian bytes.
        /// </summary>
        static abstract void Serialize(ReadOnlySpan<TElement> source, int elementCount, Span<byte> destination);
    }

    private readonly struct ByteBandSerializer : IBandSerializer<byte>
    {
        public static int BytesPerElement => sizeof(byte);

        public static void Serialize(ReadOnlySpan<byte> source, int elementCount, Span<byte> destination)
            => source[..elementCount].CopyTo(destination);
    }

    private readonly struct FloatBandSerializer : IBandSerializer<float>
    {
        public static int BytesPerElement => sizeof(float);

        public static void Serialize(ReadOnlySpan<float> source, int elementCount, Span<byte> destination)
        {
            for (int i = 0; i < elementCount; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(i * sizeof(float), sizeof(float)), source[i]);
            }
        }
    }

    private readonly struct HalfBandSerializer : IBandSerializer<float>
    {
        public static int BytesPerElement => sizeof(ushort);

        public static void Serialize(ReadOnlySpan<float> source, int elementCount, Span<byte> destination)
        {
            for (int i = 0; i < elementCount; i++)
            {
                BinaryPrimitives.WriteHalfLittleEndian(destination.Slice(i * sizeof(ushort), sizeof(ushort)), (Half)source[i]);
            }
        }
    }

    /// <summary>
    /// Streams a decode from <paramref name="source"/> to <paramref name="destination"/> one
    /// block-row band at a time, serialising each band with <typeparamref name="TSerializer"/>.
    /// Peak memory is one band of compressed blocks, one band of decoded pixels, one per-block
    /// scratch buffer, and one band of serialised output — all independent of the image height.
    /// </summary>
    private static void DecodeToStream<TPipeline, TElement, TSerializer>(
        Stream source, Stream destination, int width, int height, Footprint footprint)
        where TPipeline : struct, IBlockPipeline<TElement>
        where TElement : unmanaged
        where TSerializer : struct, IBandSerializer<TElement>
    {
        int blocksWide = footprint.BlocksWide(width);
        int blocksHigh = footprint.BlocksHigh(height);
        int bandBlockBytes = blocksWide * BlockInfo.SizeInBytes;
        int bandPixelElements = footprint.Height * width * BlockInfo.ChannelsPerPixel;
        int scratchSize = footprint.PixelCount * BlockInfo.ChannelsPerPixel;

        byte[] bandBlocks = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        TElement[] bandPixels = ArrayPool<TElement>.Shared.Rent(bandPixelElements);
        TElement[] scratch = ArrayPool<TElement>.Shared.Rent(scratchSize);
        byte[] outputBand = ArrayPool<byte>.Shared.Rent(bandPixelElements * TSerializer.BytesPerElement);
        try
        {
            Span<byte> bandSpan = bandBlocks.AsSpan(0, bandBlockBytes);
            Span<TElement> bandPixelSpan = bandPixels.AsSpan(0, bandPixelElements);
            Span<TElement> scratchSpan = scratch.AsSpan(0, scratchSize);

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                source.ReadExactly(bandSpan);
                int bandHeight = Math.Min(footprint.Height, height - (blockY * footprint.Height));
                DecodeBlockRow<TPipeline, TElement>(bandSpan, blocksWide, footprint, width, bandHeight, bandPixelSpan, scratchSpan);

                int validElements = bandHeight * width * BlockInfo.ChannelsPerPixel;
                int outputBytes = validElements * TSerializer.BytesPerElement;
                TSerializer.Serialize(bandPixelSpan, validElements, outputBand);
                destination.Write(outputBand, 0, outputBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bandBlocks);
            ArrayPool<TElement>.Shared.Return(bandPixels);
            ArrayPool<TElement>.Shared.Return(scratch);
            ArrayPool<byte>.Shared.Return(outputBand);
        }
    }

    /// <summary>
    /// Asynchronous counterpart to <see cref="DecodeToStream{TPipeline, TElement, TSerializer}"/>.
    /// The block decode itself is synchronous (CPU-bound, span-based); only the source read and
    /// destination write are awaited, so the rented buffers must persist across awaits — hence the
    /// arrays rather than spans.
    /// </summary>
    private static async Task DecodeToStreamAsync<TPipeline, TElement, TSerializer>(
        Stream source, Stream destination, int width, int height, Footprint footprint, CancellationToken cancellationToken)
        where TPipeline : struct, IBlockPipeline<TElement>
        where TElement : unmanaged
        where TSerializer : struct, IBandSerializer<TElement>
    {
        int blocksWide = footprint.BlocksWide(width);
        int blocksHigh = footprint.BlocksHigh(height);
        int bandBlockBytes = blocksWide * BlockInfo.SizeInBytes;
        int bandPixelElements = footprint.Height * width * BlockInfo.ChannelsPerPixel;
        int scratchSize = footprint.PixelCount * BlockInfo.ChannelsPerPixel;

        byte[] bandBlocks = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        TElement[] bandPixels = ArrayPool<TElement>.Shared.Rent(bandPixelElements);
        TElement[] scratch = ArrayPool<TElement>.Shared.Rent(scratchSize);
        byte[] outputBand = ArrayPool<byte>.Shared.Rent(bandPixelElements * TSerializer.BytesPerElement);
        try
        {
            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                await source.ReadExactlyAsync(bandBlocks.AsMemory(0, bandBlockBytes), cancellationToken).ConfigureAwait(false);

                int bandHeight = Math.Min(footprint.Height, height - (blockY * footprint.Height));
                DecodeBlockRow<TPipeline, TElement>(
                    bandBlocks.AsSpan(0, bandBlockBytes), blocksWide, footprint, width, bandHeight,
                    bandPixels.AsSpan(0, bandPixelElements), scratch.AsSpan(0, scratchSize));

                int validElements = bandHeight * width * BlockInfo.ChannelsPerPixel;
                int outputBytes = validElements * TSerializer.BytesPerElement;
                TSerializer.Serialize(bandPixels.AsSpan(0, bandPixelElements), validElements, outputBand);
                await destination.WriteAsync(outputBand.AsMemory(0, outputBytes), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bandBlocks);
            ArrayPool<TElement>.Shared.Return(bandPixels);
            ArrayPool<TElement>.Shared.Return(scratch);
            ArrayPool<byte>.Shared.Return(outputBand);
        }
    }
}
