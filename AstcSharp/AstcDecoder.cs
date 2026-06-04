using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.IO;

namespace AstcSharp;

/// <summary>
/// Provides methods to decode ASTC-compressed texture data into uncompressed pixel formats.
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
    /// Decompresses ASTC-compressed data to uncompressed RGBA32 format (4 bytes per pixel).
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>
    /// Array of bytes in RGBA32 format (width * height * 4 bytes total), or an empty span if the
    /// input is structurally invalid. Individual malformed blocks produce the error colour (magenta) in the output.
    /// </returns>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    public static Span<byte> DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / BlockInfo.ChannelsPerPixel);

        int totalBytes = (int)(totalPixels * BlockInfo.ChannelsPerPixel);
        byte[] imageBuffer = new byte[totalBytes];

        return DecompressImage(astcData, width, height, footprint, imageBuffer, mode)
            ? imageBuffer
            : [];
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to uncompressed RGBA32 format into a caller-provided buffer.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <param name="imageBuffer">Output buffer. Must be at least width * height * 4 bytes.</param>
    /// <returns>
    /// True if the input was structurally valid and decoding ran, false if it was rejected
    /// up front. Individual malformed blocks produce the error colour (magenta) in the output.
    /// </returns>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    public static bool DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, Span<byte> imageBuffer, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ValidateImageArgs(width, height, imageBuffer.Length, BlockInfo.ChannelsPerPixel);

        if (!TryGetBlockLayout(astcData, width, height, footprint, out int blocksWide, out int blocksHigh))
        {
            return false;
        }

        int decodedBlockSize = footprint.PixelCount * BlockInfo.ChannelsPerPixel;
        byte[] decodedBlock = ArrayPool<byte>.Shared.Rent(decodedBlockSize);
        try
        {
            Span<byte> scratch = decodedBlock.AsSpan(0, decodedBlockSize);
            if (mode == LdrDecodeMode.Srgb)
            {
                DecodeAllBlocks<LdrPipeline<SrgbMode>, byte>(
                    astcData, width, height, footprint, blocksWide, blocksHigh, imageBuffer, scratch);
            }
            else
            {
                DecodeAllBlocks<LdrPipeline<LinearMode>, byte>(
                    astcData, width, height, footprint, blocksWide, blocksHigh, imageBuffer, scratch);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decodedBlock);
        }

        return true;
    }

    /// <summary>
    /// Decompresses ASTC-compressed data read from a stream to uncompressed RGBA32 format.
    /// Reads exactly the bytes implied by <paramref name="width"/>, <paramref name="height"/>,
    /// and <paramref name="footprint"/>.
    /// </summary>
    /// <param name="stream">The stream containing ASTC-compressed block data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5).</param>
    /// <returns>
    /// Array of bytes in RGBA32 format (width * height * 4 bytes total). The stream's read
    /// position advances by the consumed block bytes.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// Thrown if the stream contains fewer bytes than the footprint requires.
    /// </exception>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    public static Span<byte> DecompressImage(Stream stream, int width, int height, Footprint footprint, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / BlockInfo.ChannelsPerPixel);

        byte[] imageBuffer = new byte[(int)(totalPixels * BlockInfo.ChannelsPerPixel)];
        return DecompressImage(stream, width, height, footprint, imageBuffer, mode)
            ? imageBuffer
            : [];
    }

    /// <summary>
    /// Decompresses ASTC-compressed data read from a stream into a caller-provided buffer.
    /// </summary>
    /// <param name="stream">The stream containing ASTC-compressed block data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <param name="imageBuffer">Output buffer. Must be at least <c>width * height * 4</c> bytes.</param>
    /// <returns>
    /// True if the stream contained the expected block count and decoding ran. The stream's
    /// read position advances by the consumed block bytes.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// Thrown if the stream contains fewer bytes than the footprint requires.
    /// </exception>
    /// <param name="mode">LDR decode mode — linear (default) or sRGB endpoint expansion.</param>
    public static bool DecompressImage(Stream stream, int width, int height, Footprint footprint, Span<byte> imageBuffer, LdrDecodeMode mode = LdrDecodeMode.Linear)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateImageArgs(width, height, imageBuffer.Length, BlockInfo.ChannelsPerPixel);

        if (mode == LdrDecodeMode.Srgb)
        {
            DecodeStreamIntoBuffer<LdrPipeline<SrgbMode>, byte>(stream, width, height, footprint, imageBuffer);
        }
        else
        {
            DecodeStreamIntoBuffer<LdrPipeline<LinearMode>, byte>(stream, width, height, footprint, imageBuffer);
        }

        return true;
    }

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
    /// Shared image-decode loop for both LDR and HDR profiles (ASTC spec §C.2.7 decode
    /// procedure, §C.2.5 LDR/HDR modes). Iterates
    /// the compressed block array in raster order, parses each block via
    /// <see cref="BlockModeDecoder.Decode"/>, runs the pipeline's profile check, and dispatches to
    /// the appropriate per-block decoder.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeAllBlocks<TPipeline, T>(
        ReadOnlySpan<byte> astcData,
        int width,
        int height,
        Footprint footprint,
        int blocksWide,
        int blocksHigh,
        Span<T> imageBuffer,
        Span<T> decodedPixels)
        where TPipeline : struct, IBlockPipeline<T>
        where T : unmanaged
    {
        int bandBlockBytes = blocksWide * BlockInfo.SizeInBytes;

        for (int blockY = 0; blockY < blocksHigh; blockY++)
        {
            ReadOnlySpan<byte> bandBlocks = astcData.Slice(blockY * bandBlockBytes, bandBlockBytes);
            DecodeBlockRow<TPipeline, T>(bandBlocks, blocksWide, blockY, footprint, width, height, imageBuffer, decodedPixels);
        }
    }

    /// <summary>
    /// Decodes one block-row (a horizontal band of <paramref name="blocksWide"/> blocks) into
    /// <paramref name="destination"/> at the pixel rows owned by block row <paramref name="blockRowIndex"/>.
    /// <paramref name="destination"/> may be the whole image (with <paramref name="destinationHeight"/>
    /// the image height) or a single-band buffer (with <paramref name="blockRowIndex"/> 0 and
    /// <paramref name="destinationHeight"/> the band's clipped pixel-row count); the clipping in
    /// <see cref="BlockImageWriter.ComputeBlockDestination"/> handles both. <paramref name="bandBlocks"/>
    /// holds exactly the band's blocks, indexed from 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeBlockRow<TPipeline, T>(
        ReadOnlySpan<byte> bandBlocks,
        int blocksWide,
        int blockRowIndex,
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
            BlockDestination dest = BlockImageWriter.ComputeBlockDestination(blockX, blockRowIndex, footprint, destinationWidth, destinationHeight);

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
    /// the fused fast path — directly to the image buffer when the block fits entirely inside
    /// the image, or to a scratch buffer at image edges that need cropping. Everything else
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
    /// Decompresses ASTC-compressed data to RGBA values.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>
    /// Values in RGBA order. For HDR content, values may exceed 1.0.
    /// </returns>
    public static Span<float> DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / 4);

        int totalFloats = (int)(totalPixels * 4);
        float[] imageBuffer = new float[totalFloats];
        if (!DecompressHdrImage(astcData, width, height, footprint, imageBuffer))
        {
            return [];
        }

        return imageBuffer;
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to RGBA float values into a caller-provided buffer.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <param name="imageBuffer">Output buffer. Must be at least width * height * 4 floats.</param>
    /// <returns>
    /// True if the input was structurally valid and decoding ran, false if it was rejected
    /// up front. Individual malformed blocks produce the error colour (magenta) in the output.
    /// </returns>
    public static bool DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, Span<float> imageBuffer)
    {
        ValidateImageArgs(width, height, imageBuffer.Length, BlockInfo.ChannelsPerPixel);

        if (!TryGetBlockLayout(astcData, width, height, footprint, out int blocksWide, out int blocksHigh))
        {
            return false;
        }

        int decodedBlockSize = footprint.PixelCount * BlockInfo.ChannelsPerPixel;
        float[] decodedBlock = ArrayPool<float>.Shared.Rent(decodedBlockSize);
        try
        {
            DecodeAllBlocks<HdrPipeline, float>(
                astcData, width, height, footprint, blocksWide, blocksHigh, imageBuffer, decodedBlock.AsSpan(0, decodedBlockSize));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(decodedBlock);
        }

        return true;
    }

    /// <summary>
    /// Decompresses ASTC-compressed data read from a stream to RGBA float values.
    /// </summary>
    /// <param name="stream">The stream containing ASTC-compressed block data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <returns>
    /// Values in RGBA order. For HDR content, values may exceed 1.0. The stream's read position
    /// advances by the consumed block bytes.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// Thrown if the stream contains fewer bytes than the footprint requires.
    /// </exception>
    public static Span<float> DecompressHdrImage(Stream stream, int width, int height, Footprint footprint)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / BlockInfo.ChannelsPerPixel);

        float[] imageBuffer = new float[(int)(totalPixels * BlockInfo.ChannelsPerPixel)];
        return DecompressHdrImage(stream, width, height, footprint, imageBuffer)
            ? imageBuffer
            : [];
    }

    /// <summary>
    /// Decompresses ASTC-compressed data read from a stream into a caller-provided HDR buffer.
    /// </summary>
    /// <param name="stream">The stream containing ASTC-compressed block data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <param name="imageBuffer">Output buffer. Must be at least <c>width * height * 4</c> floats.</param>
    /// <returns>
    /// True if the stream contained the expected block count and decoding ran. The stream's
    /// read position advances by the consumed block bytes.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// Thrown if the stream contains fewer bytes than the footprint requires.
    /// </exception>
    public static bool DecompressHdrImage(Stream stream, int width, int height, Footprint footprint, Span<float> imageBuffer)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateImageArgs(width, height, imageBuffer.Length, BlockInfo.ChannelsPerPixel);

        DecodeStreamIntoBuffer<HdrPipeline, float>(stream, width, height, footprint, imageBuffer);
        return true;
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
    /// Decompresses ASTC-compressed data to RGBA values.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint type</param>
    /// <returns>
    /// Values in RGBA order. For HDR content, values may exceed 1.0.
    /// </returns>
    public static Span<float> DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        Footprint requestedFootprint = Footprint.FromFootprintType(footprint);
        return DecompressHdrImage(astcData, width, height, requestedFootprint);
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to FP16 (<see cref="Half"/>) RGBA values.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>
    /// Values in RGBA order as FP16, or an empty span if the input is structurally invalid. For HDR-endpoint
    /// channels these are full range, for LDR endpoint channels are the nearest <see cref="Half"/> to the [0,1] value.
    /// </returns>
    public static Span<Half> DecompressHdrImageHalf(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / BlockInfo.ChannelsPerPixel);

        Half[] imageBuffer = new Half[(int)(totalPixels * BlockInfo.ChannelsPerPixel)];
        return DecompressHdrImageHalf(astcData, width, height, footprint, imageBuffer)
            ? imageBuffer
            : [];
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to FP16 (<see cref="Half"/>) RGBA values into a
    /// caller-provided buffer.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <param name="imageBuffer">Output buffer. Must be at least width * height * 4 elements.</param>
    /// <returns>
    /// True if the input was structurally valid and decoding ran, false if it was rejected
    /// up front. Individual malformed blocks produce the error colour (magenta) in the output.
    /// </returns>
    public static bool DecompressHdrImageHalf(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, Span<Half> imageBuffer)
    {
        ValidateImageArgs(width, height, imageBuffer.Length, BlockInfo.ChannelsPerPixel);

        long totalElements = (long)width * height * BlockInfo.ChannelsPerPixel;
        float[] floatBuffer = ArrayPool<float>.Shared.Rent((int)totalElements);
        try
        {
            Span<float> floatSpan = floatBuffer.AsSpan(0, (int)totalElements);
            if (!DecompressHdrImage(astcData, width, height, footprint, floatSpan))
            {
                return false;
            }

            NarrowToHalf(floatSpan, imageBuffer);
            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatBuffer);
        }
    }

    /// <summary>
    /// Decompresses ASTC-compressed data read from a stream to FP16 (<see cref="Half"/>) RGBA values.
    /// </summary>
    /// <param name="stream">The stream containing ASTC-compressed block data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <returns>
    /// Values in RGBA order as FP16. The stream's read position advances by the consumed block bytes.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// Thrown if the stream contains fewer bytes than the footprint requires.
    /// </exception>
    public static Span<Half> DecompressHdrImageHalf(Stream stream, int width, int height, Footprint footprint)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / BlockInfo.ChannelsPerPixel);

        Half[] imageBuffer = new Half[(int)(totalPixels * BlockInfo.ChannelsPerPixel)];
        return DecompressHdrImageHalf(stream, width, height, footprint, imageBuffer)
            ? imageBuffer
            : [];
    }

    /// <summary>
    /// Decompresses ASTC-compressed data read from a stream into a caller-provided FP16
    /// (<see cref="Half"/>) buffer.
    /// </summary>
    /// <param name="stream">The stream containing ASTC-compressed block data.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="footprint">The ASTC block footprint.</param>
    /// <param name="imageBuffer">Output buffer. Must be at least <c>width * height * 4</c> elements.</param>
    /// <returns>
    /// True if the stream contained the expected block count and decoding ran. The stream's
    /// read position advances by the consumed block bytes.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// Thrown if the stream contains fewer bytes than the footprint requires.
    /// </exception>
    public static bool DecompressHdrImageHalf(Stream stream, int width, int height, Footprint footprint, Span<Half> imageBuffer)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateImageArgs(width, height, imageBuffer.Length, BlockInfo.ChannelsPerPixel);

        int blocksWide = (width + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
        int bandBlockBytes = blocksWide * BlockInfo.SizeInBytes;
        int bandPixelElements = footprint.Height * width * BlockInfo.ChannelsPerPixel;
        int scratchSize = footprint.PixelCount * BlockInfo.ChannelsPerPixel;

        byte[] bandBlocks = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        float[] bandPixels = ArrayPool<float>.Shared.Rent(bandPixelElements);
        float[] scratch = ArrayPool<float>.Shared.Rent(scratchSize);
        try
        {
            Span<byte> bandSpan = bandBlocks.AsSpan(0, bandBlockBytes);
            Span<float> bandPixelSpan = bandPixels.AsSpan(0, bandPixelElements);
            Span<float> scratchSpan = scratch.AsSpan(0, scratchSize);

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                stream.ReadExactly(bandSpan);
                int bandHeight = Math.Min(footprint.Height, height - (blockY * footprint.Height));
                DecodeBlockRow<HdrPipeline, float>(bandSpan, blocksWide, 0, footprint, width, bandHeight, bandPixelSpan, scratchSpan);

                int validElements = bandHeight * width * BlockInfo.ChannelsPerPixel;
                int dstOffset = blockY * footprint.Height * width * BlockInfo.ChannelsPerPixel;
                NarrowToHalf(bandPixelSpan[..validElements], imageBuffer.Slice(dstOffset, validElements));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bandBlocks);
            ArrayPool<float>.Shared.Return(bandPixels);
            ArrayPool<float>.Shared.Return(scratch);
        }

        return true;
    }

    /// <summary>
    /// Decodes ASTC blocks read from <paramref name="source"/> and writes the FP16
    /// (<see cref="Half"/>) RGBA result to <paramref name="destination"/> as little-endian values,
    /// one block-row band at a time. Only a single band is held in memory, so peak usage is
    /// independent of the image height.
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
    /// Narrows decoded HDR float channels to <see cref="Half"/>. For values that originated as
    /// FP16 (HDR/LNS and void-extent channels, ASTC spec §C.2.15, §C.2.23) this is an exact
    /// round-trip; LDR-endpoint channels narrow to the nearest <see cref="Half"/>.
    /// </summary>
    private static void NarrowToHalf(ReadOnlySpan<float> source, Span<Half> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = (Half)source[i];
        }
    }

    internal static Span<byte> DecompressImage(AstcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return DecompressImage(file.Blocks, file.Width, file.Height, file.Footprint);
    }

    internal static Span<byte> DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        Footprint requestedFootprint = Footprint.FromFootprintType(footprint);

        return DecompressImage(astcData, width, height, requestedFootprint);
    }

    private static bool TryGetBlockLayout(
        ReadOnlySpan<byte> astcData,
        int width,
        int height,
        Footprint footprint,
        out int blocksWide,
        out int blocksHigh)
    {
        int blockWidth = footprint.Width;
        int blockHeight = footprint.Height;
        blocksWide = 0;
        blocksHigh = 0;

        if (blockWidth <= 0 || blockHeight <= 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        blocksWide = (width + blockWidth - 1) / blockWidth;
        blocksHigh = (height + blockHeight - 1) / blockHeight;

        // Guard against integer overflow in block count calculation
        long expectedBlockCount = (long)blocksWide * blocksHigh;
        if (astcData.Length % BlockInfo.SizeInBytes != 0 || astcData.Length / BlockInfo.SizeInBytes != expectedBlockCount)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates that <paramref name="width"/> and <paramref name="height"/> are positive,
    /// that width × height × <paramref name="bytesPerPixel"/> does not overflow
    /// <see cref="int.MaxValue"/>, and that <paramref name="bufferLength"/> has room for
    /// the decoded output.
    /// </summary>
    private static void ValidateImageArgs(int width, int height, int bufferLength, int bytesPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        long totalPixels = (long)width * height;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalPixels, (long)int.MaxValue / bytesPerPixel);

        long totalElements = totalPixels * bytesPerPixel;
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferLength, totalElements);
    }

    /// <summary>
    /// Reads the 16 bytes of the ASTC block at <paramref name="blockIndex"/> into a
    /// <see cref="UInt128"/> (little-endian). The caller is responsible for ensuring the
    /// stream contains the requested block — <see cref="TryGetBlockLayout"/> verifies
    /// <c>astcData.Length</c> matches the expected block count before iteration begins.
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
    /// Decodes the entire block stream from <paramref name="stream"/> into
    /// <paramref name="imageBuffer"/> one block-row band at a time. Only a single band of
    /// compressed blocks and decoded pixels is rented; the band decodes straight into its rows of
    /// the caller's buffer, so no whole-stream copy is made. Edge bands clip to the image height.
    /// </summary>
    private static void DecodeStreamIntoBuffer<TPipeline, T>(
        Stream stream, int width, int height, Footprint footprint, Span<T> imageBuffer)
        where TPipeline : struct, IBlockPipeline<T>
        where T : unmanaged
    {
        int blocksWide = (width + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
        int bandBlockBytes = blocksWide * BlockInfo.SizeInBytes;
        int scratchSize = footprint.PixelCount * BlockInfo.ChannelsPerPixel;

        byte[] bandBlocks = ArrayPool<byte>.Shared.Rent(bandBlockBytes);
        T[] scratch = ArrayPool<T>.Shared.Rent(scratchSize);
        try
        {
            Span<byte> bandSpan = bandBlocks.AsSpan(0, bandBlockBytes);
            Span<T> scratchSpan = scratch.AsSpan(0, scratchSize);

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                stream.ReadExactly(bandSpan);
                DecodeBlockRow<TPipeline, T>(bandSpan, blocksWide, blockY, footprint, width, height, imageBuffer, scratchSpan);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bandBlocks);
            ArrayPool<T>.Shared.Return(scratch);
        }
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
        int blocksWide = (width + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
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
                DecodeBlockRow<TPipeline, TElement>(bandSpan, blocksWide, 0, footprint, width, bandHeight, bandPixelSpan, scratchSpan);

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
        int blocksWide = (width + footprint.Width - 1) / footprint.Width;
        int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
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
                    bandBlocks.AsSpan(0, bandBlockBytes), blocksWide, 0, footprint, width, bandHeight,
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
