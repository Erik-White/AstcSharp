using BenchmarkDotNet.Running;

namespace AstcSharp.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var switcher = new BenchmarkSwitcher(
            [
                typeof(AstcDecodingBenchmarks),
                typeof(AstcFullImageDecodeBenchmark),
                typeof(ArmReferenceComparisonBenchmark)
            ]);
            switcher.Run(args);
        }
    }
}
