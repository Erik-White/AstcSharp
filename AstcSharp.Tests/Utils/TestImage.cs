namespace AstcSharp.Tests.Utils;

/// <summary>
/// Image helpers shared by the encode tests and benchmarks, which crop a decoded fixture to a small
/// region before re-encoding so the per-block search stays fast while still covering real
/// multi-block content.
/// </summary>
internal static class TestImage
{
    private const int ChannelsPerPixel = 4;

    /// <summary>
    /// Returns the top-left <paramref name="cropWidth"/> × <paramref name="cropHeight"/> region of an
    /// RGBA32 image, row-major, where <paramref name="sourceWidth"/> is the source row stride in
    /// pixels. The crop must fit within the source — callers pass a crop size known to be no larger
    /// than the fixture.
    /// </summary>
    public static byte[] CropTopLeft(ReadOnlySpan<byte> rgba, int sourceWidth, int cropWidth, int cropHeight)
    {
        byte[] cropped = new byte[cropWidth * cropHeight * ChannelsPerPixel];
        for (int y = 0; y < cropHeight; y++)
        {
            int srcOffset = y * sourceWidth * ChannelsPerPixel;
            int dstOffset = y * cropWidth * ChannelsPerPixel;
            rgba.Slice(srcOffset, cropWidth * ChannelsPerPixel).CopyTo(cropped.AsSpan(dstOffset));
        }

        return cropped;
    }
}
