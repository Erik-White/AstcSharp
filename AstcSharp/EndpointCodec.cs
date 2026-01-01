namespace AstcSharp;

internal static class EndpointCodec
{
    /// <summary>
    /// The 'bit_transfer_signed' function defined in Section C.2.14 of the ASTC specification
    /// </summary>
    private static void BitTransferSigned(ref int a, ref int b)
    {
        b >>= 1;
        b |= a & 0x80;
        a >>= 1;
        a &= 0x3F;

        if ((a & 0x20) != 0)
            a -= 0x40;
    }

    /// <summary>
    /// Takes two values, |a| in the range [-32, 31], and |b| in the range [0, 255],
    /// and returns the two values in [0, 255] that will reconstruct |a| and |b| when
    /// passed to the <see cref="BitTransferSigned"/> function.
    /// </summary>
    private static void InvertBitTransferSigned(ref int a, ref int b)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(a, -32);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(a, 31);
        ArgumentOutOfRangeException.ThrowIfLessThan(b, byte.MinValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(b, byte.MaxValue);

        if (a < 0)
            a += 0x40;

        a <<= 1;
        a |= b & 0x80;
        b <<= 1;
        b &= 0xff;
    }

    // Move to rgb or rgb extensions?
    private static int SquaredError(int[] a, int[] b, int numChannels = 4)
    {
        int result = 0;
        for (int i = 0; i < numChannels; ++i)
        {
            int diff = a[i] - b[i];
            result += diff * diff;
        }
        return result;
    }

    private static int[] QuantizeColorArray(RgbaColor c, int maxValue)
    {
        var arr = new int[4];
        for (int i = 0; i < 4; ++i) arr[i] = Quantization.QuantizeCEValueToRange(c[i], maxValue);
        return arr;
    }

    private static int[] UnquantizeArray(int[] v, int maxValue)
    {
        var res = new int[v.Length];
        for (int i = 0; i < v.Length; ++i) res[i] = Quantization.UnquantizeCEValueFromRange(v[i], maxValue);
        return res;
    }

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
            int squared_error,
            QuantizedEndpointPair quantized_endpoints,
            bool swapEndpoints,
            bool blueContract,
            bool useOffsetMode)
        {
            _squaredError = squared_error;
            _quantizedEndpoints = quantized_endpoints;
            _swapEndpoints = swapEndpoints;
            _blueContract = blueContract;
            _useOffsetMode = useOffsetMode;
        }

        public bool Pack(bool hasAlpha, out ColorEndpointMode endpointMode, List<int> values, ref bool needsWeightSwap)
        {
            endpointMode = ColorEndpointMode.LdrLumaDirect;
            var unquantizedLow = _quantizedEndpoints.UnquantizedLow();
            var unquantizedHigh = _quantizedEndpoints.UnquantizedHigh();

            var u_low = (int[])unquantizedLow.Clone();
            var u_high = (int[])unquantizedHigh.Clone();

            if (_useOffsetMode)
            {
                for (int i = 0; i < 4; ++i)
                {
                    int a = u_high[i]; int b = u_low[i];
                    BitTransferSigned(ref a, ref b);
                    u_high[i] = a; u_low[i] = b;
                }
            }

            int s0 = 0, s1 = 0;
            for (int i = 0; i < 3; ++i)
            {
                s0 += u_low[i];
                s1 += u_high[i];
            }

            bool swap_vals = false;
            if (_useOffsetMode)
            {
                if (_blueContract)
                {
                    swap_vals = s1 >= 0;
                }
                else
                {
                    swap_vals = s1 < 0;
                }

                if (swap_vals) return false;
            }
            else
            {
                if (_blueContract)
                {
                    if (s1 == s0) return false;
                    swap_vals = s1 > s0;
                    needsWeightSwap = !needsWeightSwap;
                }
                else
                {
                    swap_vals = s1 < s0;
                }
            }

            var quant_low = _quantizedEndpoints.QuantizedLow();
            var quant_high = _quantizedEndpoints.QuantizedHigh();

            var qLow = (int[])quant_low.Clone();
            var qHigh = (int[])quant_high.Clone();

            if (swap_vals)
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
                    int a0 = uv[0], b0 = uv[1]; BitTransferSigned(ref b0, ref a0);
                    int a1 = uv[2], b1 = uv[3]; BitTransferSigned(ref b1, ref a1);
                    int a2 = uv[4], b2 = uv[5]; BitTransferSigned(ref b2, ref a2);
                    return (b0 + b1 + b2) < 0;
                }
            default:
                return false;
        }
    }

    public static bool EncodeColorsForMode(RgbaColor endpoint_low_rgba, RgbaColor endpoint_high_rgba, int maxValue, EndpointEncodingMode encoding_mode, out ColorEndpointMode astc_mode, List<int> vals)
    {
        bool needs_weight_swap = false;
        astc_mode = ColorEndpointMode.LdrLumaDirect;
        int numVals = encoding_mode.GetValuesCount();
        for (int i = vals.Count; i < numVals; ++i) vals.Add(0);

        switch (encoding_mode)
        {
            case EndpointEncodingMode.DirectLuma:
                return EncodeColorsLuma(endpoint_low_rgba, endpoint_high_rgba, maxValue, out astc_mode, vals);
            case EndpointEncodingMode.DirectLumaAlpha:
                {
                    int avg1 = endpoint_low_rgba.Average;
                    int avg2 = endpoint_high_rgba.Average;
                    vals[0] = Quantization.QuantizeCEValueToRange(avg1, maxValue);
                    vals[1] = Quantization.QuantizeCEValueToRange(avg2, maxValue);
                    vals[2] = Quantization.QuantizeCEValueToRange(endpoint_low_rgba[3], maxValue);
                    vals[3] = Quantization.QuantizeCEValueToRange(endpoint_high_rgba[3], maxValue);
                    astc_mode = ColorEndpointMode.LdrLumaAlphaDirect;
                }
                break;
            case EndpointEncodingMode.BaseScaleRgb:
            case EndpointEncodingMode.BaseScaleRgba:
                {
                    var basec = endpoint_high_rgba;
                    var scaled = endpoint_low_rgba;

                    int num_channels_ge = 0;
                    for (int i = 0; i < 3; ++i) num_channels_ge += endpoint_high_rgba[i] >= endpoint_low_rgba[i] ? 1 : 0;

                    if (num_channels_ge < 2)
                    {
                        needs_weight_swap = true;
                        var t = basec; basec = scaled; scaled = t;
                    }

                    var q_base = QuantizeColorArray(basec, maxValue);
                    var uq_base = UnquantizeArray(q_base, maxValue);

                    int num_samples = 0;
                    int scale_sum = 0;
                    for (int i = 0; i < 3; ++i)
                    {
                        int x = uq_base[i];
                        if (x != 0)
                        {
                            ++num_samples;
                            scale_sum += (scaled[i] * 256) / x;
                        }
                    }

                    vals[0] = q_base[0];
                    vals[1] = q_base[1];
                    vals[2] = q_base[2];
                    if (num_samples > 0)
                    {
                        int avg_scale = Math.Clamp(scale_sum / num_samples, 0, 255);
                        vals[3] = Quantization.QuantizeCEValueToRange(avg_scale, maxValue);
                    }
                    else
                    {
                        vals[3] = maxValue;
                    }
                    astc_mode = ColorEndpointMode.LdrRgbBaseScale;

                    if (encoding_mode == EndpointEncodingMode.BaseScaleRgba)
                    {
                        vals[4] = Quantization.QuantizeCEValueToRange(scaled[3], maxValue);
                        vals[5] = Quantization.QuantizeCEValueToRange(basec[3], maxValue);
                        astc_mode = ColorEndpointMode.LdrRgbBaseScaleTwoA;
                    }
                }
                break;
            case EndpointEncodingMode.DirectRbg:
            case EndpointEncodingMode.DirectRgba:
                return EncodeColorsRGBA(endpoint_low_rgba, endpoint_high_rgba, maxValue, encoding_mode == EndpointEncodingMode.DirectRgba, out astc_mode, vals);
            default:
                throw new InvalidOperationException("Unimplemented color encoding.");
        }

        return needs_weight_swap;
    }

    private static bool EncodeColorsLuma(RgbaColor endpoint_low, RgbaColor endpoint_high, int maxValue, out ColorEndpointMode astc_mode, List<int> vals)
    {
        astc_mode = ColorEndpointMode.LdrLumaDirect;
        ArgumentOutOfRangeException.ThrowIfLessThan(vals.Count, 2);
        
        int avg1 = endpoint_low.Average;
        int avg2 = endpoint_high.Average;

        bool needs_weight_swap = false;
        if (avg1 > avg2) { needs_weight_swap = true; var t = avg1; avg1 = avg2; avg2 = t; }

        int offset = Math.Min(avg2 - avg1, 0x3F);
        int quant_off_low = Quantization.QuantizeCEValueToRange((avg1 & 0x3F) << 2, maxValue);
        int quant_off_high = Quantization.QuantizeCEValueToRange((avg1 & 0xC0) | offset, maxValue);

        int quant_low = Quantization.QuantizeCEValueToRange(avg1, maxValue);
        int quant_high = Quantization.QuantizeCEValueToRange(avg2, maxValue);

        vals[0] = quant_off_low;
        vals[1] = quant_off_high;
        var (dec_low_off, dec_high_off) = DecodeColorsForMode(vals, maxValue, ColorEndpointMode.LdrLumaBaseOffset);

        vals[0] = quant_low;
        vals[1] = quant_high;
        var (dec_low_dir, dec_high_dir) = DecodeColorsForMode(vals, maxValue, ColorEndpointMode.LdrLumaDirect);
        
        int calculate_error_off = 0;
        int calculate_error_dir = 0;
        if (needs_weight_swap)
        {
            calculate_error_dir = RgbaColor.SquaredError(dec_low_dir, endpoint_high) + RgbaColor.SquaredError(dec_high_dir, endpoint_low);
            calculate_error_off = RgbaColor.SquaredError(dec_low_off, endpoint_high) + RgbaColor.SquaredError(dec_high_off, endpoint_low);
        }
        else
        {
            calculate_error_dir = RgbaColor.SquaredError(dec_low_dir, endpoint_low) + RgbaColor.SquaredError(dec_high_dir, endpoint_high);
            calculate_error_off = RgbaColor.SquaredError(dec_low_off, endpoint_low) + RgbaColor.SquaredError(dec_high_off, endpoint_high);
        }

        if (calculate_error_dir <= calculate_error_off)
        {
            vals[0] = quant_low;
            vals[1] = quant_high;
            astc_mode = ColorEndpointMode.LdrLumaDirect;
        }
        else
        {
            vals[0] = quant_off_low;
            vals[1] = quant_off_high;
            astc_mode = ColorEndpointMode.LdrLumaBaseOffset;
        }

        return needs_weight_swap;
    }

    private static bool EncodeColorsRGBA(RgbaColor endpoint_low_rgba, RgbaColor endpoint_high_rgba, int maxValue, bool with_alpha, out ColorEndpointMode astc_mode, List<int> vals)
    {
        astc_mode = ColorEndpointMode.LdrRgbDirect;
        int num_channels = with_alpha ? 4 : 3;

        var inv_bc_low = endpoint_low_rgba.WithInvertedBlueContract();
        var inv_bc_high = endpoint_high_rgba.WithInvertedBlueContract();

        var direct_base = new int[4];
        var direct_offset = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            direct_base[i] = endpoint_low_rgba[i];
            direct_offset[i] = Math.Clamp(endpoint_high_rgba[i] - endpoint_low_rgba[i], -32, 31);
            int a = direct_offset[i], b = direct_base[i]; InvertBitTransferSigned(ref a, ref b); direct_offset[i] = a; direct_base[i] = b;
        }

        var inv_bc_base = new int[4];
        var inv_bc_offset = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            inv_bc_base[i] = inv_bc_high[i];
            inv_bc_offset[i] = Math.Clamp(inv_bc_low[i] - inv_bc_high[i], -32, 31);
            int a = inv_bc_offset[i], b = inv_bc_base[i]; InvertBitTransferSigned(ref a, ref b); inv_bc_offset[i] = a; inv_bc_base[i] = b;
        }

        var direct_base_swapped = new int[4];
        var direct_offset_swapped = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            direct_base_swapped[i] = endpoint_high_rgba[i];
            direct_offset_swapped[i] = Math.Clamp(endpoint_low_rgba[i] - endpoint_high_rgba[i], -32, 31);
            int a = direct_offset_swapped[i], b = direct_base_swapped[i]; InvertBitTransferSigned(ref a, ref b); direct_offset_swapped[i] = a; direct_base_swapped[i] = b;
        }

        var inv_bc_base_swapped = new int[4];
        var inv_bc_offset_swapped = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            inv_bc_base_swapped[i] = inv_bc_low[i];
            inv_bc_offset_swapped[i] = Math.Clamp(inv_bc_high[i] - inv_bc_low[i], -32, 31);
            int a = inv_bc_offset_swapped[i], b = inv_bc_base_swapped[i]; InvertBitTransferSigned(ref a, ref b); inv_bc_offset_swapped[i] = a; inv_bc_base_swapped[i] = b;
        }

        var direct_quantized = new QuantizedEndpointPair(endpoint_low_rgba, endpoint_high_rgba, maxValue);
        var bc_quantized = new QuantizedEndpointPair(inv_bc_low, inv_bc_high, maxValue);

        var offset_quantized = new QuantizedEndpointPair(new RgbaColor(direct_base[0], direct_base[1], direct_base[2], direct_base[3]), new RgbaColor(direct_offset[0], direct_offset[1], direct_offset[2], direct_offset[3]), maxValue);
        var bc_offset_quantized = new QuantizedEndpointPair(new RgbaColor(inv_bc_base[0], inv_bc_base[1], inv_bc_base[2], inv_bc_base[3]), new RgbaColor(inv_bc_offset[0], inv_bc_offset[1], inv_bc_offset[2], inv_bc_offset[3]), maxValue);

        var offset_swapped_quantized = new QuantizedEndpointPair(new RgbaColor(direct_base_swapped[0], direct_base_swapped[1], direct_base_swapped[2], direct_base_swapped[3]), new RgbaColor(direct_offset_swapped[0], direct_offset_swapped[1], direct_offset_swapped[2], direct_offset_swapped[3]), maxValue);
        var bc_offset_swapped_quantized = new QuantizedEndpointPair(new RgbaColor(inv_bc_base_swapped[0], inv_bc_base_swapped[1], inv_bc_base_swapped[2], inv_bc_base_swapped[3]), new RgbaColor(inv_bc_offset_swapped[0], inv_bc_offset_swapped[1], inv_bc_offset_swapped[2], inv_bc_offset_swapped[3]), maxValue);

        var errors = new List<CEEncodingOption>(6);

        // 3.1 regular unquantized error
        {
            var rgba_low = direct_quantized.UnquantizedLow();
            var rgba_high = direct_quantized.UnquantizedHigh();
            int sq_rgb_error = SquaredError(rgba_low, new int[] { endpoint_low_rgba[0], endpoint_low_rgba[1], endpoint_low_rgba[2], endpoint_low_rgba[3] }, num_channels)
                + SquaredError(rgba_high, new int[] { endpoint_high_rgba[0], endpoint_high_rgba[1], endpoint_high_rgba[2], endpoint_high_rgba[3] }, num_channels);
            errors.Add(new CEEncodingOption(sq_rgb_error, direct_quantized, false, false, false));
        }

        // 3.2 blue-contract
        {
            var blueContractUnquantizedLow = bc_quantized.UnquantizedLow();
            var blueContractUnquantizedHigh = bc_quantized.UnquantizedHigh();
            var blueContractLow = RgbaColorExtensions.WithBlueContract(blueContractUnquantizedLow[0], blueContractUnquantizedLow[1], blueContractUnquantizedLow[2], blueContractUnquantizedLow[3]);
            var blueContractHigh = RgbaColorExtensions.WithBlueContract(blueContractUnquantizedHigh[0], blueContractUnquantizedHigh[1], blueContractUnquantizedHigh[2], blueContractUnquantizedHigh[3]);
            // TODO: How to handle alpha for this entire functions??
            var blueContractSquaredError = with_alpha
                ? RgbaColor.SquaredError(blueContractLow, endpoint_low_rgba) + RgbaColor.SquaredError(blueContractHigh, endpoint_high_rgba)
                : RgbColor.SquaredError(blueContractLow, endpoint_low_rgba) + RgbColor.SquaredError(blueContractHigh, endpoint_high_rgba);
            
            errors.Add(new CEEncodingOption(blueContractSquaredError, bc_quantized, swapEndpoints: false, blueContract: true, useOffsetMode: false));
        }

        // 3.3 base/offset
        Action<QuantizedEndpointPair, bool> compute_base_offset_error = (pair, swapped) =>
        {
            var baseArr = pair.UnquantizedLow();
            var offsetArr = pair.UnquantizedHigh();
            var baseCopy = (int[])baseArr.Clone();
            var offsetCopy = (int[])offsetArr.Clone();
            for (int i = 0; i < num_channels; ++i)
            {
                int a = offsetCopy[i], b = baseCopy[i]; BitTransferSigned(ref a, ref b); offsetCopy[i] = Math.Clamp(baseCopy[i] + a, 0, 255);
            }

            int base_offset_error = 0;
            if (swapped)
            {
                base_offset_error = SquaredError(baseCopy, new int[] { endpoint_high_rgba[0], endpoint_high_rgba[1], endpoint_high_rgba[2], endpoint_high_rgba[3] }, num_channels)
                    + SquaredError(offsetCopy, new int[] { endpoint_low_rgba[0], endpoint_low_rgba[1], endpoint_low_rgba[2], endpoint_low_rgba[3] }, num_channels);
            }
            else
            {
                base_offset_error = SquaredError(baseCopy, new int[] { endpoint_low_rgba[0], endpoint_low_rgba[1], endpoint_low_rgba[2], endpoint_low_rgba[3] }, num_channels)
                    + SquaredError(offsetCopy, new int[] { endpoint_high_rgba[0], endpoint_high_rgba[1], endpoint_high_rgba[2], endpoint_high_rgba[3] }, num_channels);
            }

            errors.Add(new CEEncodingOption(base_offset_error, pair, swapped, false, true));
        };

        compute_base_offset_error(offset_quantized, false);

        Action<QuantizedEndpointPair, bool> compute_base_offset_blue_contract_error = (pair, swapped) =>
        {
            var baseArr = pair.UnquantizedLow();
            var offsetArr = pair.UnquantizedHigh();
            var baseCopy = (int[])baseArr.Clone();
            var offsetCopy = (int[])offsetArr.Clone();
            for (int i = 0; i < num_channels; ++i)
            {
                int a = offsetCopy[i],
                b = baseCopy[i];
                BitTransferSigned(ref a, ref b);
                offsetCopy[i] = Math.Clamp(baseCopy[i] + a, 0, 255);
            }
            // BlueContract, see section C.2.14 of the ASTC specification
            baseCopy[0] = (baseCopy[0] + baseCopy[2]) >> 1;
            baseCopy[1] = (baseCopy[1] + baseCopy[2]) >> 1;
            offsetCopy[0] = (offsetCopy[0] + offsetCopy[2]) >> 1;
            offsetCopy[1] = (offsetCopy[1] + offsetCopy[2]) >> 1;

            int sq_bc_error = 0;
            if (swapped)
            {
                sq_bc_error = SquaredError(baseCopy, new int[] { endpoint_low_rgba[0], endpoint_low_rgba[1], endpoint_low_rgba[2], endpoint_low_rgba[3] }, num_channels)
                    + SquaredError(offsetCopy, new int[] { endpoint_high_rgba[0], endpoint_high_rgba[1], endpoint_high_rgba[2], endpoint_high_rgba[3] }, num_channels);
            }
            else
            {
                sq_bc_error = SquaredError(baseCopy, new int[] { endpoint_high_rgba[0], endpoint_high_rgba[1], endpoint_high_rgba[2], endpoint_high_rgba[3] }, num_channels)
                    + SquaredError(offsetCopy, new int[] { endpoint_low_rgba[0], endpoint_low_rgba[1], endpoint_low_rgba[2], endpoint_low_rgba[3] }, num_channels);
            }

            errors.Add(new CEEncodingOption(sq_bc_error, pair, swapped, true, true));
        };

        compute_base_offset_blue_contract_error(bc_offset_quantized, false);
        compute_base_offset_error(offset_swapped_quantized, true);
        compute_base_offset_blue_contract_error(bc_offset_swapped_quantized, true);

        errors.Sort((a, b) => a.Error().CompareTo(b.Error()));

        foreach (var measurement in errors)
        {
            bool needs_weight_swap = false;
            ColorEndpointMode modeUnused;
            if (measurement.Pack(with_alpha, out modeUnused, vals, ref needs_weight_swap))
            {
                return needs_weight_swap;
            }
        }

        throw new InvalidOperationException("Shouldn't have reached this point");
    }

    public static (RgbaColor endpoint_low_rgba, RgbaColor endpoint_high_rgba) DecodeColorsForMode(List<int> vals, int maxValue, ColorEndpointMode mode)
    {
        var endpoint_low_rgba = RgbaColor.Empty;
        var endpoint_high_rgba = RgbaColor.Empty;

        switch (mode)
        {
            case ColorEndpointMode.LdrLumaDirect:
                {
                    int l0 = Quantization.UnquantizeCEValueFromRange(vals[0], maxValue);
                    int l1 = Quantization.UnquantizeCEValueFromRange(vals[1], maxValue);
                    endpoint_low_rgba = new RgbaColor(l0, l0, l0);
                    endpoint_high_rgba = new RgbaColor(l1, l1, l1);
                }
                break;
            case ColorEndpointMode.LdrLumaBaseOffset:
                {
                    int v0 = Quantization.UnquantizeCEValueFromRange(vals[0], maxValue);
                    int v1 = Quantization.UnquantizeCEValueFromRange(vals[1], maxValue);
                    int l0 = (v0 >> 2) | (v1 & 0xC0);
                    int l1 = Math.Min(l0 + (v1 & 0x3F), 0xFF);
                    endpoint_low_rgba = new RgbaColor(l0, l0, l0);
                    endpoint_high_rgba = new RgbaColor(l1, l1, l1);
                }
                break;
            case ColorEndpointMode.LdrLumaAlphaDirect:
                {
                    var v = new int[4];
                    for (int i = 0; i < 4; ++i) v[i] = i < vals.Count ? vals[i] : 0;
                    var uv = UnquantizeArray(v, maxValue);
                    endpoint_low_rgba = new RgbaColor(uv[0], uv[0], uv[0], uv[2]);
                    endpoint_high_rgba = new RgbaColor(uv[1], uv[1], uv[1], uv[3]);
                }
                break;
            case ColorEndpointMode.LdrLumaAlphaBaseOffset:
                {
                    var v = new int[4]; for (int i=0;i<4;i++) v[i] = i<vals.Count?vals[i]:0;
                    var uv = UnquantizeArray(v, maxValue);
                    int a0 = uv[0], b0 = uv[1]; BitTransferSigned(ref b0, ref a0);
                    int a2 = uv[2], b2 = uv[3]; BitTransferSigned(ref b2, ref a2);
                    endpoint_low_rgba = new RgbaColor(a0, a0, a0, a2);
                    int high_luma = a0 + b0;
                    endpoint_high_rgba = new RgbaColor(high_luma, high_luma, high_luma, a2 + b2);
                }
                break;
            case ColorEndpointMode.LdrRgbBaseScale:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbBaseScale.GetColorValuesCount();
                    var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                    var uv = UnquantizeArray(v, maxValue);

                    endpoint_low_rgba = new RgbaColor(
                        (uv[0] * uv[3]) >> 8,
                        (uv[1] * uv[3]) >> 8,
                        (uv[2] * uv[3]) >> 8);
                    endpoint_high_rgba = new RgbaColor(
                        uv[0],
                        uv[1],
                        uv[2]);
                }
                break;
            case ColorEndpointMode.LdrRgbDirect:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbDirect.GetColorValuesCount();
                    var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                    var uv = UnquantizeArray(v, maxValue);
                    int s0 = uv[0] + uv[2] + uv[4];
                    int s1 = uv[1] + uv[3] + uv[5];
                    
                    if (s1 < s0)
                    {
                        endpoint_low_rgba = new RgbaColor(
                            r: (uv[1] + uv[5]) >> 1,
                            g: (uv[3] + uv[5]) >> 1,
                            b: uv[5]);
                        endpoint_high_rgba = new RgbaColor(
                            r: (uv[0] + uv[4]) >> 1,
                            g: (uv[2] + uv[4]) >> 1,
                            b: uv[4]);
                    }
                    else
                    {
                        endpoint_low_rgba = new RgbaColor(uv[0], uv[2], uv[4]);
                        endpoint_high_rgba = new RgbaColor(uv[1], uv[3], uv[5]);
                    }
                }
                break;
            case ColorEndpointMode.LdrRgbBaseOffset:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbBaseOffset.GetColorValuesCount();
                    var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                    var uv = UnquantizeArray(v, maxValue);
                    int a0 = uv[0], b0 = uv[1]; BitTransferSigned(ref b0, ref a0);
                    int a1 = uv[2], b1 = uv[3]; BitTransferSigned(ref b1, ref a1);
                    int a2 = uv[4], b2 = uv[5]; BitTransferSigned(ref b2, ref a2);
                    
                    if (b0 + b1 + b2 < 0)
                    {
                        endpoint_low_rgba = new RgbaColor(
                            r: (a0 + b0 + a2 + b2) >> 1,
                            g: (a1 + b1 + a2 + b2) >> 1,
                            b: a2 + b2);
                        endpoint_high_rgba = new RgbaColor(
                            r: (a0 + a2) >> 1,
                            g: (a1 + a2) >> 1,
                            b: a2);
                    }
                    else
                    {
                        endpoint_low_rgba = new RgbaColor(a0, a1, a2);
                        endpoint_high_rgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2);
                    }
                }
                break;
            case ColorEndpointMode.LdrRgbBaseScaleTwoA:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbBaseScaleTwoA.GetColorValuesCount();
                    var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                    var uv = UnquantizeArray(v, maxValue);
                    endpoint_low_rgba = new RgbaColor(
                        r: (uv[0] * uv[3]) >> 8,
                        g: (uv[1] * uv[3]) >> 8,
                        b: (uv[2] * uv[3]) >> 8,
                        a: uv[4]);
                    endpoint_high_rgba = new RgbaColor(uv[0], uv[1], uv[2], uv[5]);
                }
                break;
            case ColorEndpointMode.LdrRgbaDirect:
                {
                    int kNumVals = ColorEndpointMode.LdrRgbaDirect.GetColorValuesCount();
                    var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                    var uv = UnquantizeArray(v, maxValue);
                    int s0 = uv[0] + uv[2] + uv[4];
                    int s1 = uv[1] + uv[3] + uv[5];

                    if (s1 >= s0)
                    {
                        endpoint_low_rgba = new RgbaColor(uv[0], uv[2], uv[4], uv[6]);
                        endpoint_high_rgba = new RgbaColor(uv[1], uv[3], uv[5], uv[7]);
                    }
                    else
                    {
                        endpoint_low_rgba = new RgbaColor(
                            r: (uv[1] + uv[5]) >> 1,
                            g: (uv[3] + uv[5]) >> 1,
                            b: uv[5],
                            a: uv[7]);
                        endpoint_high_rgba = new RgbaColor(
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
                    var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                    var uv = UnquantizeArray(v, maxValue);
                    int a0 = uv[0], b0 = uv[1]; BitTransferSigned(ref b0, ref a0);
                    int a1 = uv[2], b1 = uv[3]; BitTransferSigned(ref b1, ref a1);
                    int a2 = uv[4], b2 = uv[5]; BitTransferSigned(ref b2, ref a2);
                    int a3 = uv[6], b3 = uv[7]; BitTransferSigned(ref b3, ref a3);
                    
                    if (b0 + b1 + b2 < 0)
                    {
                        endpoint_low_rgba = new RgbaColor(
                            r: (a0 + b0 + a2 + b2) >> 1,
                            g: (a1 + b1 + a2 + b2) >> 1,
                            b: a2 + b2,
                            a: a3 + b3);
                        endpoint_high_rgba = new RgbaColor(
                            r: (a0 + a2) >> 1,
                            g: (a1 + a2) >> 1,
                            b: a2,
                            a: a3);
                    }
                    else
                    {
                        endpoint_low_rgba = new RgbaColor(a0, a1, a2, a3);
                        endpoint_high_rgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2, a3 + b3);
                    }
                }
                break;
            default:
                break;
        }

        return (endpoint_low_rgba, endpoint_high_rgba);
    }
}
