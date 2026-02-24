namespace AstcSharp.BiseEncoding.Quantize;

internal class QuantizationMap
{
    protected List<int> quantizationMapBuilder = [];
    protected List<int> unquantizationMapBuilder = [];

    // Flat arrays for O(1) lookup on the hot path (set by Freeze)
    private int[] quantizationMap = [];
    private int[] unquantizationMap = [];

    public int Quantize(int x)
        => (uint)x < (uint)quantizationMap.Length ? quantizationMap[x] : 0;

    public int Unquantize(int x)
        => (uint)x < (uint)unquantizationMap.Length ? unquantizationMap[x] : 0;

    /// <summary>
    /// Converts builder lists to flat arrays. Called after construction is complete.
    /// </summary>
    protected void Freeze()
    {
        unquantizationMap = [.. unquantizationMapBuilder];
        quantizationMap = [.. quantizationMapBuilder];
        unquantizationMapBuilder = [];
        quantizationMapBuilder = [];
    }

    protected void GenerateQuantizationMap()
    {
        if (unquantizationMapBuilder.Count <= 1) return;
        quantizationMapBuilder.Clear();
        for (int i = 0; i < 256; ++i)
        {
            int bestIndex = 0;
            int bestScore = int.MaxValue;
            for (int index = 0; index < unquantizationMapBuilder.Count; ++index)
            {
                int diff = i - unquantizationMapBuilder[index];
                int score = diff * diff;
                if (score < bestScore) { bestIndex = index; bestScore = score; }
            }
            quantizationMapBuilder.Add(bestIndex);
        }
    }

    internal static int Log2Floor(int value)
    {
        int result = 0;
        while ((1 << (result + 1)) <= value) result++;
        return result;
    }
}
