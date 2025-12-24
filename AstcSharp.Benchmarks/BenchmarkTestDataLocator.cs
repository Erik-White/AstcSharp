using System;
using System.IO;

namespace AstcSharp.Benchmarks
{
    public static class BenchmarkTestDataLocator
    {
        /// <summary>
        /// Locates a test data file by searching up from the benchmark directory and into the known test data location.
        /// </summary>
        /// <param name="relativePath">Relative path from the test data root (e.g. "Input/atlas_small_4x4.astc")</param>
        /// <returns>Full path to the test data file, or throws if not found.</returns>
        public static string FindTestData(string relativePath)
        {
            // Walk up from the current directory, searching for AstcSharp.Tests/TestData
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; ++i)
            {
                var testDataDir = Path.Combine(dir, "AstcSharp.Tests", "TestData");
                var candidate = Path.Combine(testDataDir, relativePath);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
                dir = Path.GetFullPath(Path.Combine(dir, ".."));
            }
            throw new FileNotFoundException($"Could not locate test data file: {relativePath}");
        }
    }
}
