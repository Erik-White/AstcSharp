using AstcSharp.BiseEncoding;
using AstcSharp.Core;

namespace AstcSharp.ColorEncoding;

internal static class EndpointCodec
{
    // TODO: Move these two to RGBA color extensions?
    private static int[] QuantizeColorArray(RgbaColor c, int maxValue)
    {
        var arr = new int[RgbaColor.BytesPerPixel];
        for (int i = 0; i < RgbaColor.BytesPerPixel; ++i) arr[i] = Quantization.QuantizeCEValueToRange(c[i], maxValue);
        return arr;
    }

    private static int[] UnquantizeArray(int[] v, int maxValue)
    {
        var res = new int[v.Length];
        for (int i = 0; i < v.Length; ++i) res[i] = Quantization.UnquantizeCEValueFromRange(v[i], maxValue);
        return res;
    }

    private static void UnquantizeInline(Span<int> v, int maxValue)
    {
        for (int i = 0; i < v.Length; ++i) v[i] = Quantization.UnquantizeCEValueFromRange(v[i], maxValue);
    }

    // TODO: Move to a separate file
    private class QuantizedEndpointPair
    {
        private readonly RgbaColor _originalLow;
        private readonly RgbaColor _originalHigh;
        private readonly int[] _quantizedLow;
        private readonly int[] _quantizedHigh;
        private readonly int[] _unquantizedLow;
        private readonly int[] _unquantizedHigh;

        public QuantizedEndpointPair(RgbaColor low, RgbaColor high, int maxValue)
        {
            _originalLow = low;
            _originalHigh = high;
            _quantizedLow = QuantizeColorArray(low, maxValue);
            _quantizedHigh = QuantizeColorArray(high, maxValue);
            _unquantizedLow = UnquantizeArray(_quantizedLow, maxValue);
            _unquantizedHigh = UnquantizeArray(_quantizedHigh, maxValue);
        }

        public int[] QuantizedLow() => _quantizedLow;
        public int[] QuantizedHigh() => _quantizedHigh;
        public int[] UnquantizedLow() => _unquantizedLow;
        public int[] UnquantizedHigh() => _unquantizedHigh;
        public RgbaColor OriginalLow() => _originalLow;
        public RgbaColor OriginalHigh() => _originalHigh;
    }

    private class CEEncodingOption
    {
        private readonly int _squaredError;
        private readonly QuantizedEndpointPair _quantizedEndpoints;
        private readonly bool _swapEndpoints;
        private readonly bool _blueContract;
        private readonly bool _useOffsetMode;

        public CEEncodingOption(
            int squaredError,
            QuantizedEndpointPair quantizedEndpoints,
            bool swapEndpoints,
            bool blueContract,
            bool useOffsetMode)
        {
            _squaredError = squaredError;
            _quantizedEndpoints = quantizedEndpoints;
            _swapEndpoints = swapEndpoints;
            _blueContract = blueContract;
            _useOffsetMode = useOffsetMode;
        }

        public bool Pack(bool hasAlpha, out ColorEndpointMode endpointMode, List<int> values, ref bool needsWeightSwap)
        {
            endpointMode = ColorEndpointMode.LdrLumaDirect;
            var unquantizedLow = _quantizedEndpoints.UnquantizedLow();
            var unquantizedHigh = _quantizedEndpoints.UnquantizedHigh();

            var uLow = (int[])unquantizedLow.Clone();
            var uHigh = (int[])unquantizedHigh.Clone();

            if (_useOffsetMode)
            {
                for (int i = 0; i < 4; ++i)
                {
                    (uHigh[i], uLow[i]) = BitOperations.TransferPrecision(uHigh[i], uLow[i]);
                }
            }

            int s0 = 0, s1 = 0;
            for (int i = 0; i < 3; ++i)
            {
                s0 += uLow[i];
                s1 += uHigh[i];
            }

            bool swapVals = false;
            if (_useOffsetMode)
            {
                if (_blueContract)
                {
                    swapVals = s1 >= 0;
                }
                else
                {
                    swapVals = s1 < 0;
                }

                if (swapVals) return false;
            }
            else
            {
                if (_blueContract)
                {
                    if (s1 == s0) return false;
                    swapVals = s1 > s0;
                    needsWeightSwap = !needsWeightSwap;
                }
                else
                {
                    swapVals = s1 < s0;
                }
            }

            var quantLow = _quantizedEndpoints.QuantizedLow();
            var quantHigh = _quantizedEndpoints.QuantizedHigh();

            var qLow = (int[])quantLow.Clone();
            var qHigh = (int[])quantHigh.Clone();

            if (swapVals)
            {
                if (_useOffsetMode) throw new InvalidOperationException();
                var tmp = qLow; qLow = qHigh; qHigh = tmp;
                needsWeightSwap = !needsWeightSwap;
            }

            values[0] = qLow[0];
            values[1] = qHigh[0];
            values[2] = qLow[1];
            values[3] = qHigh[1];
            values[4] = qLow[2];
            values[5] = qHigh[2];

            if (_useOffsetMode)
            {
                endpointMode = ColorEndpointMode.LdrRgbBaseOffset;
            }
            else
            {
                endpointMode = ColorEndpointMode.LdrRgbDirect;
            }

            if (hasAlpha)
            {
                values[6] = qLow[3];
                values[7] = qHigh[3];
                if (_useOffsetMode) endpointMode = ColorEndpointMode.LdrRgbaBaseOffset;
                else endpointMode = ColorEndpointMode.LdrRgbaDirect;
            }

            if (_swapEndpoints)
            {
                needsWeightSwap = !needsWeightSwap;
            }

            return true;
        }

        public bool BlueContract() => _blueContract;
        public int Error() => _squaredError;
    }

    public static bool UsesBlueContract(int maxValue, ColorEndpointMode mode, List<int> values)
    {
        int numVals = mode.GetColorValuesCount();
        ArgumentOutOfRangeException.ThrowIfLessThan(values.Count, numVals);

        switch (mode)
        {
            case ColorEndpointMode.LdrRgbDirect:
            case ColorEndpointMode.LdrRgbaDirect:
                {
                    int kNumVals = Math.Max(ColorEndpointMode.LdrRgbDirect.GetColorValuesCount(), ColorEndpointMode.LdrRgbaDirect.GetColorValuesCount());
                    var v = new int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) v[i] = i < values.Count ? values[i] : 0;
                    var uv = UnquantizeArray(v, maxValue);
                    int s0 = uv[0] + uv[2] + uv[4];
                    int s1 = uv[1] + uv[3] + uv[5];
                    return s0 > s1;
                }
            case ColorEndpointMode.LdrRgbBaseOffset:
            case ColorEndpointMode.LdrRgbaBaseOffset:
                {
                    int kNumVals = Math.Max(ColorEndpointMode.LdrRgbBaseOffset.GetColorValuesCount(), ColorEndpointMode.LdrRgbaBaseOffset.GetColorValuesCount());
                    var v = new int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) v[i] = i < values.Count ? values[i] : 0;
                    var uv = UnquantizeArray(v, maxValue);
                    var (b0, a0) = BitOperations.TransferPrecision(uv[1], uv[0]);
                    var (b1, a1) = BitOperations.TransferPrecision(uv[3], uv[2]);
                    var (b2, a2) = BitOperations.TransferPrecision(uv[5], uv[4]);
                    return (b0 + b1 + b2) < 0;
                }
            default:
                return false;
        }
    }

    // TODO: Extract an interface and implement instances for each encoding mode
    public static bool EncodeColorsForMode(RgbaColor endpointLowRgba, RgbaColor endpointHighRgba, int maxValue, EndpointEncodingMode encodingMode, out ColorEndpointMode astcMode, List<int> vals)
    {
        bool needsWeightSwap = false;
        astcMode = ColorEndpointMode.LdrLumaDirect;
        int numVals = encodingMode.GetValuesCount();
        for (int i = vals.Count; i < numVals; ++i) vals.Add(0);

        switch (encodingMode)
        {
            case EndpointEncodingMode.DirectLuma:
                return EncodeColorsLuma(endpointLowRgba, endpointHighRgba, maxValue, out astcMode, vals);
            case EndpointEncodingMode.DirectLumaAlpha:
                {
                    int avg1 = endpointLowRgba.Average;
                    int avg2 = endpointHighRgba.Average;
                    vals[0] = Quantization.QuantizeCEValueToRange(avg1, maxValue);
                    vals[1] = Quantization.QuantizeCEValueToRange(avg2, maxValue);
                    vals[2] = Quantization.QuantizeCEValueToRange(endpointLowRgba[3], maxValue);
                    vals[3] = Quantization.QuantizeCEValueToRange(endpointHighRgba[3], maxValue);
                    astcMode = ColorEndpointMode.LdrLumaAlphaDirect;
                }
                break;
            case EndpointEncodingMode.BaseScaleRgb:
            case EndpointEncodingMode.BaseScaleRgba:
                {
                    var basec = endpointHighRgba;
                    var scaled = endpointLowRgba;

                    int numChannelsGe = 0;
                    for (int i = 0; i < 3; ++i) numChannelsGe += endpointHighRgba[i] >= endpointLowRgba[i] ? 1 : 0;

                    if (numChannelsGe < 2)
                    {
                        needsWeightSwap = true;
                        var t = basec; basec = scaled; scaled = t;
                    }

                    var qBase = QuantizeColorArray(basec, maxValue);
                    var uqBase = UnquantizeArray(qBase, maxValue);

                    int numSamples = 0;
                    int scaleSum = 0;
                    for (int i = 0; i < 3; ++i)
                    {
                        int x = uqBase[i];
                        if (x != 0)
                        {
                            ++numSamples;
                            scaleSum += (scaled[i] * 256) / x;
                        }
                    }

                    vals[0] = qBase[0];
                    vals[1] = qBase[1];
                    vals[2] = qBase[2];
                    if (numSamples > 0)
                    {
                        int avgScale = Math.Clamp(scaleSum / numSamples, 0, 255);
                        vals[3] = Quantization.QuantizeCEValueToRange(avgScale, maxValue);
                    }
                    else
                    {
                        vals[3] = maxValue;
                    }
                    astcMode = ColorEndpointMode.LdrRgbBaseScale;

                    if (encodingMode == EndpointEncodingMode.BaseScaleRgba)
                    {
                        vals[4] = Quantization.QuantizeCEValueToRange(scaled[3], maxValue);
                        vals[5] = Quantization.QuantizeCEValueToRange(basec[3], maxValue);
                        astcMode = ColorEndpointMode.LdrRgbBaseScaleTwoA;
                    }
                }
                break;
            case EndpointEncodingMode.DirectRbg:
            case EndpointEncodingMode.DirectRgba:
                return EncodeColorsRGBA(endpointLowRgba, endpointHighRgba, maxValue, encodingMode == EndpointEncodingMode.DirectRgba, out astcMode, vals);
            default:
                throw new InvalidOperationException("Unimplemented color encoding.");
        }

        return needsWeightSwap;
    }

    private static bool EncodeColorsLuma(RgbaColor endpointLow, RgbaColor endpointHigh, int maxValue, out ColorEndpointMode astcMode, List<int> vals)
    {
        astcMode = ColorEndpointMode.LdrLumaDirect;
        ArgumentOutOfRangeException.ThrowIfLessThan(vals.Count, 2);

        int avg1 = endpointLow.Average;
        int avg2 = endpointHigh.Average;

        bool needsWeightSwap = false;
        if (avg1 > avg2) { needsWeightSwap = true; var t = avg1; avg1 = avg2; avg2 = t; }

        int offset = Math.Min(avg2 - avg1, 0x3F);
        int quantOffLow = Quantization.QuantizeCEValueToRange((avg1 & 0x3F) << 2, maxValue);
        int quantOffHigh = Quantization.QuantizeCEValueToRange((avg1 & 0xC0) | offset, maxValue);

        int quantLow = Quantization.QuantizeCEValueToRange(avg1, maxValue);
        int quantHigh = Quantization.QuantizeCEValueToRange(avg2, maxValue);

        vals[0] = quantOffLow;
        vals[1] = quantOffHigh;
        var (decLowOff, decHighOff) = DecodeColorsForMode(vals.ToArray(), maxValue, ColorEndpointMode.LdrLumaBaseOffset);

        vals[0] = quantLow;
        vals[1] = quantHigh;
        var (decLowDir, decHighDir) = DecodeColorsForMode(vals.ToArray(), maxValue, ColorEndpointMode.LdrLumaDirect);

        int calculateErrorOff = 0;
        int calculateErrorDir = 0;
        if (needsWeightSwap)
        {
            calculateErrorDir = RgbaColor.SquaredError(decLowDir, endpointHigh) + RgbaColor.SquaredError(decHighDir, endpointLow);
            calculateErrorOff = RgbaColor.SquaredError(decLowOff, endpointHigh) + RgbaColor.SquaredError(decHighOff, endpointLow);
        }
        else
        {
            calculateErrorDir = RgbaColor.SquaredError(decLowDir, endpointLow) + RgbaColor.SquaredError(decHighDir, endpointHigh);
            calculateErrorOff = RgbaColor.SquaredError(decLowOff, endpointLow) + RgbaColor.SquaredError(decHighOff, endpointHigh);
        }

        if (calculateErrorDir <= calculateErrorOff)
        {
            vals[0] = quantLow;
            vals[1] = quantHigh;
            astcMode = ColorEndpointMode.LdrLumaDirect;
        }
        else
        {
            vals[0] = quantOffLow;
            vals[1] = quantOffHigh;
            astcMode = ColorEndpointMode.LdrLumaBaseOffset;
        }

        return needsWeightSwap;
    }

    private static bool EncodeColorsRGBA(RgbaColor endpointLowRgba, RgbaColor endpointHighRgba, int maxValue, bool withAlpha, out ColorEndpointMode astcMode, List<int> vals)
    {
        astcMode = ColorEndpointMode.LdrRgbDirect;
        int numChannels = withAlpha ? 4 : 3;

        var invBcLow = endpointLowRgba.WithInvertedBlueContract();
        var invBcHigh = endpointHighRgba.WithInvertedBlueContract();

        var directBase = new int[4];
        var directOffset = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            directBase[i] = endpointLowRgba[i];
            directOffset[i] = Math.Clamp(endpointHighRgba[i] - endpointLowRgba[i], -32, 31);
            (directOffset[i], directBase[i]) = BitOperations.TransferPrecisionInverse(directOffset[i], directBase[i]);
        }

        var invBcBase = new int[4];
        var invBcOffset = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            invBcBase[i] = invBcHigh[i];
            invBcOffset[i] = Math.Clamp(invBcLow[i] - invBcHigh[i], -32, 31);
            (invBcOffset[i], invBcBase[i]) = BitOperations.TransferPrecisionInverse(invBcOffset[i], invBcBase[i]);
        }

        var directBaseSwapped = new int[4];
        var directOffsetSwapped = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            directBaseSwapped[i] = endpointHighRgba[i];
            directOffsetSwapped[i] = Math.Clamp(endpointLowRgba[i] - endpointHighRgba[i], -32, 31);
            (directOffsetSwapped[i], directBaseSwapped[i]) = BitOperations.TransferPrecisionInverse(directOffsetSwapped[i], directBaseSwapped[i]);
        }

        var invBcBaseSwapped = new int[4];
        var invBcOffsetSwapped = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            invBcBaseSwapped[i] = invBcLow[i];
            invBcOffsetSwapped[i] = Math.Clamp(invBcHigh[i] - invBcLow[i], -32, 31);
            (invBcOffsetSwapped[i], invBcBaseSwapped[i]) = BitOperations.TransferPrecisionInverse(invBcOffsetSwapped[i], invBcBaseSwapped[i]);
        }

        var directQuantized = new QuantizedEndpointPair(endpointLowRgba, endpointHighRgba, maxValue);
        var bcQuantized = new QuantizedEndpointPair(invBcLow, invBcHigh, maxValue);

        var offsetQuantized = new QuantizedEndpointPair(new RgbaColor(directBase[0], directBase[1], directBase[2], directBase[3]), new RgbaColor(directOffset[0], directOffset[1], directOffset[2], directOffset[3]), maxValue);
        var bcOffsetQuantized = new QuantizedEndpointPair(new RgbaColor(invBcBase[0], invBcBase[1], invBcBase[2], invBcBase[3]), new RgbaColor(invBcOffset[0], invBcOffset[1], invBcOffset[2], invBcOffset[3]), maxValue);

        var offsetSwappedQuantized = new QuantizedEndpointPair(new RgbaColor(directBaseSwapped[0], directBaseSwapped[1], directBaseSwapped[2], directBaseSwapped[3]), new RgbaColor(directOffsetSwapped[0], directOffsetSwapped[1], directOffsetSwapped[2], directOffsetSwapped[3]), maxValue);
        var bcOffsetSwappedQuantized = new QuantizedEndpointPair(new RgbaColor(invBcBaseSwapped[0], invBcBaseSwapped[1], invBcBaseSwapped[2], invBcBaseSwapped[3]), new RgbaColor(invBcOffsetSwapped[0], invBcOffsetSwapped[1], invBcOffsetSwapped[2], invBcOffsetSwapped[3]), maxValue);

        var errors = new List<CEEncodingOption>(6);

        // 3.1 regular unquantized error
        {
            var rgbaLow = directQuantized.UnquantizedLow();
            var rgbaHigh = directQuantized.UnquantizedHigh();
            var lowColor = new RgbaColor(rgbaLow[0], rgbaLow[1], rgbaLow[2], rgbaLow[3]);
            var highColor = new RgbaColor(rgbaHigh[0], rgbaHigh[1], rgbaHigh[2], rgbaHigh[3]);
            var sqRgbError = withAlpha
                ? RgbaColor.SquaredError(lowColor, endpointLowRgba) + RgbaColor.SquaredError(highColor, endpointHighRgba)
                : RgbColor.SquaredError(lowColor, endpointLowRgba) + RgbColor.SquaredError(highColor, endpointHighRgba);
            errors.Add(new CEEncodingOption(sqRgbError, directQuantized, false, false, false));
        }

        // 3.2 blue-contract
        {
            var blueContractUnquantizedLow = bcQuantized.UnquantizedLow();
            var blueContractUnquantizedHigh = bcQuantized.UnquantizedHigh();
            var blueContractLow = RgbaColorExtensions.WithBlueContract(blueContractUnquantizedLow[0], blueContractUnquantizedLow[1], blueContractUnquantizedLow[2], blueContractUnquantizedLow[3]);
            var blueContractHigh = RgbaColorExtensions.WithBlueContract(blueContractUnquantizedHigh[0], blueContractUnquantizedHigh[1], blueContractUnquantizedHigh[2], blueContractUnquantizedHigh[3]);
            // TODO: How to handle alpha for this entire functions??
            var blueContractSquaredError = withAlpha
                ? RgbaColor.SquaredError(blueContractLow, endpointLowRgba) + RgbaColor.SquaredError(blueContractHigh, endpointHighRgba)
                : RgbColor.SquaredError(blueContractLow, endpointLowRgba) + RgbColor.SquaredError(blueContractHigh, endpointHighRgba);

            errors.Add(new CEEncodingOption(blueContractSquaredError, bcQuantized, swapEndpoints: false, blueContract: true, useOffsetMode: false));
        }

        // 3.3 base/offset
        Action<QuantizedEndpointPair, bool> computeBaseOffsetError = (pair, swapped) =>
        {
            var baseArr = pair.UnquantizedLow();
            var offsetArr = pair.UnquantizedHigh();

            var baseColor = new RgbaColor(baseArr[0], baseArr[1], baseArr[2], baseArr[3]);
            var offsetColor = new RgbaColor(offsetArr[0], offsetArr[1], offsetArr[2], offsetArr[3]).AsOffsetFrom(baseColor);

            int baseOffsetError = 0;
            if (swapped)
            {
                baseOffsetError = withAlpha
                    ? RgbaColor.SquaredError(baseColor, endpointHighRgba) + RgbaColor.SquaredError(offsetColor, endpointLowRgba)
                    : RgbColor.SquaredError(baseColor, endpointHighRgba) + RgbColor.SquaredError(offsetColor, endpointLowRgba);
            }
            else
            {
                baseOffsetError = withAlpha
                    ? RgbaColor.SquaredError(baseColor, endpointLowRgba) + RgbaColor.SquaredError(offsetColor, endpointHighRgba)
                    : RgbColor.SquaredError(baseColor, endpointLowRgba) + RgbColor.SquaredError(offsetColor, endpointHighRgba);
            }

            errors.Add(new CEEncodingOption(baseOffsetError, pair, swapped, false, true));
        };

        computeBaseOffsetError(offsetQuantized, false);

        Action<QuantizedEndpointPair, bool> computeBaseOffsetBlueContractError = (pair, swapped) =>
        {
            var baseArr = pair.UnquantizedLow();
            var offsetArr = pair.UnquantizedHigh();

            var baseColor = new RgbaColor(baseArr[0], baseArr[1], baseArr[2], baseArr[3]);
            var offsetColor =  new RgbaColor(offsetArr[0], offsetArr[1], offsetArr[2], offsetArr[3]).AsOffsetFrom(baseColor);

            baseColor = baseColor.WithBlueContract();
            offsetColor = offsetColor.WithBlueContract();

            int sqBcError = 0;
            if (swapped)
            {
                sqBcError = withAlpha
                    ? RgbaColor.SquaredError(baseColor, endpointLowRgba) + RgbaColor.SquaredError(offsetColor, endpointHighRgba)
                    : RgbColor.SquaredError(baseColor, endpointLowRgba) + RgbColor.SquaredError(offsetColor, endpointHighRgba);
            }
            else
            {
                sqBcError = withAlpha
                    ? RgbaColor.SquaredError(baseColor, endpointHighRgba) + RgbaColor.SquaredError(offsetColor, endpointLowRgba)
                    : RgbColor.SquaredError(baseColor, endpointHighRgba) + RgbColor.SquaredError(offsetColor, endpointLowRgba);
            }

            errors.Add(new CEEncodingOption(sqBcError, pair, swapped, true, true));
        };

        computeBaseOffsetBlueContractError(bcOffsetQuantized, false);
        computeBaseOffsetError(offsetSwappedQuantized, true);
        computeBaseOffsetBlueContractError(bcOffsetSwappedQuantized, true);

        errors.Sort((a, b) => a.Error().CompareTo(b.Error()));

        foreach (var measurement in errors)
        {
            bool needsWeightSwap = false;
            ColorEndpointMode modeUnused;
            if (measurement.Pack(withAlpha, out modeUnused, vals, ref needsWeightSwap))
            {
                return needsWeightSwap;
            }
        }

        throw new InvalidOperationException("Shouldn't have reached this point");
    }

    /// <summary>
    /// Decodes color endpoints for the specified mode, returning a polymorphic endpoint pair
    /// that supports both LDR and HDR modes.
    /// </summary>
    /// <param name="vals">Quantized integer values from the ASTC block</param>
    /// <param name="maxValue">Maximum quantization value</param>
    /// <param name="mode">The color endpoint mode</param>
    /// <returns>A ColorEndpointPair representing either LDR or HDR endpoints</returns>
    public static ColorEndpointPair DecodeColorsForModePolymorphic(ReadOnlySpan<int> vals, int maxValue, ColorEndpointMode mode)
    {
        if (mode.IsHdr())
        {
            var (low, high) = HdrEndpointDecoder.DecodeHdrMode(vals, maxValue, mode);
            bool alphaIsLdr = mode == ColorEndpointMode.HdrRgbDirectLdrAlpha;
            return ColorEndpointPair.Hdr(low, high, alphaIsLdr);
        }
        else
        {
            var (low, high) = DecodeColorsForMode(vals, maxValue, mode);
            return ColorEndpointPair.Ldr(low, high);
        }
    }

    /// <summary>
    /// Decodes color endpoints from already-unquantized values.
    /// Called from the fused decode path where BISE decode + batch unquantize
    /// have already been performed. Returns an LDR ColorEndpointPair.
    /// </summary>
    internal static ColorEndpointPair DecodeColorsForModeUnquantized(ReadOnlySpan<int> uv, ColorEndpointMode mode)
    {
        RgbaColor endpointLowRgba, endpointHighRgba;

        switch (mode)
        {
            case ColorEndpointMode.LdrLumaDirect:
                endpointLowRgba = new RgbaColor(uv[0], uv[0], uv[0]);
                endpointHighRgba = new RgbaColor(uv[1], uv[1], uv[1]);
                break;
            case ColorEndpointMode.LdrLumaBaseOffset:
            {
                int l0 = (uv[0] >> 2) | (uv[1] & 0xC0);
                int l1 = Math.Min(l0 + (uv[1] & 0x3F), 0xFF);
                endpointLowRgba = new RgbaColor(l0, l0, l0);
                endpointHighRgba = new RgbaColor(l1, l1, l1);
                break;
            }
            case ColorEndpointMode.LdrLumaAlphaDirect:
                endpointLowRgba = new RgbaColor(uv[0], uv[0], uv[0], uv[2]);
                endpointHighRgba = new RgbaColor(uv[1], uv[1], uv[1], uv[3]);
                break;
            case ColorEndpointMode.LdrLumaAlphaBaseOffset:
            {
                var (b0, a0) = BitOperations.TransferPrecision(uv[1], uv[0]);
                var (b2, a2) = BitOperations.TransferPrecision(uv[3], uv[2]);
                endpointLowRgba = new RgbaColor(a0, a0, a0, a2);
                int high_luma = a0 + b0;
                endpointHighRgba = new RgbaColor(high_luma, high_luma, high_luma, a2 + b2);
                break;
            }
            case ColorEndpointMode.LdrRgbBaseScale:
                endpointLowRgba = new RgbaColor(
                    (uv[0] * uv[3]) >> 8,
                    (uv[1] * uv[3]) >> 8,
                    (uv[2] * uv[3]) >> 8);
                endpointHighRgba = new RgbaColor(uv[0], uv[1], uv[2]);
                break;
            case ColorEndpointMode.LdrRgbDirect:
            {
                int s0 = uv[0] + uv[2] + uv[4];
                int s1 = uv[1] + uv[3] + uv[5];
                if (s1 < s0)
                {
                    endpointLowRgba = new RgbaColor(
                        r: (uv[1] + uv[5]) >> 1,
                        g: (uv[3] + uv[5]) >> 1,
                        b: uv[5]);
                    endpointHighRgba = new RgbaColor(
                        r: (uv[0] + uv[4]) >> 1,
                        g: (uv[2] + uv[4]) >> 1,
                        b: uv[4]);
                }
                else
                {
                    endpointLowRgba = new RgbaColor(uv[0], uv[2], uv[4]);
                    endpointHighRgba = new RgbaColor(uv[1], uv[3], uv[5]);
                }
                break;
            }
            case ColorEndpointMode.LdrRgbBaseOffset:
            {
                var (b0, a0) = BitOperations.TransferPrecision(uv[1], uv[0]);
                var (b1, a1) = BitOperations.TransferPrecision(uv[3], uv[2]);
                var (b2, a2) = BitOperations.TransferPrecision(uv[5], uv[4]);
                if (b0 + b1 + b2 < 0)
                {
                    endpointLowRgba = new RgbaColor(
                        r: (a0 + b0 + a2 + b2) >> 1,
                        g: (a1 + b1 + a2 + b2) >> 1,
                        b: a2 + b2);
                    endpointHighRgba = new RgbaColor(
                        r: (a0 + a2) >> 1,
                        g: (a1 + a2) >> 1,
                        b: a2);
                }
                else
                {
                    endpointLowRgba = new RgbaColor(a0, a1, a2);
                    endpointHighRgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2);
                }
                break;
            }
            case ColorEndpointMode.LdrRgbBaseScaleTwoA:
                endpointLowRgba = new RgbaColor(
                    r: (uv[0] * uv[3]) >> 8,
                    g: (uv[1] * uv[3]) >> 8,
                    b: (uv[2] * uv[3]) >> 8,
                    a: uv[4]);
                endpointHighRgba = new RgbaColor(uv[0], uv[1], uv[2], uv[5]);
                break;
            case ColorEndpointMode.LdrRgbaDirect:
            {
                int s0 = uv[0] + uv[2] + uv[4];
                int s1 = uv[1] + uv[3] + uv[5];
                if (s1 >= s0)
                {
                    endpointLowRgba = new RgbaColor(uv[0], uv[2], uv[4], uv[6]);
                    endpointHighRgba = new RgbaColor(uv[1], uv[3], uv[5], uv[7]);
                }
                else
                {
                    endpointLowRgba = new RgbaColor(
                        r: (uv[1] + uv[5]) >> 1,
                        g: (uv[3] + uv[5]) >> 1,
                        b: uv[5],
                        a: uv[7]);
                    endpointHighRgba = new RgbaColor(
                        r: (uv[0] + uv[4]) >> 1,
                        g: (uv[2] + uv[4]) >> 1,
                        b: uv[4],
                        a: uv[6]);
                }
                break;
            }
            case ColorEndpointMode.LdrRgbaBaseOffset:
            {
                var (b0, a0) = BitOperations.TransferPrecision(uv[1], uv[0]);
                var (b1, a1) = BitOperations.TransferPrecision(uv[3], uv[2]);
                var (b2, a2) = BitOperations.TransferPrecision(uv[5], uv[4]);
                var (b3, a3) = BitOperations.TransferPrecision(uv[7], uv[6]);
                if (b0 + b1 + b2 < 0)
                {
                    endpointLowRgba = new RgbaColor(
                        r: (a0 + b0 + a2 + b2) >> 1,
                        g: (a1 + b1 + a2 + b2) >> 1,
                        b: a2 + b2,
                        a: a3 + b3);
                    endpointHighRgba = new RgbaColor(
                        r: (a0 + a2) >> 1,
                        g: (a1 + a2) >> 1,
                        b: a2,
                        a: a3);
                }
                else
                {
                    endpointLowRgba = new RgbaColor(a0, a1, a2, a3);
                    endpointHighRgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2, a3 + b3);
                }
                break;
            }
            default:
                endpointLowRgba = RgbaColor.Empty;
                endpointHighRgba = RgbaColor.Empty;
                break;
        }

        return ColorEndpointPair.Ldr(endpointLowRgba, endpointHighRgba);
    }

    public static (RgbaColor endpointLowRgba, RgbaColor endpointHighRgba) DecodeColorsForMode(ReadOnlySpan<int> vals, int maxValue, ColorEndpointMode mode)
    {
        var endpointLowRgba = RgbaColor.Empty;
        var endpointHighRgba = RgbaColor.Empty;

        switch (mode)
        {
            case ColorEndpointMode.LdrLumaDirect:
                {
                    int l0 = Quantization.UnquantizeCEValueFromRange(vals[0], maxValue);
                    int l1 = Quantization.UnquantizeCEValueFromRange(vals[1], maxValue);
                    endpointLowRgba = new RgbaColor(l0, l0, l0);
                    endpointHighRgba = new RgbaColor(l1, l1, l1);
                }
                break;
            case ColorEndpointMode.LdrLumaBaseOffset:
                {
                    int v0 = Quantization.UnquantizeCEValueFromRange(vals[0], maxValue);
                    int v1 = Quantization.UnquantizeCEValueFromRange(vals[1], maxValue);
                    int l0 = (v0 >> 2) | (v1 & 0xC0);
                    int l1 = Math.Min(l0 + (v1 & 0x3F), 0xFF);
                    endpointLowRgba = new RgbaColor(l0, l0, l0);
                    endpointHighRgba = new RgbaColor(l1, l1, l1);
                }
                break;
            case ColorEndpointMode.LdrLumaAlphaDirect:
                {
                    Span<int> uv = stackalloc int[4];
                    for (int i = 0; i < 4; ++i) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);
                    endpointLowRgba = new RgbaColor(uv[0], uv[0], uv[0], uv[2]);
                    endpointHighRgba = new RgbaColor(uv[1], uv[1], uv[1], uv[3]);
                }
                break;
            case ColorEndpointMode.LdrLumaAlphaBaseOffset:
                {
                    Span<int> uv = stackalloc int[4];
                    for (int i = 0; i < 4; i++) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);
                    var (b0, a0) = BitOperations.TransferPrecision(uv[1], uv[0]);
                    var (b2, a2) = BitOperations.TransferPrecision(uv[3], uv[2]);
                    endpointLowRgba = new RgbaColor(a0, a0, a0, a2);
                    int high_luma = a0 + b0;
                    endpointHighRgba = new RgbaColor(high_luma, high_luma, high_luma, a2 + b2);
                }
                break;
            case ColorEndpointMode.LdrRgbBaseScale:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbBaseScale.GetColorValuesCount();
                    Span<int> uv = stackalloc int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);

                    endpointLowRgba = new RgbaColor(
                        (uv[0] * uv[3]) >> 8,
                        (uv[1] * uv[3]) >> 8,
                        (uv[2] * uv[3]) >> 8);
                    endpointHighRgba = new RgbaColor(
                        uv[0],
                        uv[1],
                        uv[2]);
                }
                break;
            case ColorEndpointMode.LdrRgbDirect:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbDirect.GetColorValuesCount();
                    Span<int> uv = stackalloc int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);
                    int s0 = uv[0] + uv[2] + uv[4];
                    int s1 = uv[1] + uv[3] + uv[5];

                    if (s1 < s0)
                    {
                        endpointLowRgba = new RgbaColor(
                            r: (uv[1] + uv[5]) >> 1,
                            g: (uv[3] + uv[5]) >> 1,
                            b: uv[5]);
                        endpointHighRgba = new RgbaColor(
                            r: (uv[0] + uv[4]) >> 1,
                            g: (uv[2] + uv[4]) >> 1,
                            b: uv[4]);
                    }
                    else
                    {
                        endpointLowRgba = new RgbaColor(uv[0], uv[2], uv[4]);
                        endpointHighRgba = new RgbaColor(uv[1], uv[3], uv[5]);
                    }
                }
                break;
            case ColorEndpointMode.LdrRgbBaseOffset:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbBaseOffset.GetColorValuesCount();
                    Span<int> uv = stackalloc int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);
                    var (b0, a0) = BitOperations.TransferPrecision(uv[1], uv[0]);
                    var (b1, a1) = BitOperations.TransferPrecision(uv[3], uv[2]);
                    var (b2, a2) = BitOperations.TransferPrecision(uv[5], uv[4]);

                    if (b0 + b1 + b2 < 0)
                    {
                        endpointLowRgba = new RgbaColor(
                            r: (a0 + b0 + a2 + b2) >> 1,
                            g: (a1 + b1 + a2 + b2) >> 1,
                            b: a2 + b2);
                        endpointHighRgba = new RgbaColor(
                            r: (a0 + a2) >> 1,
                            g: (a1 + a2) >> 1,
                            b: a2);
                    }
                    else
                    {
                        endpointLowRgba = new RgbaColor(a0, a1, a2);
                        endpointHighRgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2);
                    }
                }
                break;
            case ColorEndpointMode.LdrRgbBaseScaleTwoA:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbBaseScaleTwoA.GetColorValuesCount();
                    Span<int> uv = stackalloc int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);
                    endpointLowRgba = new RgbaColor(
                        r: (uv[0] * uv[3]) >> 8,
                        g: (uv[1] * uv[3]) >> 8,
                        b: (uv[2] * uv[3]) >> 8,
                        a: uv[4]);
                    endpointHighRgba = new RgbaColor(uv[0], uv[1], uv[2], uv[5]);
                }
                break;
            case ColorEndpointMode.LdrRgbaDirect:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbaDirect.GetColorValuesCount();
                    Span<int> uv = stackalloc int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);
                    int s0 = uv[0] + uv[2] + uv[4];
                    int s1 = uv[1] + uv[3] + uv[5];

                    if (s1 >= s0)
                    {
                        endpointLowRgba = new RgbaColor(uv[0], uv[2], uv[4], uv[6]);
                        endpointHighRgba = new RgbaColor(uv[1], uv[3], uv[5], uv[7]);
                    }
                    else
                    {
                        endpointLowRgba = new RgbaColor(
                            r: (uv[1] + uv[5]) >> 1,
                            g: (uv[3] + uv[5]) >> 1,
                            b: uv[5],
                            a: uv[7]);
                        endpointHighRgba = new RgbaColor(
                            r: (uv[0] + uv[4]) >> 1,
                            g: (uv[2] + uv[4]) >> 1,
                            b: uv[4],
                            a: uv[6]);
                    }
                }
                break;
            case ColorEndpointMode.LdrRgbaBaseOffset:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbaBaseOffset.GetColorValuesCount();
                    Span<int> uv = stackalloc int[kNumVals];
                    for (int i = 0; i < kNumVals; ++i) uv[i] = i < vals.Length ? vals[i] : 0;
                    UnquantizeInline(uv, maxValue);
                    var (b0, a0) = BitOperations.TransferPrecision(uv[1], uv[0]);
                    var (b1, a1) = BitOperations.TransferPrecision(uv[3], uv[2]);
                    var (b2, a2) = BitOperations.TransferPrecision(uv[5], uv[4]);
                    var (b3, a3) = BitOperations.TransferPrecision(uv[7], uv[6]);

                    if (b0 + b1 + b2 < 0)
                    {
                        endpointLowRgba = new RgbaColor(
                            r: (a0 + b0 + a2 + b2) >> 1,
                            g: (a1 + b1 + a2 + b2) >> 1,
                            b: a2 + b2,
                            a: a3 + b3);
                        endpointHighRgba = new RgbaColor(
                            r: (a0 + a2) >> 1,
                            g: (a1 + a2) >> 1,
                            b: a2,
                            a: a3);
                    }
                    else
                    {
                        endpointLowRgba = new RgbaColor(a0, a1, a2, a3);
                        endpointHighRgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2, a3 + b3);
                    }
                }
                break;
            default:
                break;
        }

        return (endpointLowRgba, endpointHighRgba);
    }
}
