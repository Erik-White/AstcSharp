namespace AstcSharp.BiseEncoding.Quantize;

internal class QuantizationMap
{
    protected List<int> quantization_map_builder = [];
    protected List<int> unquantization_map_builder = [];

    // Flat arrays for O(1) lookup on the hot path (set by Freeze)
    private int[] quantization_map_ = [];
    private int[] unquantization_map_ = [];

    public int Quantize(int x)
        => (uint)x < (uint)quantization_map_.Length ? quantization_map_[x] : 0;

    public int Unquantize(int x)
        => (uint)x < (uint)unquantization_map_.Length ? unquantization_map_[x] : 0;

    /// <summary>
    /// Converts builder lists to flat arrays. Called after construction is complete.
    /// </summary>
    protected void Freeze()
    {
        unquantization_map_ = [.. unquantization_map_builder];
        quantization_map_ = [.. quantization_map_builder];
        unquantization_map_builder = [];
        quantization_map_builder = [];
    }

    protected void GenerateQuantizationMap()
    {
        if (unquantization_map_builder.Count <= 1) return;
        quantization_map_builder.Clear();
        for (int i = 0; i < 256; ++i)
        {
            int bestIdx = 0;
            int bestScore = int.MaxValue;
            for (int idx = 0; idx < unquantization_map_builder.Count; ++idx)
            {
                int diff = i - unquantization_map_builder[idx];
                int score = diff * diff;
                if (score < bestScore) { bestIdx = idx; bestScore = score; }
            }
            quantization_map_builder.Add(bestIdx);
        }
    }

    internal static int Log2Floor(int v)
    {
        int r = 0;
        while ((1 << (r + 1)) <= v) r++;
        return r;
    }
}
