// Port of astc-codec/src/decoder/endpoint_codec.{h,cc}
using System;
using System.Collections.Generic;
using System.Linq;

namespace AstcSharp.Reference
{
    public enum EndpointEncodingMode
    {
        kDirectLuma,
        kDirectLumaAlpha,
        kBaseScaleRGB,
        kBaseScaleRGBA,
        kDirectRGB,
        kDirectRGBA
    }

    public static class EndpointCodec
    {
        public static int NumValuesForEncodingMode(EndpointEncodingMode mode)
        {
            return mode == EndpointEncodingMode.kDirectLuma ? 2 :
                   mode == EndpointEncodingMode.kDirectLumaAlpha ? 4 :
                   mode == EndpointEncodingMode.kBaseScaleRGB ? 4 :
                   mode == EndpointEncodingMode.kBaseScaleRGBA ? 6 :
                   mode == EndpointEncodingMode.kDirectRGB ? 6 : 8;
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        private static void BitTransferSigned(ref int a, ref int b)
        {
            b >>= 1;
            b |= a & 0x80;
            a >>= 1;
            a &= 0x3F;
            if ((a & 0x20) != 0) a -= 0x40;
        }

        private static void InvertBitTransferSigned(ref int a, ref int b)
        {
            if (a < -32 || a >= 32) throw new ArgumentOutOfRangeException();
            if (b < 0 || b >= 256) throw new ArgumentOutOfRangeException();

            if (a < 0) a += 0x40;
            a <<= 1;
            a |= (b & 0x80);
            b <<= 1;
            b &= 0xff;
        }

        private static int AverageRGB(RgbaColor c)
        {
            int sum = c[0] + c[1] + c[2];
            return (sum * 256 + 384) / 768;
        }

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

        private static int SquaredError(RgbaColor a, RgbaColor b, int numChannels = 4)
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

        private static RgbaColor InvertBlueContract(RgbaColor c)
        {
            var result = c;
            result[0] = Clamp(2 * c[0] - c[2], 0, 255);
            result[1] = Clamp(2 * c[1] - c[2], 0, 255);
            return result;
        }

        private class QuantizedEndpointPair
        {
            private readonly RgbaColor orig_low_;
            private readonly RgbaColor orig_high_;
            private readonly int[] quant_low_;
            private readonly int[] quant_high_;
            private readonly int[] unquant_low_;
            private readonly int[] unquant_high_;

            public QuantizedEndpointPair(RgbaColor low, RgbaColor high, int maxValue)
            {
                orig_low_ = low;
                orig_high_ = high;
                quant_low_ = QuantizeColorArray(low, maxValue);
                quant_high_ = QuantizeColorArray(high, maxValue);
                unquant_low_ = UnquantizeArray(quant_low_, maxValue);
                unquant_high_ = UnquantizeArray(quant_high_, maxValue);
            }

            public int[] QuantizedLow() => quant_low_;
            public int[] QuantizedHigh() => quant_high_;
            public int[] UnquantizedLow() => unquant_low_;
            public int[] UnquantizedHigh() => unquant_high_;
            public RgbaColor OriginalLow() => orig_low_;
            public RgbaColor OriginalHigh() => orig_high_;
        }

        private class CEEncodingOption
        {
            private readonly int squared_error_;
            private readonly QuantizedEndpointPair quantized_endpoints_;
            private readonly bool swap_endpoints_;
            private readonly bool blue_contract_;
            private readonly bool use_offset_mode_;

            public CEEncodingOption(int squared_error, QuantizedEndpointPair quantized_endpoints,
                bool swap_endpoints, bool blue_contract, bool use_offset_mode)
            {
                squared_error_ = squared_error;
                quantized_endpoints_ = quantized_endpoints;
                swap_endpoints_ = swap_endpoints;
                blue_contract_ = blue_contract;
                use_offset_mode_ = use_offset_mode;
            }

            public bool Pack(bool with_alpha, out ColorEndpointMode astc_mode, List<int> vals, ref bool needs_weight_swap)
            {
                astc_mode = ColorEndpointMode.kLDRLumaDirect;
                var unquantized_low = quantized_endpoints_.UnquantizedLow();
                var unquantized_high = quantized_endpoints_.UnquantizedHigh();

                var u_low = (int[])unquantized_low.Clone();
                var u_high = (int[])unquantized_high.Clone();

                if (use_offset_mode_)
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
                if (use_offset_mode_)
                {
                    if (blue_contract_)
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
                    if (blue_contract_)
                    {
                        if (s1 == s0) return false;
                        swap_vals = s1 > s0;
                        needs_weight_swap = !needs_weight_swap;
                    }
                    else
                    {
                        swap_vals = s1 < s0;
                    }
                }

                var quant_low = quantized_endpoints_.QuantizedLow();
                var quant_high = quantized_endpoints_.QuantizedHigh();

                var qlow = (int[])quant_low.Clone();
                var qhigh = (int[])quant_high.Clone();

                if (swap_vals)
                {
                    if (use_offset_mode_) throw new InvalidOperationException();
                    var tmp = qlow; qlow = qhigh; qhigh = tmp;
                    needs_weight_swap = !needs_weight_swap;
                }

                vals[0] = qlow[0];
                vals[1] = qhigh[0];
                vals[2] = qlow[1];
                vals[3] = qhigh[1];
                vals[4] = qlow[2];
                vals[5] = qhigh[2];

                if (use_offset_mode_)
                {
                    astc_mode = ColorEndpointMode.kLDRRGBBaseOffset;
                }
                else
                {
                    astc_mode = ColorEndpointMode.kLDRRGBDirect;
                }

                if (with_alpha)
                {
                    vals[6] = qlow[3];
                    vals[7] = qhigh[3];
                    if (use_offset_mode_) astc_mode = ColorEndpointMode.kLDRRGBABaseOffset;
                    else astc_mode = ColorEndpointMode.kLDRRGBADirect;
                }

                if (swap_endpoints_)
                {
                    needs_weight_swap = !needs_weight_swap;
                }

                return true;
            }

            public bool BlueContract() => blue_contract_;
            public int Error() => squared_error_;
        }

        public static bool UsesBlueContract(int maxValue, ColorEndpointMode mode, List<int> vals)
        {
            int numVals = Types.NumColorValuesForEndpointMode(mode);
            if (vals.Count < numVals) throw new ArgumentException("vals size");

            switch (mode)
            {
                case ColorEndpointMode.kLDRRGBDirect:
                case ColorEndpointMode.kLDRRGBADirect:
                    {
                        int kNumVals = Math.Max(Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBDirect), Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBADirect));
                        var v = new int[kNumVals];
                        for (int i = 0; i < kNumVals; ++i) v[i] = i < vals.Count ? vals[i] : 0;
                        var uv = UnquantizeArray(v, maxValue);
                        int s0 = uv[0] + uv[2] + uv[4];
                        int s1 = uv[1] + uv[3] + uv[5];
                        return s0 > s1;
                    }
                case ColorEndpointMode.kLDRRGBBaseOffset:
                case ColorEndpointMode.kLDRRGBABaseOffset:
                    {
                        int kNumVals = Math.Max(Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBBaseOffset), Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBABaseOffset));
                        var v = new int[kNumVals];
                        for (int i = 0; i < kNumVals; ++i) v[i] = i < vals.Count ? vals[i] : 0;
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
            astc_mode = ColorEndpointMode.kLDRLumaDirect;
            int numVals = NumValuesForEncodingMode(encoding_mode);
            for (int i = vals.Count; i < numVals; ++i) vals.Add(0);

            switch (encoding_mode)
            {
                case EndpointEncodingMode.kDirectLuma:
                    return EncodeColorsLuma(endpoint_low_rgba, endpoint_high_rgba, maxValue, out astc_mode, vals);
                case EndpointEncodingMode.kDirectLumaAlpha:
                    {
                        int avg1 = AverageRGB(endpoint_low_rgba);
                        int avg2 = AverageRGB(endpoint_high_rgba);
                        vals[0] = Quantization.QuantizeCEValueToRange(avg1, maxValue);
                        vals[1] = Quantization.QuantizeCEValueToRange(avg2, maxValue);
                        vals[2] = Quantization.QuantizeCEValueToRange(endpoint_low_rgba[3], maxValue);
                        vals[3] = Quantization.QuantizeCEValueToRange(endpoint_high_rgba[3], maxValue);
                        astc_mode = ColorEndpointMode.kLDRLumaAlphaDirect;
                    }
                    break;
                case EndpointEncodingMode.kBaseScaleRGB:
                case EndpointEncodingMode.kBaseScaleRGBA:
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
                            int avg_scale = Clamp(scale_sum / num_samples, 0, 255);
                            vals[3] = Quantization.QuantizeCEValueToRange(avg_scale, maxValue);
                        }
                        else
                        {
                            vals[3] = maxValue;
                        }
                        astc_mode = ColorEndpointMode.kLDRRGBBaseScale;

                        if (encoding_mode == EndpointEncodingMode.kBaseScaleRGBA)
                        {
                            vals[4] = Quantization.QuantizeCEValueToRange(scaled[3], maxValue);
                            vals[5] = Quantization.QuantizeCEValueToRange(basec[3], maxValue);
                            astc_mode = ColorEndpointMode.kLDRRGBBaseScaleTwoA;
                        }
                    }
                    break;
                case EndpointEncodingMode.kDirectRGB:
                case EndpointEncodingMode.kDirectRGBA:
                    return EncodeColorsRGBA(endpoint_low_rgba, endpoint_high_rgba, maxValue, encoding_mode == EndpointEncodingMode.kDirectRGBA, out astc_mode, vals);
                default:
                    throw new InvalidOperationException("Unimplemented color encoding.");
            }

            return needs_weight_swap;
        }

        private static bool EncodeColorsLuma(RgbaColor endpoint_low, RgbaColor endpoint_high, int maxValue, out ColorEndpointMode astc_mode, List<int> vals)
        {
            astc_mode = ColorEndpointMode.kLDRLumaDirect;
            if (vals.Count < 2) throw new ArgumentException();
            int avg1 = AverageRGB(endpoint_low);
            int avg2 = AverageRGB(endpoint_high);

            bool needs_weight_swap = false;
            if (avg1 > avg2) { needs_weight_swap = true; var t = avg1; avg1 = avg2; avg2 = t; }

            int offset = Math.Min(avg2 - avg1, 0x3F);
            int quant_off_low = Quantization.QuantizeCEValueToRange((avg1 & 0x3F) << 2, maxValue);
            int quant_off_high = Quantization.QuantizeCEValueToRange((avg1 & 0xC0) | offset, maxValue);

            int quant_low = Quantization.QuantizeCEValueToRange(avg1, maxValue);
            int quant_high = Quantization.QuantizeCEValueToRange(avg2, maxValue);

            vals[0] = quant_off_low;
            vals[1] = quant_off_high;
            DecodeColorsForMode(vals, maxValue, ColorEndpointMode.kLDRLumaBaseOffset, out var dec_low_off, out var dec_high_off);

            vals[0] = quant_low;
            vals[1] = quant_high;
            DecodeColorsForMode(vals, maxValue, ColorEndpointMode.kLDRLumaDirect, out var dec_low_dir, out var dec_high_dir);

            int calculate_error_off = 0;
            int calculate_error_dir = 0;
            if (needs_weight_swap)
            {
                calculate_error_dir = SquaredError(dec_low_dir, endpoint_high, 4) + SquaredError(dec_high_dir, endpoint_low, 4);
                calculate_error_off = SquaredError(dec_low_off, endpoint_high, 4) + SquaredError(dec_high_off, endpoint_low, 4);
            }
            else
            {
                calculate_error_dir = SquaredError(dec_low_dir, endpoint_low, 4) + SquaredError(dec_high_dir, endpoint_high, 4);
                calculate_error_off = SquaredError(dec_low_off, endpoint_low, 4) + SquaredError(dec_high_off, endpoint_high, 4);
            }

            if (calculate_error_dir <= calculate_error_off)
            {
                vals[0] = quant_low;
                vals[1] = quant_high;
                astc_mode = ColorEndpointMode.kLDRLumaDirect;
            }
            else
            {
                vals[0] = quant_off_low;
                vals[1] = quant_off_high;
                astc_mode = ColorEndpointMode.kLDRLumaBaseOffset;
            }

            return needs_weight_swap;
        }

        private static bool EncodeColorsRGBA(RgbaColor endpoint_low_rgba, RgbaColor endpoint_high_rgba, int maxValue, bool with_alpha, out ColorEndpointMode astc_mode, List<int> vals)
        {
            astc_mode = ColorEndpointMode.kLDRRGBDirect;
            int num_channels = with_alpha ? 4 : 3;

            var inv_bc_low = InvertBlueContract(endpoint_low_rgba);
            var inv_bc_high = InvertBlueContract(endpoint_high_rgba);

            var direct_base = new int[4];
            var direct_offset = new int[4];
            for (int i = 0; i < 4; ++i)
            {
                direct_base[i] = endpoint_low_rgba[i];
                direct_offset[i] = Clamp(endpoint_high_rgba[i] - endpoint_low_rgba[i], -32, 31);
                int a = direct_offset[i], b = direct_base[i]; InvertBitTransferSigned(ref a, ref b); direct_offset[i] = a; direct_base[i] = b;
            }

            var inv_bc_base = new int[4];
            var inv_bc_offset = new int[4];
            for (int i = 0; i < 4; ++i)
            {
                inv_bc_base[i] = inv_bc_high[i];
                inv_bc_offset[i] = Clamp(inv_bc_low[i] - inv_bc_high[i], -32, 31);
                int a = inv_bc_offset[i], b = inv_bc_base[i]; InvertBitTransferSigned(ref a, ref b); inv_bc_offset[i] = a; inv_bc_base[i] = b;
            }

            var direct_base_swapped = new int[4];
            var direct_offset_swapped = new int[4];
            for (int i = 0; i < 4; ++i)
            {
                direct_base_swapped[i] = endpoint_high_rgba[i];
                direct_offset_swapped[i] = Clamp(endpoint_low_rgba[i] - endpoint_high_rgba[i], -32, 31);
                int a = direct_offset_swapped[i], b = direct_base_swapped[i]; InvertBitTransferSigned(ref a, ref b); direct_offset_swapped[i] = a; direct_base_swapped[i] = b;
            }

            var inv_bc_base_swapped = new int[4];
            var inv_bc_offset_swapped = new int[4];
            for (int i = 0; i < 4; ++i)
            {
                inv_bc_base_swapped[i] = inv_bc_low[i];
                inv_bc_offset_swapped[i] = Clamp(inv_bc_high[i] - inv_bc_low[i], -32, 31);
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
                var bc_low = bc_quantized.UnquantizedLow();
                var bc_high = bc_quantized.UnquantizedHigh();
                var bc_low_arr = (int[])bc_low.Clone();
                var bc_high_arr = (int[])bc_high.Clone();
                // BlueContract on arrays -> modify first two channels
                bc_low_arr[0] = (bc_low_arr[0] + bc_low_arr[2]) >> 1;
                bc_low_arr[1] = (bc_low_arr[1] + bc_low_arr[2]) >> 1;
                bc_high_arr[0] = (bc_high_arr[0] + bc_high_arr[2]) >> 1;
                bc_high_arr[1] = (bc_high_arr[1] + bc_high_arr[2]) >> 1;

                int sq_bc_error = SquaredError(bc_low_arr, new int[] { endpoint_low_rgba[0], endpoint_low_rgba[1], endpoint_low_rgba[2], endpoint_low_rgba[3] }, num_channels)
                    + SquaredError(bc_high_arr, new int[] { endpoint_high_rgba[0], endpoint_high_rgba[1], endpoint_high_rgba[2], endpoint_high_rgba[3] }, num_channels);
                errors.Add(new CEEncodingOption(sq_bc_error, bc_quantized, false, true, false));
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
                    int a = offsetCopy[i], b = baseCopy[i]; BitTransferSigned(ref a, ref b); offsetCopy[i] = Math.Clamp(baseCopy[i] + a, 0, 255);
                }
                // BlueContract
                baseCopy[0] = (baseCopy[0] + baseCopy[2]) >> 1; baseCopy[1] = (baseCopy[1] + baseCopy[2]) >> 1;
                offsetCopy[0] = (offsetCopy[0] + offsetCopy[2]) >> 1; offsetCopy[1] = (offsetCopy[1] + offsetCopy[2]) >> 1;

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

        public static void DecodeColorsForMode(List<int> vals, int maxValue, ColorEndpointMode mode, out RgbaColor endpoint_low_rgba, out RgbaColor endpoint_high_rgba)
        {
            endpoint_low_rgba = new RgbaColor(0,0,0,0);
            endpoint_high_rgba = new RgbaColor(0,0,0,0);
            switch (mode)
            {
                case ColorEndpointMode.kLDRLumaDirect:
                    {
                        int l0 = Quantization.UnquantizeCEValueFromRange(vals[0], maxValue);
                        int l1 = Quantization.UnquantizeCEValueFromRange(vals[1], maxValue);
                        endpoint_low_rgba = new RgbaColor(l0, l0, l0, 255);
                        endpoint_high_rgba = new RgbaColor(l1, l1, l1, 255);
                    }
                    break;
                case ColorEndpointMode.kLDRLumaBaseOffset:
                    {
                        int v0 = Quantization.UnquantizeCEValueFromRange(vals[0], maxValue);
                        int v1 = Quantization.UnquantizeCEValueFromRange(vals[1], maxValue);
                        int l0 = (v0 >> 2) | (v1 & 0xC0);
                        int l1 = Math.Min(l0 + (v1 & 0x3F), 0xFF);
                        endpoint_low_rgba = new RgbaColor(l0, l0, l0, 255);
                        endpoint_high_rgba = new RgbaColor(l1, l1, l1, 255);
                    }
                    break;
                case ColorEndpointMode.kLDRLumaAlphaDirect:
                    {
                        var v = new int[4];
                        for (int i = 0; i < 4; ++i) v[i] = i < vals.Count ? vals[i] : 0;
                        var uv = UnquantizeArray(v, maxValue);
                        endpoint_low_rgba = new RgbaColor(uv[0], uv[0], uv[0], uv[2]);
                        endpoint_high_rgba = new RgbaColor(uv[1], uv[1], uv[1], uv[3]);
                    }
                    break;
                case ColorEndpointMode.kLDRLumaAlphaBaseOffset:
                    {
                        var v = new int[4]; for (int i=0;i<4;i++) v[i] = i<vals.Count?vals[i]:0;
                        var uv = UnquantizeArray(v, maxValue);
                        int a0 = uv[0], b0 = uv[1]; BitTransferSigned(ref b0, ref a0);
                        int a2 = uv[2], b2 = uv[3]; BitTransferSigned(ref b2, ref a2);
                        endpoint_low_rgba = new RgbaColor(a0, a0, a0, a2);
                        int high_luma = a0 + b0;
                        endpoint_high_rgba = new RgbaColor(high_luma, high_luma, high_luma, a2 + b2);
                        endpoint_low_rgba[0] = Clamp(endpoint_low_rgba[0], 0, 255);
                        endpoint_low_rgba[1] = Clamp(endpoint_low_rgba[1], 0, 255);
                        endpoint_low_rgba[2] = Clamp(endpoint_low_rgba[2], 0, 255);
                        endpoint_high_rgba[0] = Clamp(endpoint_high_rgba[0], 0, 255);
                        endpoint_high_rgba[1] = Clamp(endpoint_high_rgba[1], 0, 255);
                        endpoint_high_rgba[2] = Clamp(endpoint_high_rgba[2], 0, 255);
                    }
                    break;
                case ColorEndpointMode.kLDRRGBBaseScale:
                    {
                        int kNumVals = Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBBaseScale);
                        var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                        var uv = UnquantizeArray(v, maxValue);
                        endpoint_high_rgba = new RgbaColor(uv[0], uv[1], uv[2], 255);
                        for (int i = 0; i < 3; ++i) { int x = endpoint_high_rgba[i]; endpoint_low_rgba[i] = (x * uv[3]) >> 8; }
                        endpoint_low_rgba[3] = 255;
                    }
                    break;
                case ColorEndpointMode.kLDRRGBDirect:
                    {
                        int kNumVals = Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBDirect);
                        var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                        var uv = UnquantizeArray(v, maxValue);
                        int s0 = uv[0] + uv[2] + uv[4];
                        int s1 = uv[1] + uv[3] + uv[5];
                        endpoint_low_rgba = new RgbaColor(uv[0], uv[2], uv[4], 255);
                        endpoint_high_rgba = new RgbaColor(uv[1], uv[3], uv[5], 255);
                        if (s1 < s0)
                        {
                            var t = endpoint_low_rgba; endpoint_low_rgba = endpoint_high_rgba; endpoint_high_rgba = t;
                            endpoint_low_rgba[0] = (endpoint_low_rgba[0] + endpoint_low_rgba[2]) >> 1;
                            endpoint_low_rgba[1] = (endpoint_low_rgba[1] + endpoint_low_rgba[2]) >> 1;
                            endpoint_high_rgba[0] = (endpoint_high_rgba[0] + endpoint_high_rgba[2]) >> 1;
                            endpoint_high_rgba[1] = (endpoint_high_rgba[1] + endpoint_high_rgba[2]) >> 1;
                        }
                    }
                    break;
                case ColorEndpointMode.kLDRRGBBaseOffset:
                    {
                        int kNumVals = Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBBaseOffset);
                        var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                        var uv = UnquantizeArray(v, maxValue);
                        int a0 = uv[0], b0 = uv[1]; BitTransferSigned(ref b0, ref a0);
                        int a1 = uv[2], b1 = uv[3]; BitTransferSigned(ref b1, ref a1);
                        int a2 = uv[4], b2 = uv[5]; BitTransferSigned(ref b2, ref a2);
                        endpoint_low_rgba = new RgbaColor(a0, a1, a2, 255);
                        endpoint_high_rgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2, 255);
                        if (b0 + b1 + b2 < 0) {
                            var t = endpoint_low_rgba; endpoint_low_rgba = endpoint_high_rgba; endpoint_high_rgba = t;
                            endpoint_low_rgba[0] = (endpoint_low_rgba[0] + endpoint_low_rgba[2]) >> 1;
                            endpoint_low_rgba[1] = (endpoint_low_rgba[1] + endpoint_low_rgba[2]) >> 1;
                            endpoint_high_rgba[0] = (endpoint_high_rgba[0] + endpoint_high_rgba[2]) >> 1;
                            endpoint_high_rgba[1] = (endpoint_high_rgba[1] + endpoint_high_rgba[2]) >> 1;
                        }
                        for (int i=0;i<3;++i) { endpoint_low_rgba[i] = Clamp(endpoint_low_rgba[i], 0, 255); endpoint_high_rgba[i] = Clamp(endpoint_high_rgba[i], 0, 255); }
                    }
                    break;
                case ColorEndpointMode.kLDRRGBBaseScaleTwoA:
                    {
                        int kNumVals = Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBBaseScaleTwoA);
                        var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                        var uv = UnquantizeArray(v, maxValue);
                        endpoint_low_rgba = new RgbaColor(uv[0], uv[1], uv[2], 255);
                        endpoint_high_rgba = new RgbaColor(uv[0], uv[1], uv[2], 255);
                        for (int i=0;i<3;++i) endpoint_low_rgba[i] = (endpoint_low_rgba[i] * uv[3]) >> 8;
                        endpoint_low_rgba[3] = uv[4];
                        endpoint_high_rgba[3] = uv[5];
                    }
                    break;
                case ColorEndpointMode.kLDRRGBADirect:
                    {
                        int kNumVals = Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBADirect);
                        var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                        var uv = UnquantizeArray(v, maxValue);
                        int s0 = uv[0] + uv[2] + uv[4];
                        int s1 = uv[1] + uv[3] + uv[5];
                        endpoint_low_rgba = new RgbaColor(uv[0], uv[2], uv[4], uv[6]);
                        endpoint_high_rgba = new RgbaColor(uv[1], uv[3], uv[5], uv[7]);
                        if (s1 < s0)
                        {
                            var t = endpoint_low_rgba; endpoint_low_rgba = endpoint_high_rgba; endpoint_high_rgba = t;
                            endpoint_low_rgba[0] = (endpoint_low_rgba[0] + endpoint_low_rgba[2]) >> 1;
                            endpoint_low_rgba[1] = (endpoint_low_rgba[1] + endpoint_low_rgba[2]) >> 1;
                            endpoint_high_rgba[0] = (endpoint_high_rgba[0] + endpoint_high_rgba[2]) >> 1;
                            endpoint_high_rgba[1] = (endpoint_high_rgba[1] + endpoint_high_rgba[2]) >> 1;
                        }
                    }
                    break;
                case ColorEndpointMode.kLDRRGBABaseOffset:
                    {
                        int kNumVals = Types.NumColorValuesForEndpointMode(ColorEndpointMode.kLDRRGBABaseOffset);
                        var v = new int[kNumVals]; for (int i=0;i<kNumVals;++i) v[i] = i<vals.Count?vals[i]:0;
                        var uv = UnquantizeArray(v, maxValue);
                        int a0 = uv[0], b0 = uv[1]; BitTransferSigned(ref b0, ref a0);
                        int a1 = uv[2], b1 = uv[3]; BitTransferSigned(ref b1, ref a1);
                        int a2 = uv[4], b2 = uv[5]; BitTransferSigned(ref b2, ref a2);
                        int a3 = uv[6], b3 = uv[7]; BitTransferSigned(ref b3, ref a3);
                        endpoint_low_rgba = new RgbaColor(a0, a1, a2, a3);
                        endpoint_high_rgba = new RgbaColor(a0 + b0, a1 + b1, a2 + b2, a3 + b3);
                        if (b0 + b1 + b2 < 0)
                        {
                            var t = endpoint_low_rgba; endpoint_low_rgba = endpoint_high_rgba; endpoint_high_rgba = t;
                            endpoint_low_rgba[0] = (endpoint_low_rgba[0] + endpoint_low_rgba[2]) >> 1;
                            endpoint_low_rgba[1] = (endpoint_low_rgba[1] + endpoint_low_rgba[2]) >> 1;
                            endpoint_high_rgba[0] = (endpoint_high_rgba[0] + endpoint_high_rgba[2]) >> 1;
                            endpoint_high_rgba[1] = (endpoint_high_rgba[1] + endpoint_high_rgba[2]) >> 1;
                        }
                        for (int i=0;i<4;++i) { endpoint_low_rgba[i] = Clamp(endpoint_low_rgba[i], 0, 255); endpoint_high_rgba[i] = Clamp(endpoint_high_rgba[i], 0, 255); }
                    }
                    break;
                default:
                    endpoint_low_rgba = new RgbaColor(0,0,0,0);
                    endpoint_high_rgba = new RgbaColor(0,0,0,0);
                    break;
            }
        }
    }
}
