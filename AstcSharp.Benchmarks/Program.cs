using BenchmarkDotNet.Running;

namespace AstcSharp.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var switcher = new BenchmarkSwitcher(new[]
            {
                typeof(AstcDecodingBenchmarks),
                typeof(AstcFullImageDecodeBenchmark)
            });
            switcher.Run(args);
        }
    }
}
