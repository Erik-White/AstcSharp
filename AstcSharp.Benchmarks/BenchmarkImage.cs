namespace AstcSharp.Benchmarks;

/// <summary>
/// Shared image helpers for the encode benchmarks.
/// </summary>
internal static class BenchmarkImage
{
    private const int BytesPerPixel = 4;

    /// <summary>
    /// Returns the top-left <paramref name="cropWidth"/>×<paramref name="cropHeight"/> RGBA8 crop of
    /// <paramref name="source"/> (whose row stride is <paramref name="sourceWidth"/> pixels). Used to
    /// take a small, tractable tile out of a decoded fixture for per-iteration encoding.
    /// </summary>
    public static byte[] CropTopLeft(byte[] source, int sourceWidth, int cropWidth, int cropHeight)
    {
        byte[] crop = new byte[cropWidth * cropHeight * BytesPerPixel];
        for (int y = 0; y < cropHeight; y++)
        {
            source.AsSpan(y * sourceWidth * BytesPerPixel, cropWidth * BytesPerPixel)
                .CopyTo(crop.AsSpan(y * cropWidth * BytesPerPixel, cropWidth * BytesPerPixel));
        }

        return crop;
    }
}
