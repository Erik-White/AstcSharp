namespace AstcSharp.Benchmarks;

public static class BenchmarkTestDataLocator
{
    /// <summary>
    /// Locates a test data file by walking up from the benchmark directory until
    /// AstcSharp.Tests/TestData/Input is found.
    /// </summary>
    /// <param name="relativePath">Path under TestData/Input (e.g. "Astc/rgba-4x4.astc").</param>
    public static string FindTestData(string relativePath)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; ++i)
        {
            string candidate = Path.Combine(dir, "AstcSharp.Tests", "TestData", "Input", relativePath);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new FileNotFoundException($"Could not locate test data file: {relativePath}");
    }
}
