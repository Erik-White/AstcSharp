namespace AstcSharp.BiseEncoding;

internal static class Quantization
{
    public const int kEndpointRangeMinValue = 5;
    public const int kWeightRangeMaxValue = 31;

    private static int GetUnquantizedTritValue(int trit, int bits, int range)
    {
        int a = (bits & 1) != 0 ? 0x1FF : 0;
        int b = 0, c = 0;
        switch (range)
        {
            case 5:
                b = 0; c = 204; break;
            case 11:
            {
                int x = (bits >> 1) & 0x1;
                b = (x << 1) | (x << 2) | (x << 4) | (x << 8);
                c = 93;
            }
            break;
            case 23:
            {
                int x = (bits >> 1) & 0x3;
                b = x | (x << 2) | (x << 7);
                c = 44;
            }
            break;
            case 47:
            {
                int x = (bits >> 1) & 0x7;
                b = x | (x << 6);
                c = 22;
            }
            break;
            case 95:
            {
                int x = (bits >> 1) & 0xF;
                b = (x >> 2) | (x << 5);
                c = 11;
            }
            break;
            case 191:
            {
                int x = (bits >> 1) & 0x1F;
                b = (x >> 4) | (x << 4);
                c = 5;
            }
            break;
            default:
                throw new ArgumentException("Illegal trit encoding");
        }
        int t = trit * c + b;
        t ^= a;
        t = (a & 0x80) | (t >> 2);
        return t;
    }

    private static int GetUnquantizedQuintValue(int quint, int bits, int range)
    {
        int a = (bits & 1) != 0 ? 0x1FF : 0;
        int b = 0, c = 0;
        switch (range)
        {
            case 9: b = 0; c = 113; break;
            case 19:
            {
                int x = (bits >> 1) & 0x1;
                b = (x << 2) | (x << 3) | (x << 8);
                c = 54;
            }
            break;
            case 39:
            {
                int x = (bits >> 1) & 0x3;
                b = (x >> 1) | (x << 1) | (x << 7);
                c = 26;
            }
            break;
            case 79:
            {
                int x = (bits >> 1) & 0x7;
                b = (x >> 1) | (x << 6);
                c = 13;
            }
            break;
            case 159:
            {
                int x = (bits >> 1) & 0xF;
                b = (x >> 3) | (x << 5);
                c = 6;
            }
            break;
            default:
                throw new ArgumentException("Illegal quint encoding");
        }
        int t = quint * c + b;
        t ^= a;
        t = (a & 0x80) | (t >> 2);
        return t;
    }

    private static int GetUnquantizedTritWeight(int trit, int bits, int range)
    {
        int a = (bits & 1) != 0 ? 0x7F : 0;
        int b = 0, c = 0;
        switch (range)
        {
            case 2:
                return new[] { 0, 32, 63 }[trit];
            case 5:
                c = 50; b = 0; break;
            case 11:
                c = 23; b = (bits >> 1) & 1; b |= (b << 2) | (b << 6); break;
            case 23:
                c = 11; b = (bits >> 1) & 0x3; b |= (b << 5); break;
            default:
                throw new ArgumentException("Illegal trit encoding");
        }
        int t = trit * c + b;
        t ^= a;
        t = (a & 0x20) | (t >> 2);
        return t;
    }

    private static int GetUnquantizedQuintWeight(int quint, int bits, int range)
    {
        int a = (bits & 1) != 0 ? 0x7F : 0;
        int b = 0, c = 0;
        switch (range)
        {
            case 4:
                return new[] { 0, 16, 32, 47, 63 }[quint];
            case 9:
                c = 28; b = 0; break;
            case 19:
                c = 13; b = (bits >> 1) & 0x1; b = (b << 1) | (b << 6); break;
            default:
                throw new ArgumentException("Illegal quint encoding");
        }
        int t = quint * c + b;
        t ^= a;
        t = (a & 0x20) | (t >> 2);
        return t;
    }

    // QuantizationMap and derived classes
    private class QuantizationMap
    {
        protected List<int> quantization_map_ = new List<int>();
        protected List<int> unquantization_map_ = new List<int>();

        public int Quantize(int x)
        {
            return x < quantization_map_.Count ? quantization_map_[x] : 0;
        }

        public int Unquantize(int x)
        {
            return x < unquantization_map_.Count ? unquantization_map_[x] : 0;
        }

        protected void GenerateQuantizationMap()
        {
            if (unquantization_map_.Count <= 1) return;
            quantization_map_.Clear();
            for (int i = 0; i < 256; ++i)
            {
                int bestIdx = 0;
                int bestScore = int.MaxValue;
                for (int idx = 0; idx < unquantization_map_.Count; ++idx)
                {
                    int diff = i - unquantization_map_[idx];
                    int score = diff * diff;
                    if (score < bestScore) { bestIdx = idx; bestScore = score; }
                }
                quantization_map_.Add(bestIdx);
            }
        }
    }

    private class TritQuantizationMap : QuantizationMap
    {
        public TritQuantizationMap(int range, Func<int,int,int,int> unquantFunc)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual((range + 1) % 3, 0);
            
            int num_bits_pow_2 = (range + 1) / 3;
            int num_bits = num_bits_pow_2 == 0 ? 0 : Log2Floor(num_bits_pow_2);

            for (int trit = 0; trit < 3; ++trit)
                for (int bits = 0; bits < (1 << num_bits); ++bits)
                    unquantization_map_.Add(unquantFunc(trit, bits, range));

            GenerateQuantizationMap();
        }
    }

    private class QuintQuantizationMap : QuantizationMap
    {
        public QuintQuantizationMap(int range, Func<int,int,int,int> unquantFunc)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual((range + 1) % 5, 0);

            int num_bits_pow_2 = (range + 1) / 5;
            int num_bits = num_bits_pow_2 == 0 ? 0 : Log2Floor(num_bits_pow_2);

            for (int quint = 0; quint < 5; ++quint)
                for (int bits = 0; bits < (1 << num_bits); ++bits)
                    unquantization_map_.Add(unquantFunc(quint, bits, range));

            GenerateQuantizationMap();
        }
    }

    private class BitQuantizationMap : QuantizationMap
    {
        // TotalUnquantizedBits is 8 for endpoint values and 6 for weights
        public BitQuantizationMap(int range, int totalUnquantizedBits)
        {
            // ensure range+1 is power of two
            ArgumentOutOfRangeException.ThrowIfNotEqual(CountOnes(range + 1), 1);
            
            int num_bits = Log2Floor(range + 1);

            for (int bits = 0; bits <= range; bits++)
            {
                int unquantized = bits;
                int num_unquantized_bits = num_bits;
                while (num_unquantized_bits < totalUnquantizedBits)
                {
                    int num_dst_bits_to_shift_up = Math.Min(num_bits, totalUnquantizedBits - num_unquantized_bits);
                    int num_src_bits_to_shift_down = num_bits - num_dst_bits_to_shift_up;
                    unquantized <<= num_dst_bits_to_shift_up;
                    unquantized |= bits >> num_src_bits_to_shift_down;
                    num_unquantized_bits += num_dst_bits_to_shift_up;
                }
                if (num_unquantized_bits != totalUnquantizedBits) throw new InvalidOperationException();
                unquantization_map_.Add(unquantized);

                if (bits > 0)
                {
                    int prev_unquant = unquantization_map_[bits - 1];
                    while (quantization_map_.Count <= (prev_unquant + unquantized) / 2)
                        quantization_map_.Add(bits - 1);
                }
                while (quantization_map_.Count <= unquantized) quantization_map_.Add(bits);
            }

            // expected size
            if (quantization_map_.Count != (1 << totalUnquantizedBits))
            {
                // fine, but try to keep consistent
            }
        }
    }

    private static int Log2Floor(int v)
    {
        int r = 0;
        while ((1 << (r + 1)) <= v) r++;
        return r;
    }

    private static int CountOnes(int v)
    {
        int c = 0;
        while (v != 0) { c += v & 1; v >>= 1; }
        return c;
    }

    // Caches for quantization maps
    private static readonly SortedDictionary<int, QuantizationMap> endpointMaps = InitEndpointMaps();
    private static readonly SortedDictionary<int, QuantizationMap> weightMaps = InitWeightMaps();

    private static SortedDictionary<int, QuantizationMap> InitEndpointMaps()
    {
        var d = new SortedDictionary<int, QuantizationMap>
        {
            { 5, new TritQuantizationMap(5, GetUnquantizedTritValue) },
            { 7, new BitQuantizationMap(7, 8) },
            { 9, new QuintQuantizationMap(9, GetUnquantizedQuintValue) },
            { 11, new TritQuantizationMap(11, GetUnquantizedTritValue) },
            { 15, new BitQuantizationMap(15, 8) },
            { 19, new QuintQuantizationMap(19, GetUnquantizedQuintValue) },
            { 23, new TritQuantizationMap(23, GetUnquantizedTritValue) },
            { 31, new BitQuantizationMap(31, 8) },
            { 39, new QuintQuantizationMap(39, GetUnquantizedQuintValue) },
            { 47, new TritQuantizationMap(47, GetUnquantizedTritValue) },
            { 63, new BitQuantizationMap(63, 8) },
            { 79, new QuintQuantizationMap(79, GetUnquantizedQuintValue) },
            { 95, new TritQuantizationMap(95, GetUnquantizedTritValue) },
            { 127, new BitQuantizationMap(127, 8) },
            { 159, new QuintQuantizationMap(159, GetUnquantizedQuintValue) },
            { 191, new TritQuantizationMap(191, GetUnquantizedTritValue) },
            { 255, new BitQuantizationMap(255, 8) }
        };
        return d;
    }

    private static SortedDictionary<int, QuantizationMap> InitWeightMaps()
    {
        var d = new SortedDictionary<int, QuantizationMap>
        {
            { 1, new BitQuantizationMap(1, 6) },
            { 2, new TritQuantizationMap(2, GetUnquantizedTritWeight) },
            { 3, new BitQuantizationMap(3, 6) },
            { 4, new QuintQuantizationMap(4, GetUnquantizedQuintWeight) },
            { 5, new TritQuantizationMap(5, GetUnquantizedTritWeight) },
            { 7, new BitQuantizationMap(7, 6) },
            { 9, new QuintQuantizationMap(9, GetUnquantizedQuintWeight) },
            { 11, new TritQuantizationMap(11, GetUnquantizedTritWeight) },
            { 15, new BitQuantizationMap(15, 6) },
            { 19, new QuintQuantizationMap(19, GetUnquantizedQuintWeight) },
            { 23, new TritQuantizationMap(23, GetUnquantizedTritWeight) },
            { 31, new BitQuantizationMap(31, 6) }
        };
        return d;
    }

    private static QuantizationMap? GetQuantMapForValueRange(int r)
    {
        if (r < 0 || r >= 256) return null;
        // find greatest key <= r
        foreach (var kv in endpointMaps.Reverse())
        {
            if (kv.Key <= r) return kv.Value;
        }
        return null;
    }

    private static QuantizationMap? GetQuantMapForWeightRange(int r)
    {
        if (r < 0 || r >= 32) return null;
        foreach (var kv in weightMaps.Reverse())
        {
            if (kv.Key <= r) return kv.Value;
        }
        return null;
    }

    public static int QuantizeCEValueToRange(int value, int range_max_value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(range_max_value, kEndpointRangeMinValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range_max_value, byte.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, byte.MaxValue);
        
        var map = GetQuantMapForValueRange(range_max_value);
        return map != null ? map.Quantize(value) : 0;
    }

    public static int UnquantizeCEValueFromRange(int value, int range_max_value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(range_max_value, kEndpointRangeMinValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range_max_value, byte.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, range_max_value);
        
        var map = GetQuantMapForValueRange(range_max_value);
        return map != null ? map.Unquantize(value) : 0;
    }

    public static int QuantizeWeightToRange(int weight, int range_max_value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(range_max_value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range_max_value, kWeightRangeMaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 64);
        
        if (weight > 33) weight -= 1;
        var map = GetQuantMapForWeightRange(range_max_value);
        return map != null ? map.Quantize(weight) : 0;
    }

    public static int UnquantizeWeightFromRange(int weight, int range_max_value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(range_max_value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range_max_value, kWeightRangeMaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, range_max_value);
        
        var map = GetQuantMapForWeightRange(range_max_value);
        int dq = map != null ? map.Unquantize(weight) : 0;
        if (dq > 32) dq += 1;
        return dq;
    }
}
