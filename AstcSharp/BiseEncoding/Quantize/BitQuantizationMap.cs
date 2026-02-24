namespace AstcSharp.BiseEncoding.Quantize;

internal class BitQuantizationMap : QuantizationMap
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
            unquantization_map_builder.Add(unquantized);

            if (bits > 0)
            {
                int prev_unquant = unquantization_map_builder[bits - 1];
                while (quantization_map_builder.Count <= (prev_unquant + unquantized) / 2)
                    quantization_map_builder.Add(bits - 1);
            }
            while (quantization_map_builder.Count <= unquantized) quantization_map_builder.Add(bits);
        }

        Freeze();
    }

    private static int CountOnes(int v)
    {
        int c = 0;
        while (v != 0) { c += v & 1; v >>= 1; }
        return c;
    }
}
