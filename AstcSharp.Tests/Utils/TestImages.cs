namespace AstcSharp.Tests.Utils;

/// <summary>
/// Synthetic RGBA test images shared by the encode tests and benchmarks across both the LDR (byte)
/// and HDR (<see cref="Half"/>) profiles. Each generator produces a content archetype that drives a
/// specific encoder code path (constant, gradient, multi-region, decorrelated-alpha), so tests and
/// benchmarks exercise the same well-understood inputs. All images are row-major, 4 channels per
/// pixel, RGBA order.
/// </summary>
/// <remarks>
/// Generators unique to a single test (e.g. the various single-line ramps, or the narrow-range /
/// uniform-darkening HDR blocks that pin one endpoint mode) stay local to that test; only content
/// reused across files lives here.
/// </remarks>
internal static class TestImages
{
    private const int Channels = 4;

    // ---- LDR (byte / UNORM8) ----

    /// <summary>
    /// A constant colour filling the whole image.
    /// </summary>
    public static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] pixels = new byte[width * height * Channels];
        for (int i = 0; i < pixels.Length; i += Channels)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }

    /// <summary>
    /// A chromatic gradient across x: red rises, green falls, blue rises at half rate — opaque. The
    /// three channels vary independently, exercising the RGB endpoint modes.
    /// </summary>
    public static byte[] ChromaticGradient(int width, int height)
    {
        byte[] pixels = new byte[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                byte v = (byte)(255 * x / Math.Max(1, width - 1));
                pixels[idx] = v;
                pixels[idx + 1] = (byte)(255 - v);
                pixels[idx + 2] = (byte)(128 + (v / 2));
                pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// A grayscale ramp (R=G=B) across the x+y diagonal, opaque — single-channel content for the
    /// luminance modes.
    /// </summary>
    public static byte[] GrayscaleRamp(int width, int height)
    {
        byte[] pixels = new byte[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                byte v = (byte)(255 * (x + y) / Math.Max(1, width + height - 2));
                pixels[idx] = v;
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
                pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Two vertically-ramped regions split left/right: a red→yellow ramp beside a blue→cyan ramp. The
    /// four endpoints are not collinear, so a single endpoint line fits poorly — content that elicits
    /// partitioning.
    /// </summary>
    public static byte[] TwoRegion(int width, int height)
    {
        byte[] pixels = new byte[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                float t = (float)y / Math.Max(1, height - 1);
                if (x < width / 2)
                {
                    pixels[idx] = 220;
                    pixels[idx + 1] = (byte)(20 + (t * 200));
                    pixels[idx + 2] = 20;
                }
                else
                {
                    pixels[idx] = 20;
                    pixels[idx + 1] = (byte)(20 + (t * 200));
                    pixels[idx + 2] = 220;
                }

                pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Four saturated solid colours, one per quadrant — four well-separated points in RGB space that
    /// no single (or double) endpoint line covers, eliciting a multi-partition fit.
    /// </summary>
    public static byte[] FourQuadrant(int width, int height)
    {
        (byte R, byte G, byte B)[] quadrant =
        [
            (240, 20, 20), (20, 240, 20), (20, 20, 240), (240, 240, 20),
        ];

        byte[] pixels = new byte[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                int cell = ((y < height / 2) ? 0 : 2) + ((x < width / 2) ? 0 : 1);
                (byte r, byte g, byte b) = quadrant[cell];
                pixels[idx] = r; pixels[idx + 1] = g; pixels[idx + 2] = b; pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// RGB ramps up while alpha ramps down — anti-correlated channels a single weight line cannot
    /// track, the ideal case for a dual-plane block with the second plane on alpha.
    /// </summary>
    public static byte[] DecorrelatedAlpha(int width, int height)
    {
        byte[] pixels = new byte[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                byte up = (byte)(t * 255);
                pixels[idx] = up; pixels[idx + 1] = up; pixels[idx + 2] = up;
                pixels[idx + 3] = (byte)((1 - t) * 255);
            }
        }

        return pixels;
    }

    // ---- HDR (Half / FP16) ----

    /// <summary>
    /// A constant HDR colour filling the whole image.
    /// </summary>
    public static Half[] SolidHdr(int width, int height, Half r, Half g, Half b, Half a)
    {
        Half[] pixels = new Half[width * height * Channels];
        for (int i = 0; i < pixels.Length; i += Channels)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }

    /// <summary>
    /// A chromatic HDR gradient across x (values above 1.0), each channel varying independently —
    /// drives the single-partition RGB search.
    /// </summary>
    public static Half[] ChromaticGradientHdr(int width, int height)
    {
        Half[] pixels = new Half[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                float t = (float)x / Math.Max(1, width - 1);
                pixels[idx] = (Half)(4.0f * t);
                pixels[idx + 1] = (Half)(2.0f * (1.0f - t));
                pixels[idx + 2] = (Half)(1.0f + (3.0f * t));
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    /// <summary>
    /// A smooth chromatic HDR gradient across the x+y diagonal, channels varying independently — a
    /// single endpoint line leaves error a second weight plane removes, so the encoder tends to pick
    /// dual-plane.
    /// </summary>
    public static Half[] SmoothGradientHdr(int width, int height)
    {
        Half[] pixels = new Half[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                pixels[idx] = (Half)(1.0f + (3.0f * t));
                pixels[idx + 1] = (Half)(2.0f + (1.0f * t));
                pixels[idx + 2] = (Half)(3.0f - (2.0f * t));
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Two HDR colour regions split left/right, each a vertical ramp — poorly served by one endpoint
    /// line, so the encoder may partition.
    /// </summary>
    public static Half[] TwoRegionHdr(int width, int height)
    {
        Half[] pixels = new Half[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                float t = (float)y / Math.Max(1, height - 1);
                if (x < width / 2)
                {
                    pixels[idx] = (Half)4.0f; pixels[idx + 1] = (Half)(0.5f + (3.0f * t)); pixels[idx + 2] = (Half)0.5f;
                }
                else
                {
                    pixels[idx] = (Half)0.5f; pixels[idx + 1] = (Half)(0.5f + (3.0f * t)); pixels[idx + 2] = (Half)4.0f;
                }

                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Four saturated solid HDR colours, one per quadrant — four well-separated RGB points eliciting a
    /// multi-partition fit.
    /// </summary>
    public static Half[] FourQuadrantHdr(int width, int height)
    {
        (float R, float G, float B)[] quadrant =
        [
            (4.0f, 0.25f, 0.25f), (0.25f, 4.0f, 0.25f), (0.25f, 0.25f, 4.0f), (4.0f, 4.0f, 0.25f),
        ];

        Half[] pixels = new Half[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                int cell = ((y < height / 2) ? 0 : 2) + ((x < width / 2) ? 0 : 1);
                (float r, float g, float b) = quadrant[cell];
                pixels[idx] = (Half)r; pixels[idx + 1] = (Half)g; pixels[idx + 2] = (Half)b; pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    /// <summary>
    /// An HDR grey ramp whose luma rises while alpha falls — anti-correlated channels for the
    /// dual-plane / CEM 15 search.
    /// </summary>
    public static Half[] DecorrelatedAlphaHdr(int width, int height)
    {
        Half[] pixels = new Half[width * height * Channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * Channels;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                Half up = (Half)(4.0f * t);
                pixels[idx] = up; pixels[idx + 1] = up; pixels[idx + 2] = up; pixels[idx + 3] = (Half)(4.0f * (1.0f - t));
            }
        }

        return pixels;
    }
}
