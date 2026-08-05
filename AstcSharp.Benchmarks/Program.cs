using BenchmarkDotNet.Running;

namespace AstcSharp.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var switcher = new BenchmarkSwitcher(
            [
                typeof(DecodingBenchmark),
                typeof(AstcFullImageDecodeBenchmark),
                typeof(EncodingBenchmark),
                typeof(HdrEncodingBenchmark),
                typeof(AstcFullImageEncodeBenchmark),
                typeof(CodecComparisonBenchmark),
                typeof(ReferenceDecoderBenchmark),
                typeof(LdrQualityBenchmark),
                typeof(HdrQualityBenchmark),
            ]);
            switcher.Run(args);
        }
    }
}
