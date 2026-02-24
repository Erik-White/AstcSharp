namespace AstcSharp.BiseEncoding.Quantize;

internal sealed class QuintQuantizationMap : QuantizationMap
{
    public QuintQuantizationMap(int range, Func<int, int, int, int> unquantFunc)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((range + 1) % 5, 0);

        int bitsPowerOfTwo = (range + 1) / 5;
        int bitCount = bitsPowerOfTwo == 0 ? 0 : Log2Floor(bitsPowerOfTwo);

        for (int quint = 0; quint < 5; ++quint)
            for (int bits = 0; bits < (1 << bitCount); ++bits)
                _unquantizationMapBuilder.Add(unquantFunc(quint, bits, range));

        GenerateQuantizationMap();
        Freeze();
    }

    internal static int GetUnquantizedValue(int quint, int bits, int range)
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

    internal static int GetUnquantizedWeight(int quint, int bits, int range)
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
}
