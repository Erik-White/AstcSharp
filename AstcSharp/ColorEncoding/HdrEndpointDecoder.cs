using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.Core;

namespace AstcSharp.ColorEncoding;

/// <summary>
/// Decodes HDR (High Dynamic Range) color endpoints for ASTC texture compression.
/// </summary>
/// <remarks>
/// HDR modes produce 12-bit intermediate values (0-4095) which are shifted left by 4
/// to produce the final 16-bit values (0-65520) stored as FP16 bit patterns.
/// </remarks>
internal static class HdrEndpointDecoder
{
    public static (RgbaHdrColor low, RgbaHdrColor high) DecodeHdrMode(ReadOnlySpan<int> vals, int maxValue, ColorEndpointMode mode)
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
    /// Decodes HDR endpoints from already-unquantized values.
    /// Called from the fused decode path where BISE decode + batch unquantize
    /// have already been performed.
    /// </summary>
    public static (RgbaHdrColor low, RgbaHdrColor high) DecodeHdrModeUnquantized(ReadOnlySpan<int> value, ColorEndpointMode mode)
    {
        return mode switch
        {
            ColorEndpointMode.HdrLumaLargeRange => UnpackHdrLuminanceLargeRangeCore(value[0], value[1]),
            ColorEndpointMode.HdrLumaSmallRange => UnpackHdrLuminanceSmallRangeCore(value[0], value[1]),
            ColorEndpointMode.HdrRgbBaseScale => UnpackHdrRgbBaseScaleCore(value[0], value[1], value[2], value[3]),
            ColorEndpointMode.HdrRgbDirect => UnpackHdrRgbDirectCore(value[0], value[1], value[2], value[3], value[4], value[5]),
            ColorEndpointMode.HdrRgbDirectLdrAlpha => UnpackHdrRgbDirectLdrAlphaCore(value),
            ColorEndpointMode.HdrRgbDirectHdrAlpha => UnpackHdrRgbDirectHdrAlphaCore(value),
            _ => throw new InvalidOperationException($"Mode {mode} is not an HDR mode")
        };
    }

    /// <summary>
    /// Performs an unsigned left shift of a signed value, avoiding undefined behavior
    /// that would occur with signed left shift of negative values.
    /// </summary>
    private static int SafeSignedLeftShift(int val, int shift) => (int)((uint)val << shift);

    /// <summary>
    /// Mode 2: HDR Luminance, Large Range.
    /// Ported from hdr_luminance_large_range_unpack() in ARM astc-encoder.
    /// </summary>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrLuminanceLargeRange(ReadOnlySpan<int> vals, int maxValue)
    {
        int v0 = vals.Length > 0 ? Quantization.UnquantizeCEValueFromRange(vals[0], maxValue) : 0;
        int v1 = vals.Length > 1 ? Quantization.UnquantizeCEValueFromRange(vals[1], maxValue) : 0;
        return UnpackHdrLuminanceLargeRangeCore(v0, v1);
    }

    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrLuminanceLargeRangeCore(int v0, int v1)
    {
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

        var low = new RgbaHdrColor((ushort)(y0 << 4), (ushort)(y0 << 4), (ushort)(y0 << 4), 0x7800);
        var high = new RgbaHdrColor((ushort)(y1 << 4), (ushort)(y1 << 4), (ushort)(y1 << 4), 0x7800);
        return (low, high);
    }

    /// <summary>
    /// Mode 3: HDR Luminance, Small Range.
    /// </summary>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrLuminanceSmallRange(ReadOnlySpan<int> vals, int maxValue)
    {
        int v0 = vals.Length > 0 ? Quantization.UnquantizeCEValueFromRange(vals[0], maxValue) : 0;
        int v1 = vals.Length > 1 ? Quantization.UnquantizeCEValueFromRange(vals[1], maxValue) : 0;
        return UnpackHdrLuminanceSmallRangeCore(v0, v1);
    }

    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrLuminanceSmallRangeCore(int v0, int v1)
    {
        int y0, y1;
        if ((v0 & 0x80) != 0)
        {
            y0 = ((v1 & 0xE0) << 4) | ((v0 & 0x7F) << 2);
            y1 = (v1 & 0x1F) << 2;
        }
        else
        {
            y0 = ((v1 & 0xF0) << 4) | ((v0 & 0x7F) << 1);
            y1 = (v1 & 0x0F) << 1;
        }

        y1 += y0;
        if (y1 > 0xFFF)
            y1 = 0xFFF;

        var low = new RgbaHdrColor((ushort)(y0 << 4), (ushort)(y0 << 4), (ushort)(y0 << 4), 0x7800);
        var high = new RgbaHdrColor((ushort)(y1 << 4), (ushort)(y1 << 4), (ushort)(y1 << 4), 0x7800);
        return (low, high);
    }

    /// <summary>
    /// Mode 7: HDR RGB, Base+Scale
    /// </summary>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbBaseScale(ReadOnlySpan<int> vals, int maxValue)
    {
        int v0 = vals.Length > 0 ? Quantization.UnquantizeCEValueFromRange(vals[0], maxValue) : 0;
        int v1 = vals.Length > 1 ? Quantization.UnquantizeCEValueFromRange(vals[1], maxValue) : 0;
        int v2 = vals.Length > 2 ? Quantization.UnquantizeCEValueFromRange(vals[2], maxValue) : 0;
        int v3 = vals.Length > 3 ? Quantization.UnquantizeCEValueFromRange(vals[3], maxValue) : 0;
        return UnpackHdrRgbBaseScaleCore(v0, v1, v2, v3);
    }

    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbBaseScaleCore(int v0, int v1, int v2, int v3)
    {
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

        int bit0 = (v1 >> 6) & 1;
        int bit1 = (v1 >> 5) & 1;
        int bit2 = (v2 >> 6) & 1;
        int bit3 = (v2 >> 5) & 1;
        int bit4 = (v3 >> 7) & 1;
        int bit5 = (v3 >> 6) & 1;
        int bit6 = (v3 >> 5) & 1;

        int ohm = 1 << mode;

        if ((ohm & 0x30) != 0) green |= bit0 << 6;
        if ((ohm & 0x3A) != 0) green |= bit1 << 5;
        if ((ohm & 0x30) != 0) blue |= bit2 << 6;
        if ((ohm & 0x3A) != 0) blue |= bit3 << 5;

        if ((ohm & 0x3D) != 0) scale |= bit6 << 5;
        if ((ohm & 0x2D) != 0) scale |= bit5 << 6;
        if ((ohm & 0x04) != 0) scale |= bit4 << 7;

        if ((ohm & 0x3B) != 0) red |= bit4 << 6;
        if ((ohm & 0x04) != 0) red |= bit3 << 6;

        if ((ohm & 0x10) != 0) red |= bit5 << 7;
        if ((ohm & 0x0F) != 0) red |= bit2 << 7;

        if ((ohm & 0x05) != 0) red |= bit1 << 8;
        if ((ohm & 0x0A) != 0) red |= bit0 << 8;

        if ((ohm & 0x05) != 0) red |= bit0 << 9;
        if ((ohm & 0x02) != 0) red |= bit6 << 9;

        if ((ohm & 0x01) != 0) red |= bit3 << 10;
        if ((ohm & 0x02) != 0) red |= bit5 << 10;

        // Shift amounts per mode (from ARM reference shamts table)
        ReadOnlySpan<int> shamts = [1, 1, 2, 3, 4, 5];
        int shamt = shamts[mode];

        red <<= shamt;
        green <<= shamt;
        blue <<= shamt;
        scale <<= shamt;

        if (mode != 5)
        {
            green = red - green;
            blue = red - blue;
        }

        // Swap components based on major component
        if (majcomp == 1)
        {
            (red, green) = (green, red);
        }
        else if (majcomp == 2)
        {
            (red, blue) = (blue, red);
        }

        // Low endpoint is base minus scale offset
        int red0 = red - scale;
        int green0 = green - scale;
        int blue0 = blue - scale;

        // Clamp to [0, 0xFFF]
        red = Math.Max(red, 0);
        green = Math.Max(green, 0);
        blue = Math.Max(blue, 0);
        red0 = Math.Max(red0, 0);
        green0 = Math.Max(green0, 0);
        blue0 = Math.Max(blue0, 0);

        var low = new RgbaHdrColor((ushort)(red0 << 4), (ushort)(green0 << 4), (ushort)(blue0 << 4), 0x7800);
        var high = new RgbaHdrColor((ushort)(red << 4), (ushort)(green << 4), (ushort)(blue << 4), 0x7800);
        return (low, high);
    }

    /// <summary>
    /// Mode 11: HDR RGB Direct
    /// </summary>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirect(ReadOnlySpan<int> vals, int maxValue)
    {
        int v0 = vals.Length > 0 ? Quantization.UnquantizeCEValueFromRange(vals[0], maxValue) : 0;
        int v1 = vals.Length > 1 ? Quantization.UnquantizeCEValueFromRange(vals[1], maxValue) : 0;
        int v2 = vals.Length > 2 ? Quantization.UnquantizeCEValueFromRange(vals[2], maxValue) : 0;
        int v3 = vals.Length > 3 ? Quantization.UnquantizeCEValueFromRange(vals[3], maxValue) : 0;
        int v4 = vals.Length > 4 ? Quantization.UnquantizeCEValueFromRange(vals[4], maxValue) : 0;
        int v5 = vals.Length > 5 ? Quantization.UnquantizeCEValueFromRange(vals[5], maxValue) : 0;
        return UnpackHdrRgbDirectCore(v0, v1, v2, v3, v4, v5);
    }

    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirectCore(int v0, int v1, int v2, int v3, int v4, int v5)
    {
        int modeval = ((v1 & 0x80) >> 7) | (((v2 & 0x80) >> 7) << 1) | (((v3 & 0x80) >> 7) << 2);
        int majcomp = ((v4 & 0x80) >> 7) | (((v5 & 0x80) >> 7) << 1);

        // Special case: majcomp == 3 (direct passthrough)
        if (majcomp == 3)
        {
            var low = new RgbaHdrColor(
                (ushort)(v0 << 8),
                (ushort)(v2 << 8),
                (ushort)((v4 & 0x7F) << 9),
                0x7800);
            var high = new RgbaHdrColor(
                (ushort)(v1 << 8),
                (ushort)(v3 << 8),
                (ushort)((v5 & 0x7F) << 9),
                0x7800);
            return (low, high);
        }

        int a = v0 | ((v1 & 0x40) << 2);
        int b0 = v2 & 0x3F;
        int b1 = v3 & 0x3F;
        int c = v1 & 0x3F;
        int d0 = v4 & 0x7F;
        int d1 = v5 & 0x7F;

        // dbits table from ARM reference
        ReadOnlySpan<int> dbitsTab = [7, 6, 7, 6, 5, 6, 5, 6];
        int dbits = dbitsTab[modeval];

        int bit0 = (v2 >> 6) & 1;
        int bit1 = (v3 >> 6) & 1;
        int bit2 = (v4 >> 6) & 1;
        int bit3 = (v5 >> 6) & 1;
        int bit4 = (v4 >> 5) & 1;
        int bit5 = (v5 >> 5) & 1;

        int ohmod = 1 << modeval;

        // Bit placement for 'a'
        if ((ohmod & 0xA4) != 0) a |= bit0 << 9;
        if ((ohmod & 0x8) != 0) a |= bit2 << 9;
        if ((ohmod & 0x50) != 0) a |= bit4 << 9;
        if ((ohmod & 0x50) != 0) a |= bit5 << 10;
        if ((ohmod & 0xA0) != 0) a |= bit1 << 10;
        if ((ohmod & 0xC0) != 0) a |= bit2 << 11;

        // Bit placement for 'c'
        if ((ohmod & 0x4) != 0) c |= bit1 << 6;
        if ((ohmod & 0xE8) != 0) c |= bit3 << 6;
        if ((ohmod & 0x20) != 0) c |= bit2 << 7;

        // Bit placement for 'b0' and 'b1'
        if ((ohmod & 0x5B) != 0) { b0 |= bit0 << 6; b1 |= bit1 << 6; }
        if ((ohmod & 0x12) != 0) { b0 |= bit2 << 7; b1 |= bit3 << 7; }

        // Bit placement for 'd0' and 'd1'
        if ((ohmod & 0xAF) != 0) { d0 |= bit4 << 5; d1 |= bit5 << 5; }
        if ((ohmod & 0x5) != 0) { d0 |= bit2 << 6; d1 |= bit3 << 6; }

        // Sign-extend d0 and d1 based on dbits
        int sxShamt = 32 - dbits;
        d0 = (d0 << sxShamt) >> sxShamt;
        d1 = (d1 << sxShamt) >> sxShamt;

        // Expand to 12 bits
        int valShamt = (modeval >> 1) ^ 3;
        a = SafeSignedLeftShift(a, valShamt);
        b0 = SafeSignedLeftShift(b0, valShamt);
        b1 = SafeSignedLeftShift(b1, valShamt);
        c = SafeSignedLeftShift(c, valShamt);
        d0 = SafeSignedLeftShift(d0, valShamt);
        d1 = SafeSignedLeftShift(d1, valShamt);

        // Compute color values per ARM reference
        int red1 = a;
        int green1 = a - b0;
        int blue1 = a - b1;
        int red0 = a - c;
        int green0 = a - b0 - c - d0;
        int blue0 = a - b1 - c - d1;

        // Clamp to [0, 4095]
        red0 = Math.Clamp(red0, 0, 0xFFF);
        green0 = Math.Clamp(green0, 0, 0xFFF);
        blue0 = Math.Clamp(blue0, 0, 0xFFF);
        red1 = Math.Clamp(red1, 0, 0xFFF);
        green1 = Math.Clamp(green1, 0, 0xFFF);
        blue1 = Math.Clamp(blue1, 0, 0xFFF);

        // Swap components based on major component
        if (majcomp == 1)
        {
            (red0, green0) = (green0, red0);
            (red1, green1) = (green1, red1);
        }
        else if (majcomp == 2)
        {
            (red0, blue0) = (blue0, red0);
            (red1, blue1) = (blue1, red1);
        }

        var lowResult = new RgbaHdrColor((ushort)(red0 << 4), (ushort)(green0 << 4), (ushort)(blue0 << 4), 0x7800);
        var highResult = new RgbaHdrColor((ushort)(red1 << 4), (ushort)(green1 << 4), (ushort)(blue1 << 4), 0x7800);
        return (lowResult, highResult);
    }

    /// <summary>
    /// Mode 14: HDR RGB Direct with LDR Alpha.
    /// RGB uses mode 11 logic; alpha uses LDR encoding (0-255 scaled to 0-65535).
    /// </summary>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirectLdrAlpha(ReadOnlySpan<int> vals, int maxValue)
    {
        var (rgbLow, rgbHigh) = UnpackHdrRgbDirect(vals, maxValue);

        int a0 = vals.Length > 6 ? Quantization.UnquantizeCEValueFromRange(vals[6], maxValue) : 0;
        int a1 = vals.Length > 7 ? Quantization.UnquantizeCEValueFromRange(vals[7], maxValue) : 0;

        ushort alpha0 = (ushort)(a0 * 257);
        ushort alpha1 = (ushort)(a1 * 257);

        var low = new RgbaHdrColor(rgbLow.R, rgbLow.G, rgbLow.B, alpha0);
        var high = new RgbaHdrColor(rgbHigh.R, rgbHigh.G, rgbHigh.B, alpha1);
        return (low, high);
    }

    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirectLdrAlphaCore(ReadOnlySpan<int> uv)
    {
        var (rgbLow, rgbHigh) = UnpackHdrRgbDirectCore(uv[0], uv[1], uv[2], uv[3], uv[4], uv[5]);

        ushort alpha0 = (ushort)(uv[6] * 257);
        ushort alpha1 = (ushort)(uv[7] * 257);

        var low = new RgbaHdrColor(rgbLow.R, rgbLow.G, rgbLow.B, alpha0);
        var high = new RgbaHdrColor(rgbHigh.R, rgbHigh.G, rgbHigh.B, alpha1);
        return (low, high);
    }

    /// <summary>
    /// Mode 15: HDR RGB Direct with HDR Alpha
    /// </summary>
    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirectHdrAlpha(ReadOnlySpan<int> vals, int maxValue)
    {
        var (rgbLow, rgbHigh) = UnpackHdrRgbDirect(vals, maxValue);

        int v6 = vals.Length > 6 ? Quantization.UnquantizeCEValueFromRange(vals[6], maxValue) : 0;
        int v7 = vals.Length > 7 ? Quantization.UnquantizeCEValueFromRange(vals[7], maxValue) : 0;

        var (alpha0, alpha1) = UnpackHdrAlpha(v6, v7);

        var low = new RgbaHdrColor(rgbLow.R, rgbLow.G, rgbLow.B, alpha0);
        var high = new RgbaHdrColor(rgbHigh.R, rgbHigh.G, rgbHigh.B, alpha1);
        return (low, high);
    }

    private static (RgbaHdrColor low, RgbaHdrColor high) UnpackHdrRgbDirectHdrAlphaCore(ReadOnlySpan<int> uv)
    {
        var (rgbLow, rgbHigh) = UnpackHdrRgbDirectCore(uv[0], uv[1], uv[2], uv[3], uv[4], uv[5]);

        var (alpha0, alpha1) = UnpackHdrAlpha(uv[6], uv[7]);

        var low = new RgbaHdrColor(rgbLow.R, rgbLow.G, rgbLow.B, alpha0);
        var high = new RgbaHdrColor(rgbHigh.R, rgbHigh.G, rgbHigh.B, alpha1);
        return (low, high);
    }

    /// <summary>
    /// Decodes HDR alpha values
    /// </summary>
    private static (ushort low, ushort high) UnpackHdrAlpha(int v6, int v7)
    {
        int selector = ((v6 >> 7) & 1) | ((v7 >> 6) & 2);
        v6 &= 0x7F;
        v7 &= 0x7F;

        int a0, a1;

        if (selector == 3)
        {
            // Simple mode: direct 7-bit values shifted to 12-bit
            a0 = v6 << 5;
            a1 = v7 << 5;
        }
        else
        {
            // Complex mode: base + sign-extended offset
            v6 |= (v7 << (selector + 1)) & 0x780;
            v7 &= (0x3F >> selector);
            v7 ^= 32 >> selector;
            v7 -= 32 >> selector;
            v6 <<= (4 - selector);
            v7 <<= (4 - selector);
            v7 += v6;

            if (v7 < 0)
                v7 = 0;
            else if (v7 > 0xFFF)
                v7 = 0xFFF;

            a0 = v6;
            a1 = v7;
        }

        a0 = Math.Clamp(a0, 0, 0xFFF);
        a1 = Math.Clamp(a1, 0, 0xFFF);

        return ((ushort)(a0 << 4), (ushort)(a1 << 4));
    }
}
