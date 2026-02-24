using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.Core;

namespace AstcSharp.ColorEncoding;

internal static class EndpointCodec
{
    // TODO: Move these two to RGBA color extensions?
    private static int[] QuantizeColorArray(RgbaColor c, int maxValue)
    {
        var array = new int[RgbaColor.BytesPerPixel];
        for (int i = 0; i < RgbaColor.BytesPerPixel; ++i) array[i] = Quantization.QuantizeCEValueToRange(c[i], maxValue);
        return array;
    }

    private static int[] UnquantizeArray(int[] values, int maxValue)
    {
        var result = new int[values.Length];
        for (int i = 0; i < values.Length; ++i) result[i] = Quantization.UnquantizeCEValueFromRange(values[i], maxValue);
        return result;
    }

    private static void UnquantizeInline(Span<int> values, int maxValue)
    {
        for (int i = 0; i < values.Length; ++i) values[i] = Quantization.UnquantizeCEValueFromRange(values[i], maxValue);
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
            var unquantizedLowOriginal = _quantizedEndpoints.UnquantizedLow();
            var unquantizedHighOriginal = _quantizedEndpoints.UnquantizedHigh();

            var unquantizedLow = (int[])unquantizedLowOriginal.Clone();
            var unquantizedHigh = (int[])unquantizedHighOriginal.Clone();

            if (_useOffsetMode)
            {
                for (int i = 0; i < 4; ++i)
                {
                    (unquantizedHigh[i], unquantizedLow[i]) = BitOperations.TransferPrecision(unquantizedHigh[i], unquantizedLow[i]);
                }
            }

            int sum0 = 0, sum1 = 0;
            for (int i = 0; i < 3; ++i)
            {
                sum0 += unquantizedLow[i];
                sum1 += unquantizedHigh[i];
            }

            bool swapVals = false;
            if (_useOffsetMode)
            {
                if (_blueContract)
                {
                    swapVals = sum1 >= 0;
                }
                else
                {
                    swapVals = sum1 < 0;
                }

                if (swapVals) return false;
            }
            else
            {
                if (_blueContract)
                {
                    if (sum1 == sum0) return false;
                    swapVals = sum1 > sum0;
                    needsWeightSwap = !needsWeightSwap;
                }
                else
                {
                    swapVals = sum1 < sum0;
                }
            }

            var quantizedLowOriginal = _quantizedEndpoints.QuantizedLow();
            var quantizedHighOriginal = _quantizedEndpoints.QuantizedHigh();

            var quantizedLow = (int[])quantizedLowOriginal.Clone();
            var quantizedHigh = (int[])quantizedHighOriginal.Clone();

            if (swapVals)
            {
                if (_useOffsetMode) throw new InvalidOperationException();
                var tmp = quantizedLow; quantizedLow = quantizedHigh; quantizedHigh = tmp;
                needsWeightSwap = !needsWeightSwap;
            }

            values[0] = quantizedLow[0];
            values[1] = quantizedHigh[0];
            values[2] = quantizedLow[1];
            values[3] = quantizedHigh[1];
            values[4] = quantizedLow[2];
            values[5] = quantizedHigh[2];

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
                values[6] = quantizedLow[3];
                values[7] = quantizedHigh[3];
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
        int valueCount = mode.GetColorValuesCount();
        ArgumentOutOfRangeException.ThrowIfLessThan(values.Count, valueCount);

        switch (mode)
        {
            case ColorEndpointMode.LdrRgbDirect:
            case ColorEndpointMode.LdrRgbaDirect:
                {
                    int maxValueCount = Math.Max(ColorEndpointMode.LdrRgbDirect.GetColorValuesCount(), ColorEndpointMode.LdrRgbaDirect.GetColorValuesCount());
                    var v = new int[maxValueCount];
                    for (int i = 0; i < maxValueCount; ++i) v[i] = i < values.Count ? values[i] : 0;
                    var unquantizedValues = UnquantizeArray(v, maxValue);
                    int s0 = unquantizedValues[0] + unquantizedValues[2] + unquantizedValues[4];
                    int s1 = unquantizedValues[1] + unquantizedValues[3] + unquantizedValues[5];
                    return s0 > s1;
                }
            case ColorEndpointMode.LdrRgbBaseOffset:
            case ColorEndpointMode.LdrRgbaBaseOffset:
                {
                    int maxValueCount = Math.Max(ColorEndpointMode.LdrRgbBaseOffset.GetColorValuesCount(), ColorEndpointMode.LdrRgbaBaseOffset.GetColorValuesCount());
                    var v = new int[maxValueCount];
                    for (int i = 0; i < maxValueCount; ++i) v[i] = i < values.Count ? values[i] : 0;
                    var unquantizedValues = UnquantizeArray(v, maxValue);
                    var (b0, a0) = BitOperations.TransferPrecision(unquantizedValues[1], unquantizedValues[0]);
                    var (b1, a1) = BitOperations.TransferPrecision(unquantizedValues[3], unquantizedValues[2]);
                    var (b2, a2) = BitOperations.TransferPrecision(unquantizedValues[5], unquantizedValues[4]);
                    return (b0 + b1 + b2) < 0;
                }
            default:
                return false;
        }
    }

    // TODO: Extract an interface and implement instances for each encoding mode
    public static bool EncodeColorsForMode(RgbaColor endpointLowRgba, RgbaColor endpointHighRgba, int maxValue, EndpointEncodingMode encodingMode, out ColorEndpointMode astcMode, List<int> values)
    {
        bool needsWeightSwap = false;
        astcMode = ColorEndpointMode.LdrLumaDirect;
        int valueCount = encodingMode.GetValuesCount();
        for (int i = values.Count; i < valueCount; ++i) values.Add(0);

        switch (encodingMode)
        {
            case EndpointEncodingMode.DirectLuma:
                return EncodeColorsLuma(endpointLowRgba, endpointHighRgba, maxValue, out astcMode, values);
            case EndpointEncodingMode.DirectLumaAlpha:
                {
                    int avg1 = endpointLowRgba.Average;
                    int avg2 = endpointHighRgba.Average;
                    values[0] = Quantization.QuantizeCEValueToRange(avg1, maxValue);
                    values[1] = Quantization.QuantizeCEValueToRange(avg2, maxValue);
                    values[2] = Quantization.QuantizeCEValueToRange(endpointLowRgba[3], maxValue);
                    values[3] = Quantization.QuantizeCEValueToRange(endpointHighRgba[3], maxValue);
                    astcMode = ColorEndpointMode.LdrLumaAlphaDirect;
                }
                break;
            case EndpointEncodingMode.BaseScaleRgb:
            case EndpointEncodingMode.BaseScaleRgba:
                {
                    var baseColor = endpointHighRgba;
                    var scaled = endpointLowRgba;

                    int numChannelsGe = 0;
                    for (int i = 0; i < 3; ++i) numChannelsGe += endpointHighRgba[i] >= endpointLowRgba[i] ? 1 : 0;

                    if (numChannelsGe < 2)
                    {
                        needsWeightSwap = true;
                        var temp = baseColor; baseColor = scaled; scaled = temp;
                    }

                    var quantizedBase = QuantizeColorArray(baseColor, maxValue);
                    var unquantizedBase = UnquantizeArray(quantizedBase, maxValue);

                    int numSamples = 0;
                    int scaleSum = 0;
                    for (int i = 0; i < 3; ++i)
                    {
                        int x = unquantizedBase[i];
                        if (x != 0)
                        {
                            ++numSamples;
                            scaleSum += (scaled[i] * 256) / x;
                        }
                    }

                    values[0] = quantizedBase[0];
                    values[1] = quantizedBase[1];
                    values[2] = quantizedBase[2];
                    if (numSamples > 0)
                    {
                        int avgScale = Math.Clamp(scaleSum / numSamples, 0, 255);
                        values[3] = Quantization.QuantizeCEValueToRange(avgScale, maxValue);
                    }
                    else
                    {
                        values[3] = maxValue;
                    }
                    astcMode = ColorEndpointMode.LdrRgbBaseScale;

                    if (encodingMode == EndpointEncodingMode.BaseScaleRgba)
                    {
                        values[4] = Quantization.QuantizeCEValueToRange(scaled[3], maxValue);
                        values[5] = Quantization.QuantizeCEValueToRange(baseColor[3], maxValue);
                        astcMode = ColorEndpointMode.LdrRgbBaseScaleTwoA;
                    }
                }
                break;
            case EndpointEncodingMode.DirectRbg:
            case EndpointEncodingMode.DirectRgba:
                return EncodeColorsRGBA(endpointLowRgba, endpointHighRgba, maxValue, encodingMode == EndpointEncodingMode.DirectRgba, out astcMode, values);
            default:
                throw new InvalidOperationException("Unimplemented color encoding.");
        }

        return needsWeightSwap;
    }

    private static bool EncodeColorsLuma(RgbaColor endpointLow, RgbaColor endpointHigh, int maxValue, out ColorEndpointMode astcMode, List<int> values)
    {
        astcMode = ColorEndpointMode.LdrLumaDirect;
        ArgumentOutOfRangeException.ThrowIfLessThan(values.Count, 2);

        int avg1 = endpointLow.Average;
        int avg2 = endpointHigh.Average;

        bool needsWeightSwap = false;
        if (avg1 > avg2) { needsWeightSwap = true; var temp = avg1; avg1 = avg2; avg2 = temp; }

        int offset = Math.Min(avg2 - avg1, 0x3F);
        int quantOffLow = Quantization.QuantizeCEValueToRange((avg1 & 0x3F) << 2, maxValue);
        int quantOffHigh = Quantization.QuantizeCEValueToRange((avg1 & 0xC0) | offset, maxValue);

        int quantLow = Quantization.QuantizeCEValueToRange(avg1, maxValue);
        int quantHigh = Quantization.QuantizeCEValueToRange(avg2, maxValue);

        values[0] = quantOffLow;
        values[1] = quantOffHigh;
        var (decLowOff, decHighOff) = DecodeColorsForMode(values.ToArray(), maxValue, ColorEndpointMode.LdrLumaBaseOffset);

        values[0] = quantLow;
        values[1] = quantHigh;
        var (decLowDir, decHighDir) = DecodeColorsForMode(values.ToArray(), maxValue, ColorEndpointMode.LdrLumaDirect);

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
            values[0] = quantLow;
            values[1] = quantHigh;
            astcMode = ColorEndpointMode.LdrLumaDirect;
        }
        else
        {
            values[0] = quantOffLow;
            values[1] = quantOffHigh;
            astcMode = ColorEndpointMode.LdrLumaBaseOffset;
        }

        return needsWeightSwap;
    }

    private static bool EncodeColorsRGBA(RgbaColor endpointLowRgba, RgbaColor endpointHighRgba, int maxValue, bool withAlpha, out ColorEndpointMode astcMode, List<int> values)
    {
        astcMode = ColorEndpointMode.LdrRgbDirect;
        int numChannels = withAlpha ? 4 : 3;

        var invertedBlueContractLow = endpointLowRgba.WithInvertedBlueContract();
        var invertedBlueContractHigh = endpointHighRgba.WithInvertedBlueContract();

        var directBase = new int[4];
        var directOffset = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            directBase[i] = endpointLowRgba[i];
            directOffset[i] = Math.Clamp(endpointHighRgba[i] - endpointLowRgba[i], -32, 31);
            (directOffset[i], directBase[i]) = BitOperations.TransferPrecisionInverse(directOffset[i], directBase[i]);
        }

        var invertedBlueContractBase = new int[4];
        var invertedBlueContractOffset = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            invertedBlueContractBase[i] = invertedBlueContractHigh[i];
            invertedBlueContractOffset[i] = Math.Clamp(invertedBlueContractLow[i] - invertedBlueContractHigh[i], -32, 31);
            (invertedBlueContractOffset[i], invertedBlueContractBase[i]) = BitOperations.TransferPrecisionInverse(invertedBlueContractOffset[i], invertedBlueContractBase[i]);
        }

        var directBaseSwapped = new int[4];
        var directOffsetSwapped = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            directBaseSwapped[i] = endpointHighRgba[i];
            directOffsetSwapped[i] = Math.Clamp(endpointLowRgba[i] - endpointHighRgba[i], -32, 31);
            (directOffsetSwapped[i], directBaseSwapped[i]) = BitOperations.TransferPrecisionInverse(directOffsetSwapped[i], directBaseSwapped[i]);
        }

        var invertedBlueContractBaseSwapped = new int[4];
        var invertedBlueContractOffsetSwapped = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            invertedBlueContractBaseSwapped[i] = invertedBlueContractLow[i];
            invertedBlueContractOffsetSwapped[i] = Math.Clamp(invertedBlueContractHigh[i] - invertedBlueContractLow[i], -32, 31);
            (invertedBlueContractOffsetSwapped[i], invertedBlueContractBaseSwapped[i]) = BitOperations.TransferPrecisionInverse(invertedBlueContractOffsetSwapped[i], invertedBlueContractBaseSwapped[i]);
        }

        var directQuantized = new QuantizedEndpointPair(endpointLowRgba, endpointHighRgba, maxValue);
        var bcQuantized = new QuantizedEndpointPair(invertedBlueContractLow, invertedBlueContractHigh, maxValue);

        var offsetQuantized = new QuantizedEndpointPair(new RgbaColor(directBase[0], directBase[1], directBase[2], directBase[3]), new RgbaColor(directOffset[0], directOffset[1], directOffset[2], directOffset[3]), maxValue);
        var bcOffsetQuantized = new QuantizedEndpointPair(new RgbaColor(invertedBlueContractBase[0], invertedBlueContractBase[1], invertedBlueContractBase[2], invertedBlueContractBase[3]), new RgbaColor(invertedBlueContractOffset[0], invertedBlueContractOffset[1], invertedBlueContractOffset[2], invertedBlueContractOffset[3]), maxValue);

        var offsetSwappedQuantized = new QuantizedEndpointPair(new RgbaColor(directBaseSwapped[0], directBaseSwapped[1], directBaseSwapped[2], directBaseSwapped[3]), new RgbaColor(directOffsetSwapped[0], directOffsetSwapped[1], directOffsetSwapped[2], directOffsetSwapped[3]), maxValue);
        var bcOffsetSwappedQuantized = new QuantizedEndpointPair(new RgbaColor(invertedBlueContractBaseSwapped[0], invertedBlueContractBaseSwapped[1], invertedBlueContractBaseSwapped[2], invertedBlueContractBaseSwapped[3]), new RgbaColor(invertedBlueContractOffsetSwapped[0], invertedBlueContractOffsetSwapped[1], invertedBlueContractOffsetSwapped[2], invertedBlueContractOffsetSwapped[3]), maxValue);

        var errors = new List<CEEncodingOption>(6);

        // 3.1 regular unquantized error
        {
            var rgbaLow = directQuantized.UnquantizedLow();
            var rgbaHigh = directQuantized.UnquantizedHigh();
            var lowColor = new RgbaColor(rgbaLow[0], rgbaLow[1], rgbaLow[2], rgbaLow[3]);
            var highColor = new RgbaColor(rgbaHigh[0], rgbaHigh[1], rgbaHigh[2], rgbaHigh[3]);
            var squaredRgbError = withAlpha
                ? RgbaColor.SquaredError(lowColor, endpointLowRgba) + RgbaColor.SquaredError(highColor, endpointHighRgba)
                : RgbColor.SquaredError(lowColor, endpointLowRgba) + RgbColor.SquaredError(highColor, endpointHighRgba);
            errors.Add(new CEEncodingOption(squaredRgbError, directQuantized, false, false, false));
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
            var offsetColor = new RgbaColor(offsetArr[0], offsetArr[1], offsetArr[2], offsetArr[3]).AsOffsetFrom(baseColor);

            baseColor = baseColor.WithBlueContract();
            offsetColor = offsetColor.WithBlueContract();

            int squaredBlueContractError = 0;
            if (swapped)
            {
                squaredBlueContractError = withAlpha
                    ? RgbaColor.SquaredError(baseColor, endpointLowRgba) + RgbaColor.SquaredError(offsetColor, endpointHighRgba)
                    : RgbColor.SquaredError(baseColor, endpointLowRgba) + RgbColor.SquaredError(offsetColor, endpointHighRgba);
            }
            else
            {
                squaredBlueContractError = withAlpha
                    ? RgbaColor.SquaredError(baseColor, endpointHighRgba) + RgbaColor.SquaredError(offsetColor, endpointLowRgba)
                    : RgbColor.SquaredError(baseColor, endpointHighRgba) + RgbColor.SquaredError(offsetColor, endpointLowRgba);
            }

            errors.Add(new CEEncodingOption(squaredBlueContractError, pair, swapped, true, true));
        };

        computeBaseOffsetBlueContractError(bcOffsetQuantized, false);
        computeBaseOffsetError(offsetSwappedQuantized, true);
        computeBaseOffsetBlueContractError(bcOffsetSwappedQuantized, true);

        errors.Sort((a, b) => a.Error().CompareTo(b.Error()));

        foreach (var measurement in errors)
        {
            bool needsWeightSwap = false;
            ColorEndpointMode modeUnused;
            if (measurement.Pack(withAlpha, out modeUnused, values, ref needsWeightSwap))
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
    /// <param name="values">Quantized integer values from the ASTC block</param>
    /// <param name="maxValue">Maximum quantization value</param>
    /// <param name="mode">The color endpoint mode</param>
    /// <returns>A ColorEndpointPair representing either LDR or HDR endpoints</returns>
    public static ColorEndpointPair DecodeColorsForModePolymorphic(ReadOnlySpan<int> values, int maxValue, ColorEndpointMode mode)
    {
        if (mode.IsHdr())
        {
            var (low, high) = HdrEndpointDecoder.DecodeHdrMode(values, maxValue, mode);
            bool alphaIsLdr = mode == ColorEndpointMode.HdrRgbDirectLdrAlpha;
            return ColorEndpointPair.Hdr(low, high, alphaIsLdr);
        }
        else
        {
            var (low, high) = DecodeColorsForMode(values, maxValue, mode);
            return ColorEndpointPair.Ldr(low, high);
        }
    }

    /// <summary>
    /// Decodes color endpoints from already-unquantized values, supporting both LDR and HDR modes.
    /// Called from the fused HDR decode path where BISE decode + batch unquantize
    /// have already been performed. Returns a ColorEndpointPair (LDR or HDR).
    /// </summary>
    internal static ColorEndpointPair DecodeColorsForModePolymorphicUnquantized(ReadOnlySpan<int> unquantizedValues, ColorEndpointMode mode)
    {
        if (mode.IsHdr())
        {
            var (low, high) = HdrEndpointDecoder.DecodeHdrModeUnquantized(unquantizedValues, mode);
            bool alphaIsLdr = mode == ColorEndpointMode.HdrRgbDirectLdrAlpha;
            return ColorEndpointPair.Hdr(low, high, alphaIsLdr);
        }

        return DecodeColorsForModeUnquantized(unquantizedValues, mode);
    }

    /// <summary>
    /// Decodes color endpoints from already-unquantized values.
    /// Called from the fused decode path where BISE decode + batch unquantize
    /// have already been performed. Returns an LDR ColorEndpointPair.
    /// </summary>
    internal static ColorEndpointPair DecodeColorsForModeUnquantized(ReadOnlySpan<int> unquantizedValues, ColorEndpointMode mode)
    {
        RgbaColor endpointLowRgba, endpointHighRgba;

        switch (mode)
        {
            case ColorEndpointMode.LdrLumaDirect:
                endpointLowRgba = new RgbaColor(unquantizedValues[0], unquantizedValues[0], unquantizedValues[0]);
                endpointHighRgba = new RgbaColor(unquantizedValues[1], unquantizedValues[1], unquantizedValues[1]);
                break;
            case ColorEndpointMode.LdrLumaBaseOffset:
                {
                    int l0 = (unquantizedValues[0] >> 2) | (unquantizedValues[1] & 0xC0);
                    int l1 = Math.Min(l0 + (unquantizedValues[1] & 0x3F), 0xFF);
                    endpointLowRgba = new RgbaColor(l0, l0, l0);
                    endpointHighRgba = new RgbaColor(l1, l1, l1);
                    break;
                }
            case ColorEndpointMode.LdrLumaAlphaDirect:
                endpointLowRgba = new RgbaColor(unquantizedValues[0], unquantizedValues[0], unquantizedValues[0], unquantizedValues[2]);
                endpointHighRgba = new RgbaColor(unquantizedValues[1], unquantizedValues[1], unquantizedValues[1], unquantizedValues[3]);
                break;
            case ColorEndpointMode.LdrLumaAlphaBaseOffset:
                {
                    var (b0, a0) = BitOperations.TransferPrecision(unquantizedValues[1], unquantizedValues[0]);
                    var (b2, a2) = BitOperations.TransferPrecision(unquantizedValues[3], unquantizedValues[2]);
                    endpointLowRgba = new RgbaColor(a0, a0, a0, a2);
                    int highLuma = a0 + b0;
                    endpointHighRgba = new RgbaColor(highLuma, highLuma, highLuma, a2 + b2);
                    break;
                }
            case ColorEndpointMode.LdrRgbBaseScale:
                endpointLowRgba = new RgbaColor(
                    (unquantizedValues[0] * unquantizedValues[3]) >> 8,
                    (unquantizedValues[1] * unquantizedValues[3]) >> 8,
                    (unquantizedValues[2] * unquantizedValues[3]) >> 8);
                endpointHighRgba = new RgbaColor(unquantizedValues[0], unquantizedValues[1], unquantizedValues[2]);
                break;
            case ColorEndpointMode.LdrRgbDirect:
                {
                    int sum0 = unquantizedValues[0] + unquantizedValues[2] + unquantizedValues[4];
                    int sum1 = unquantizedValues[1] + unquantizedValues[3] + unquantizedValues[5];
                    if (sum1 < sum0)
                    {
                        endpointLowRgba = new RgbaColor(
                            r: (unquantizedValues[1] + unquantizedValues[5]) >> 1,
                            g: (unquantizedValues[3] + unquantizedValues[5]) >> 1,
                            b: unquantizedValues[5]);
                        endpointHighRgba = new RgbaColor(
                            r: (unquantizedValues[0] + unquantizedValues[4]) >> 1,
                            g: (unquantizedValues[2] + unquantizedValues[4]) >> 1,
                            b: unquantizedValues[4]);
                    }
                    else
                    {
                        endpointLowRgba = new RgbaColor(unquantizedValues[0], unquantizedValues[2], unquantizedValues[4]);
                        endpointHighRgba = new RgbaColor(unquantizedValues[1], unquantizedValues[3], unquantizedValues[5]);
                    }
                    break;
                }
            case ColorEndpointMode.LdrRgbBaseOffset:
                {
                    var (b0, a0) = BitOperations.TransferPrecision(unquantizedValues[1], unquantizedValues[0]);
                    var (b1, a1) = BitOperations.TransferPrecision(unquantizedValues[3], unquantizedValues[2]);
                    var (b2, a2) = BitOperations.TransferPrecision(unquantizedValues[5], unquantizedValues[4]);
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
                    r: (unquantizedValues[0] * unquantizedValues[3]) >> 8,
                    g: (unquantizedValues[1] * unquantizedValues[3]) >> 8,
                    b: (unquantizedValues[2] * unquantizedValues[3]) >> 8,
                    a: unquantizedValues[4]);
                endpointHighRgba = new RgbaColor(unquantizedValues[0], unquantizedValues[1], unquantizedValues[2], unquantizedValues[5]);
                break;
            case ColorEndpointMode.LdrRgbaDirect:
                {
                    int sum0 = unquantizedValues[0] + unquantizedValues[2] + unquantizedValues[4];
                    int sum1 = unquantizedValues[1] + unquantizedValues[3] + unquantizedValues[5];
                    if (sum1 >= sum0)
                    {
                        endpointLowRgba = new RgbaColor(unquantizedValues[0], unquantizedValues[2], unquantizedValues[4], unquantizedValues[6]);
                        endpointHighRgba = new RgbaColor(unquantizedValues[1], unquantizedValues[3], unquantizedValues[5], unquantizedValues[7]);
                    }
                    else
                    {
                        endpointLowRgba = new RgbaColor(
                            r: (unquantizedValues[1] + unquantizedValues[5]) >> 1,
                            g: (unquantizedValues[3] + unquantizedValues[5]) >> 1,
                            b: unquantizedValues[5],
                            a: unquantizedValues[7]);
                        endpointHighRgba = new RgbaColor(
                            r: (unquantizedValues[0] + unquantizedValues[4]) >> 1,
                            g: (unquantizedValues[2] + unquantizedValues[4]) >> 1,
                            b: unquantizedValues[4],
                            a: unquantizedValues[6]);
                    }
                    break;
                }
            case ColorEndpointMode.LdrRgbaBaseOffset:
                {
                    var (b0, a0) = BitOperations.TransferPrecision(unquantizedValues[1], unquantizedValues[0]);
                    var (b1, a1) = BitOperations.TransferPrecision(unquantizedValues[3], unquantizedValues[2]);
                    var (b2, a2) = BitOperations.TransferPrecision(unquantizedValues[5], unquantizedValues[4]);
                    var (b3, a3) = BitOperations.TransferPrecision(unquantizedValues[7], unquantizedValues[6]);
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

    public static (RgbaColor endpointLowRgba, RgbaColor endpointHighRgba) DecodeColorsForMode(ReadOnlySpan<int> values, int maxValue, ColorEndpointMode mode)
    {
        int count = mode.GetColorValuesCount();
        Span<int> unquantizedValues = stackalloc int[count];
        int copyLen = Math.Min(count, values.Length);
        for (int i = 0; i < copyLen; i++) unquantizedValues[i] = values[i];
        UnquantizeInline(unquantizedValues, maxValue);
        var pair = DecodeColorsForModeUnquantized(unquantizedValues, mode);
        return (pair.LdrLow, pair.LdrHigh);
    }
}
