namespace AstcSharp.Tests.Utils;

/// <summary>
/// Image helpers shared by the encode tests, which crop a decoded fixture to a small region before
/// re-encoding so the per-block search stays fast while still covering real multi-block content.
/// </summary>
internal static class TestImage
{
    private const int ChannelsPerPixel = 4;

    /// <summary>
    /// Returns the top-left <paramref name="cropWidth"/> × <paramref name="cropHeight"/> region of an
    /// RGBA32 image, row-major. <paramref name="cropWidth"/>/<paramref name="cropHeight"/> are clamped
    /// to the source bounds so a crop never reads past the image.
    /// </summary>
    public static byte[] CropTopLeft(ReadOnlySpan<byte> rgba, int sourceWidth, int cropWidth, int cropHeight)
    {
        int width = Math.Min(cropWidth, sourceWidth);
        int height = Math.Min(cropHeight, rgba.Length / (sourceWidth * ChannelsPerPixel));

        byte[] cropped = new byte[width * height * ChannelsPerPixel];
        for (int y = 0; y < height; y++)
        {
            int srcOffset = y * sourceWidth * ChannelsPerPixel;
            int dstOffset = y * width * ChannelsPerPixel;
            rgba.Slice(srcOffset, width * ChannelsPerPixel).CopyTo(cropped.AsSpan(dstOffset));
        }

        return cropped;
    }
}
