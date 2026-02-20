using AstcSharp.BiseEncoding;
using AstcSharp.Core;

namespace AstcSharp.ColorEncoding;

/// <summary>
/// Decodes HDR (High Dynamic Range) color endpoints for ASTC texture compression.
/// </summary>
/// <remarks>
/// This implementation is based on the ARM astc-encoder reference implementation,
/// specifically the astcenc_color_unquantize.cpp file. HDR modes output 12-bit
/// intermediate values (0-4095) which are then shifted left by 4 to produce the
/// final 16-bit values (0-65520) stored in ushort format.
/// </remarks>
internal static class HdrEndpointDecoder
{
    /// <summary>
    /// Decodes HDR color endpoints based on the specified mode.
    /// </summary>
    /// <param name="vals">Quantized integer values from the ASTC block</param>
    /// <param name="maxValue">Maximum quantization value</param>
    /// <param name="mode">The HDR color endpoint mode</param>
    /// <returns>A pair of HDR colors representing the low and high endpoints</returns>
    public static (RgbaHdrColor low, RgbaHdrColor high) DecodeHdrMode(List<int> vals, int maxValue, ColorEndpointMode mode)
    {
        return mode switch
        {
            ColorEndpointMode.HdrLumaLargeRange => UnpackHdrLuminanceLargeRange(vals, maxValue),
            ColorEndpointMode.HdrLumaSmallRange => UnpackHdrLuminanceSmallRange(vals, maxValue),
            ColorEndpointMode.HdrRgbBaseScale => UnpackHdrRgbBaseScale(vals, maxValue),
            ColorEndpointMode.HdrRgbDirect => UnpackHdrRgbDirect(vals, maxValue),
            ColorEndpointMode.HdrRgbDirectLdrAlpha => UnpackHdrRgbDirectLdrAlpha(vals, maxValue),
            ColorEndpointMode.HdrRgbDirectHdrAlpha => UnpackHdrRgbDirectHdrAlpha(vals, maxValue),
            _ => throw new InvalidOperationException($"Mode {mode} is not an HDR mode")
        };
    }

    /// <summary>
    /// Mode 2: HDR Luminance, Large Range
    /// </summary>
    /// <remarks>
    /// Ported from hdr_luminance_large_range_unpack() in ARM astc-encoder.
    /// Produces luminance values with a large dynamic range.
    /// Alpha is set to a fixed HDR value (0x7800).
    /// </remarks>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrLuminanceLargeRange(List<int> vals, int maxValue)
    {
        // Ensure we have at least 2 values
        var v = new int[2];
        for (int i = 0; i < 2; ++i)
            v[i] = i < vals.Count ? vals[i] : 0;

        // Unquantize the values
        int v0 = Quantization.UnquantizeCEValueFromRange(v[0], maxValue);
        int v1 = Quantization.UnquantizeCEValueFromRange(v[1], maxValue);

        int y0, y1;

        if (v1 >= v0)
        {
            y0 = v0 << 4;
            y1 = v1 << 4;
        }
        else
        {
            y0 = (v1 << 4) + 8;
            y1 = (v0 << 4) - 8;
        }

        // Luminance is replicated to RGB, alpha is fixed at 0x7800
        var low = new RgbaHdrColor((ushort)y0, (ushort)y0, (ushort)y0, (ushort)0x7800);
        var high = new RgbaHdrColor((ushort)y1, (ushort)y1, (ushort)y1, (ushort)0x7800);

        return (low, high);
    }

    /// <summary>
    /// Mode 3: HDR Luminance, Small Range
    /// </summary>
    /// <remarks>
    /// Ported from hdr_luminance_small_range_unpack() in ARM astc-encoder.
    /// Produces luminance values with a smaller dynamic range but better precision.
    /// Uses differential encoding.
    /// </remarks>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrLuminanceSmallRange(List<int> vals, int maxValue)
    {
        // Ensure we have at least 2 values
        var v = new int[2];
        for (int i = 0; i < 2; ++i)
            v[i] = i < vals.Count ? vals[i] : 0;

        // Unquantize the values
        int v0 = Quantization.UnquantizeCEValueFromRange(v[0], maxValue);
        int v1 = Quantization.UnquantizeCEValueFromRange(v[1], maxValue);

        int y0, y1;

        if ((v0 & 0x80) != 0)
        {
            // Mode where top bit of v0 is set
            y0 = ((v1 & 0xE0) << 4) | ((v0 & 0x7F) << 2);
            y1 = (v1 & 0x1F) << 2;
        }
        else
        {
            // Mode where top bit of v0 is clear
            y0 = ((v1 & 0xF0) << 4) | ((v0 & 0x7F) << 1);
            y1 = (v1 & 0x0F) << 1;
        }

        // Differential encoding: y1 is relative to y0
        y1 += y0;

        // Clamp to 12-bit range (0-4095)
        y0 = Math.Clamp(y0, 0, 0xFFF);
        y1 = Math.Clamp(y1, 0, 0xFFF);

        // Shift left by 4 to produce final 16-bit values
        y0 <<= 4;
        y1 <<= 4;

        // Luminance is replicated to RGB, alpha is fixed at 0x7800
        var low = new RgbaHdrColor((ushort)y0, (ushort)y0, (ushort)y0, (ushort)0x7800);
        var high = new RgbaHdrColor((ushort)y1, (ushort)y1, (ushort)y1, (ushort)0x7800);

        return (low, high);
    }

    /// <summary>
    /// Mode 7: HDR RGB, Base+Scale
    /// </summary>
    /// <remarks>
    /// Ported from hdr_rgbo_unpack() in ARM astc-encoder.
    /// One endpoint is a base color, the other is derived by scaling.
    /// </remarks>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbBaseScale(List<int> vals, int maxValue)
    {
        // Ensure we have at least 4 values
        var v = new int[4];
        for (int i = 0; i < 4; ++i)
            v[i] = i < vals.Count ? vals[i] : 0;

        // Unquantize the values
        int v0 = Quantization.UnquantizeCEValueFromRange(v[0], maxValue);
        int v1 = Quantization.UnquantizeCEValueFromRange(v[1], maxValue);
        int v2 = Quantization.UnquantizeCEValueFromRange(v[2], maxValue);
        int v3 = Quantization.UnquantizeCEValueFromRange(v[3], maxValue);

        // Extract mode bits
        int modeval = ((v0 & 0xC0) >> 6) | (((v1 & 0x80) >> 7) << 2) | (((v2 & 0x80) >> 7) << 3);

        int majcomp;
        int mode;

        if ((modeval & 0xC) != 0xC)
        {
            majcomp = modeval >> 2;
            mode = modeval & 3;
        }
        else if (modeval != 0xF)
        {
            majcomp = modeval & 3;
            mode = 4;
        }
        else
        {
            majcomp = 0;
            mode = 5;
        }

        int red = v0 & 0x3F;
        int green = v1 & 0x1F;
        int blue = v2 & 0x1F;
        int scale = v3 & 0x1F;

        int x0 = (v1 >> 6) & 1;
        int x1 = (v1 >> 5) & 1;
        int x2 = (v2 >> 6) & 1;
        int x3 = (v2 >> 5) & 1;
        int x4 = (v3 >> 7) & 1;
        int x5 = (v3 >> 6) & 1;
        int x6 = (v3 >> 5) & 1;

        int ohm = 1 << mode;

        if ((ohm & 0x30) != 0)
            green |= x0 << 6;
        if ((ohm & 0x3A) != 0)
            green |= x1 << 5;
        if ((ohm & 0x30) != 0)
            blue |= x2 << 6;
        if ((ohm & 0x3A) != 0)
            blue |= x3 << 5;

        if ((ohm & 0x3D) != 0)
            scale |= x6 << 5;
        if ((ohm & 0x2D) != 0)
            scale |= x5 << 6;
        if ((ohm & 0x04) != 0)
            scale |= x4 << 7;

        if ((ohm & 0x3B) != 0)
            red |= x4 << 6;
        if ((ohm & 0x04) != 0)
            red |= x3 << 6;

        if ((ohm & 0x10) != 0)
            red |= x5 << 7;
        if ((ohm & 0x0F) != 0)
            red |= x2 << 7;

        if ((ohm & 0x05) != 0)
            red |= x1 << 8;
        if ((ohm & 0x0A) != 0)
            red |= x0 << 8;

        if ((ohm & 0x05) != 0)
            red |= x0 << 9;
        if ((ohm & 0x02) != 0)
            red |= x6 << 9;

        if ((ohm & 0x01) != 0)
            red |= x3 << 10;
        if ((ohm & 0x02) != 0)
            red |= x5 << 10;

        // Expand values based on bit counts
        static int Expand(int v, int bits)
        {
            return bits switch
            {
                6 => (v << 5) | (v >> 1),
                7 => (v << 4) | (v >> 3),
                8 => (v << 3) | (v >> 5),
                9 => (v << 2) | (v >> 7),
                10 => (v << 1) | (v >> 9),
                11 => v,
                _ => v
            };
        }

        int shamt = (mode >> 1) ^ 3;
        red <<= shamt;
        green <<= shamt;
        blue <<= shamt;
        scale <<= shamt;

        if (mode != 5)
        {
            green = red - green;
            blue = red - blue;
        }

        // Expand to 12-bit
        red = Expand(red, (mode < 4) ? 9 : 11);
        green = Expand(green, (mode < 4) ? 9 : 11);
        blue = Expand(blue, (mode < 4) ? 9 : 11);
        scale = Expand(scale, (mode < 4) ? 8 : 11);

        // Clamp to 12-bit
        red = Math.Clamp(red, 0, 0xFFF);
        green = Math.Clamp(green, 0, 0xFFF);
        blue = Math.Clamp(blue, 0, 0xFFF);
        scale = Math.Clamp(scale, 0, 0xFFF);

        // Apply scale
        int scaledRed = (red * scale) >> 12;
        int scaledGreen = (green * scale) >> 12;
        int scaledBlue = (blue * scale) >> 12;

        // Swap components based on major component
        int c0, c1, c2;
        if (majcomp == 1)
        {
            c0 = scaledGreen;
            c1 = scaledRed;
            c2 = scaledBlue;
        }
        else if (majcomp == 2)
        {
            c0 = scaledBlue;
            c1 = scaledGreen;
            c2 = scaledRed;
        }
        else
        {
            c0 = scaledRed;
            c1 = scaledGreen;
            c2 = scaledBlue;
        }

        // Shift left by 4 for final 16-bit values
        var low = new RgbaHdrColor((ushort)(c0 << 4), (ushort)(c1 << 4), (ushort)(c2 << 4), (ushort)0xFFFF);

        // High endpoint uses unscaled base color
        int hc0, hc1, hc2;
        if (majcomp == 1)
        {
            hc0 = green;
            hc1 = red;
            hc2 = blue;
        }
        else if (majcomp == 2)
        {
            hc0 = blue;
            hc1 = green;
            hc2 = red;
        }
        else
        {
            hc0 = red;
            hc1 = green;
            hc2 = blue;
        }

        var high = new RgbaHdrColor((ushort)(hc0 << 4), (ushort)(hc1 << 4), (ushort)(hc2 << 4), (ushort)0xFFFF);

        return (low, high);
    }

    /// <summary>
    /// Mode 11: HDR RGB Direct
    /// </summary>
    /// <remarks>
    /// Ported from hdr_rgb_unpack() in ARM astc-encoder.
    /// Direct encoding of RGB values with high bit depth.
    /// </remarks>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirect(List<int> vals, int maxValue)
    {
        // Ensure we have at least 6 values
        var v = new int[6];
        for (int i = 0; i < 6; ++i)
            v[i] = i < vals.Count ? vals[i] : 0;

        // Unquantize the values
        int v0 = Quantization.UnquantizeCEValueFromRange(v[0], maxValue);
        int v1 = Quantization.UnquantizeCEValueFromRange(v[1], maxValue);
        int v2 = Quantization.UnquantizeCEValueFromRange(v[2], maxValue);
        int v3 = Quantization.UnquantizeCEValueFromRange(v[3], maxValue);
        int v4 = Quantization.UnquantizeCEValueFromRange(v[4], maxValue);
        int v5 = Quantization.UnquantizeCEValueFromRange(v[5], maxValue);

        // Extract mode from top bits
        int majcomp = ((v4 & 0x80) >> 7) | ((v5 & 0x80) >> 6);

        // Special case: direct passthrough
        if (majcomp == 3)
        {
            int pr0 = ((v0 & 0xF0) << 4) | ((v2 & 0xF0));
            int pr1 = ((v1 & 0xF0) << 4) | ((v3 & 0xF0));
            int pg0 = ((v0 & 0x0F) << 8) | ((v2 & 0x0F) << 4);
            int pg1 = ((v1 & 0x0F) << 8) | ((v3 & 0x0F) << 4);
            int pb0 = ((v4 & 0x7F) << 5);
            int pb1 = ((v5 & 0x7F) << 5);

            var lowPassthrough = new RgbaHdrColor((ushort)(pr0 << 4), (ushort)(pg0 << 4), (ushort)(pb0 << 4), (ushort)0xFFFF);
            var highPassthrough = new RgbaHdrColor((ushort)(pr1 << 4), (ushort)(pg1 << 4), (ushort)(pb1 << 4), (ushort)0xFFFF);
            return (lowPassthrough, highPassthrough);
        }

        // Extract mode bits
        int mode = ((v1 & 0x80) >> 7) | ((v2 & 0x80) >> 6) | ((v3 & 0x80) >> 5);

        int va = v0 | ((v1 & 0x40) << 2);
        int vb0 = v2 & 0x3F;
        int vb1 = v3 & 0x3F;
        int vc = v1 & 0x3F;
        int vd0 = v4 & 0x7F;
        int vd1 = v5 & 0x7F;

        // Bit handling based on mode
        static int SignExtend(int val, int bits)
        {
            int shift = 32 - bits;
            return (val << shift) >> shift;
        }

        int x0 = (v2 >> 6) & 1;
        int x1 = (v3 >> 6) & 1;

        int ohm = 1 << mode;

        if ((ohm & 0xA4) != 0)
            va |= x0 << 9;
        if ((ohm & 0x08) != 0)
            va |= x1 << 9;
        if ((ohm & 0x50) != 0)
            va |= x0 << 10;
        if ((ohm & 0x50) != 0)
            va |= x1 << 11;
        if ((ohm & 0xA0) != 0)
            va |= x1 << 10;
        if ((ohm & 0xC0) != 0)
            va |= x1 << 11;

        if ((ohm & 0x04) != 0)
        {
            vb0 |= x0 << 6;
            vb1 |= x1 << 6;
        }
        if ((ohm & 0xE8) != 0)
        {
            vb0 |= x0 << 7;
            vb1 |= x1 << 7;
        }
        if ((ohm & 0x20) != 0)
        {
            vc |= x0 << 6;
        }
        if ((ohm & 0x5B) != 0)
        {
            vc |= x0 << 7;
            vc |= x1 << 7;
        }
        if ((ohm & 0x5B) != 0)
        {
            vd0 |= x0 << 7;
            vd1 |= x1 << 7;
        }

        // Sign extend based on mode
        int shamt = (mode >> 1) ^ 3;
        va <<= shamt;
        vb0 <<= shamt;
        vb1 <<= shamt;
        vc <<= shamt;
        vd0 <<= shamt;
        vd1 <<= shamt;

        // Sign extend vb0, vb1, vc, vd0, vd1
        int signbits = mode < 4 ? 9 : 11;
        vb0 = SignExtend(vb0, signbits);
        vb1 = SignExtend(vb1, signbits);
        vc = SignExtend(vc, signbits);
        vd0 = SignExtend(vd0, signbits);
        vd1 = SignExtend(vd1, signbits);

        // Add offsets to base
        vb0 += va;
        vb1 += va;
        vc += va;
        vd0 += va;
        vd1 += va;

        // Clamp to 0-4095 range
        va = Math.Clamp(va, 0, 0xFFF);
        vb0 = Math.Clamp(vb0, 0, 0xFFF);
        vb1 = Math.Clamp(vb1, 0, 0xFFF);
        vc = Math.Clamp(vc, 0, 0xFFF);
        vd0 = Math.Clamp(vd0, 0, 0xFFF);
        vd1 = Math.Clamp(vd1, 0, 0xFFF);

        // Arrange components based on major component
        int r0, g0, b0, r1, g1, b1;
        if (majcomp == 1)
        {
            r0 = vb0; g0 = va; b0 = vc;
            r1 = vb1; g1 = va; b1 = vd1;
        }
        else if (majcomp == 2)
        {
            r0 = vc; g0 = vb0; b0 = va;
            r1 = vd1; g1 = vb1; b1 = va;
        }
        else
        {
            r0 = va; g0 = vb0; b0 = vc;
            r1 = va; g1 = vb1; b1 = vd1;
        }

        // Shift left by 4 for final 16-bit values
        var low = new RgbaHdrColor((ushort)(r0 << 4), (ushort)(g0 << 4), (ushort)(b0 << 4), (ushort)0xFFFF);
        var high = new RgbaHdrColor((ushort)(r1 << 4), (ushort)(g1 << 4), (ushort)(b1 << 4), (ushort)0xFFFF);

        return (low, high);
    }

    /// <summary>
    /// Mode 14: HDR RGB Direct with LDR Alpha
    /// </summary>
    /// <remarks>
    /// RGB channels use HDR encoding (mode 11 logic), alpha uses LDR (0-255 scaled to 0-65535).
    /// </remarks>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirectLdrAlpha(List<int> vals, int maxValue)
    {
        // RGB portion uses mode 11 logic
        var (rgbLow, rgbHigh) = UnpackHdrRgbDirect(vals, maxValue);

        // Alpha portion uses LDR encoding
        var alphaVals = new int[2];
        if (vals.Count > 6) alphaVals[0] = vals[6];
        if (vals.Count > 7) alphaVals[1] = vals[7];

        int a0 = Quantization.UnquantizeCEValueFromRange(alphaVals[0], maxValue);
        int a1 = Quantization.UnquantizeCEValueFromRange(alphaVals[1], maxValue);

        // Scale LDR alpha (0-255) to HDR range (0-65535) using multiply by 257
        ushort alpha0 = (ushort)(a0 * 257);
        ushort alpha1 = (ushort)(a1 * 257);

        var low = new RgbaHdrColor(rgbLow.R, rgbLow.G, rgbLow.B, alpha0);
        var high = new RgbaHdrColor(rgbHigh.R, rgbHigh.G, rgbHigh.B, alpha1);

        return (low, high);
    }

    /// <summary>
    /// Mode 15: HDR RGB Direct with HDR Alpha
    /// </summary>
    /// <remarks>
    /// Ported from hdr_rgb_hdr_alpha_unpack() in ARM astc-encoder.
    /// Both RGB and alpha use HDR encoding.
    /// </remarks>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirectHdrAlpha(List<int> vals, int maxValue)
    {
        // RGB portion uses mode 11 logic (first 6 values)
        var (rgbLow, rgbHigh) = UnpackHdrRgbDirect(vals, maxValue);

        // Alpha portion uses HDR alpha encoding (values 6-7)
        var alphaVals = new int[2];
        if (vals.Count > 6) alphaVals[0] = vals[6];
        if (vals.Count > 7) alphaVals[1] = vals[7];

        var (alpha0, alpha1) = UnpackHdrAlpha(alphaVals, maxValue);

        var low = new RgbaHdrColor(rgbLow.R, rgbLow.G, rgbLow.B, alpha0);
        var high = new RgbaHdrColor(rgbHigh.R, rgbHigh.G, rgbHigh.B, alpha1);

        return (low, high);
    }

    /// <summary>
    /// Helper function to unpack HDR alpha values.
    /// </summary>
    /// <remarks>
    /// Ported from hdr_alpha_unpack() in ARM astc-encoder.
    /// </remarks>
    private static (ushort low, ushort high) UnpackHdrAlpha(int[] vals, int maxValue)
    {
        int v6 = Quantization.UnquantizeCEValueFromRange(vals[0], maxValue);
        int v7 = Quantization.UnquantizeCEValueFromRange(vals[1], maxValue);

        int selector = ((v6 >> 7) & 1) | ((v7 >> 6) & 2);

        int a0, a1;

        switch (selector)
        {
            case 0:
                {
                    // Mode 0: 8-bit with offset
                    a0 = v6 & 0x7F;
                    a1 = v7 & 0x7F;
                    a0 <<= 5;
                    a1 <<= 5;
                }
                break;
            case 1:
                {
                    // Mode 1: Base+offset
                    a0 = (v6 & 0x7F) | ((v7 & 0x40) << 1);
                    a1 = (v7 & 0x3F);
                    a0 <<= 4;
                    a1 <<= 4;
                    a1 += a0;
                    a1 = Math.Clamp(a1, 0, 0xFFF);
                }
                break;
            case 2:
                {
                    // Mode 2: 7-bit plus extra bit
                    a0 = (v6 & 0x7F) | ((v7 & 0x20) << 2);
                    a1 = (v7 & 0x1F);
                    a0 <<= 4;
                    a1 <<= 4;
                    a1 = a0 - a1;
                    a1 = Math.Clamp(a1, 0, 0xFFF);
                }
                break;
            case 3:
            default:
                {
                    // Mode 3: Direct 6-bit
                    a0 = (v6 & 0x7F) | ((v7 & 0x30) << 3);
                    a1 = (v7 & 0x0F) | ((v6 & 0x40) >> 2);
                    a0 <<= 3;
                    a1 <<= 3;
                }
                break;
        }

        // Clamp and shift to 16-bit
        a0 = Math.Clamp(a0, 0, 0xFFF);
        a1 = Math.Clamp(a1, 0, 0xFFF);

        return ((ushort)(a0 << 4), (ushort)(a1 << 4));
    }
}
