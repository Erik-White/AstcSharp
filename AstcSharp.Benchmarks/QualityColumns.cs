using System.Globalization;
using AstcSharp.Core;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace AstcSharp.Benchmarks;

/// <summary>
/// One footprint's encode quality: AstcSharp's and the reference encoder's log-PSNR (dB) on the
/// fixture, and AstcSharp's dB advantage. Shared by the LDR and HDR quality benchmarks.
/// </summary>
public readonly record struct QualityResult(double OurPsnr, double ReferencePsnr)
{
    public double Gap => OurPsnr - ReferencePsnr;
}

/// <summary>
/// A custom summary column that reports encode quality (log-PSNR, dB) alongside BenchmarkDotNet's
/// timing columns, so one report shows both how fast and how good each footprint's encode is versus
/// the reference encoder. Quality is deterministic, so the benchmark measures it once per footprint and
/// this column reads that cache via <paramref name="resolve"/> — it is never sampled like a timing.
/// </summary>
internal sealed class QualityColumn : IColumn
{
    private readonly Func<FootprintType, QualityResult> resolve;
    private readonly Func<QualityResult, double> select;

    public QualityColumn(string columnName, string legend, Func<FootprintType, QualityResult> resolve, Func<QualityResult, double> select)
    {
        this.ColumnName = columnName;
        this.Legend = legend;
        this.resolve = resolve;
        this.select = select;
    }

    public string Id => "Quality." + this.ColumnName;

    public string ColumnName { get; }

    public string Legend { get; }

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public bool AlwaysShow => true;

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => this.GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        if (benchmarkCase.Parameters["FootprintType"] is not FootprintType footprintType)
        {
            return "?";
        }

        return select(resolve(footprintType)).ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The three quality columns (Our dB, Ref dB, dB vs Ref) bound to a benchmark's cached
    /// per-footprint quality lookup.
    /// </summary>
    public static IColumn[] For(Func<FootprintType, QualityResult> resolve) =>
    [
        new QualityColumn("Our dB", "AstcSharp encode log-PSNR (dB) against the source", resolve, r => r.OurPsnr),
        new QualityColumn("Ref dB", "Reference encoder log-PSNR (dB) against the source", resolve, r => r.ReferencePsnr),
        new QualityColumn("dB vs Ref", "AstcSharp minus reference log-PSNR (dB); positive means AstcSharp reconstructs better", resolve, r => r.Gap),
    ];
}
