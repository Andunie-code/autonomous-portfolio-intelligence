using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Research.Hypotheses;

/// <summary>One grid point's full research-window run (no-halt, m4-protocol §5).</summary>
public sealed record GridPointRun(string Label, BacktestResult Run);

public sealed record WalkForwardFold(int TestYear, string SelectedLabel, double SelectionMetric, double TestMetric);

public sealed record AnchoredWalkForward(IReadOnlyList<WalkForwardFold> Folds)
{
    public double Share => Folds.Count == 0 ? 0 : (double)Folds.Count(f => f.TestMetric > 0) / Folds.Count;
}

/// <summary>
/// Pre-registered selection over a hypothesis's grid (m4-protocol §5–§6):
/// <list type="bullet">
/// <item>candidate = the point with the highest M_R on the research window; ties go to the earlier point;</item>
/// <item>G3 for grid hypotheses = anchored expanding window: for each test year k, the point with the highest annualised
/// mean 4h log return over [U, k) is selected and its annualised mean in year k is the test; the selection never
/// looks at year k or later.</item>
/// </list>
/// Years are assigned by candle open time, as in <c>ResearchGateEvaluator.WalkForwardShare</c>.
/// </summary>
public static class GridSelection
{
    public static IReadOnlyList<int> TestYears { get; } = [2023, 2024, 2025];

    public static int SelectCandidate(IReadOnlyList<GridPointRun> points)
    {
        ArgumentOutOfRangeException.ThrowIfZero(points.Count);
        var best = 0;
        var bestMetric = ReturnStatistics.PrimaryMetric(points[0].Run);
        for (var i = 1; i < points.Count; i++)
        {
            var metric = ReturnStatistics.PrimaryMetric(points[i].Run);
            if (metric > bestMetric)
            {
                best = i;
                bestMetric = metric;
            }
        }

        return best;
    }

    public static AnchoredWalkForward Anchored(IReadOnlyList<GridPointRun> points, IReadOnlyList<int>? testYears = null)
    {
        var byYear = points.Select(p => YearlyReturns(p.Run)).ToList();
        var folds = new List<WalkForwardFold>();
        foreach (var year in testYears ?? TestYears)
        {
            var best = -1;
            var bestMetric = double.NegativeInfinity;
            for (var i = 0; i < points.Count; i++)
            {
                var before = byYear[i].Where(kv => kv.Key < year).SelectMany(kv => kv.Value).ToList();
                var metric = ReturnStatistics.AnnualisedMean(before);
                if (best < 0 || metric > bestMetric)
                {
                    best = i;
                    bestMetric = metric;
                }
            }

            var test = byYear[best].TryGetValue(year, out var returns) ? ReturnStatistics.AnnualisedMean(returns) : 0;
            folds.Add(new WalkForwardFold(year, points[best].Label, bestMetric, test));
        }

        return new AnchoredWalkForward(folds);
    }

    /// <summary>4h log returns by calendar year of the candle open time.</summary>
    public static IReadOnlyDictionary<int, List<double>> YearlyReturns(BacktestResult run)
    {
        var returns = ReturnStatistics.LogReturns(run.InitialCapital, run.Equity);
        var result = new SortedDictionary<int, List<double>>();
        for (var i = 0; i < returns.Length; i++)
        {
            var year = run.Equity[i].Time.AddTicks(-1).Year;
            if (!result.TryGetValue(year, out var list))
            {
                result[year] = list = [];
            }

            list.Add(returns[i]);
        }

        return result;
    }
}
