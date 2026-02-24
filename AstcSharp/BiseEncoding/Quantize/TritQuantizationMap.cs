namespace AstcSharp.BiseEncoding.Quantize;

internal class TritQuantizationMap : QuantizationMap
{
    public TritQuantizationMap(int range, Func<int, int, int, int> unquantFunc)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((range + 1) % 3, 0);

        int num_bits_pow_2 = (range + 1) / 3;
        int num_bits = num_bits_pow_2 == 0 ? 0 : Log2Floor(num_bits_pow_2);

        for (int trit = 0; trit < 3; ++trit)
            for (int bits = 0; bits < (1 << num_bits); ++bits)
                unquantization_map_builder.Add(unquantFunc(trit, bits, range));

        GenerateQuantizationMap();
        Freeze();
    }

    internal static int GetUnquantizedValue(int trit, int bits, int range)
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

    internal static int GetUnquantizedWeight(int trit, int bits, int range)
    {
        if (range == 2)
            return trit switch
            {
                0 => 0,
                1 => 32,
                _ => 63
            };

        int a = (bits & 1) != 0 ? 0x7F : 0;
        var (b, c) = range switch
        {
            5 => (0, 50),
            11 => ((bits >> 1) & 1) is var x
                ? (x | (x << 2) | (x << 6), 23)
                : default,
            23 => ((bits >> 1) & 0x3) is var x
                ? (x | (x << 5), 11)
                : default,
            _ => throw new ArgumentException("Illegal trit encoding")
        };
        int t = trit * c + b;
        t ^= a;
        return (a & 0x20) | (t >> 2);
    }
}
