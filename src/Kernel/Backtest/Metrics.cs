namespace PortfolioIntelligence.Kernel.Backtest;

public sealed record PerformanceMetrics(
    decimal TotalReturn,
    double Cagr,
    decimal MaxDrawdown,
    double Sharpe,
    int Trades,
    double WinRate,
    double AverageExposure,
    double Turnover,
    decimal FeesPaid,
    decimal SlippagePaid);

/// <summary>Report metrics of docs/m2-protocol.md §11 over the equity series at each 4h close.</summary>
public static class Metrics
{
    public const double PeriodsPerYear = 6 * 365;

    public static PerformanceMetrics Compute(BacktestResult result, double years)
    {
        var equity = result.Equity.Select(p => p.Equity).ToList();
        var initial = result.InitialCapital;
        var final = equity.Count == 0 ? initial : equity[^1];

        decimal peak = initial, maxDrawdown = 0;
        foreach (var value in equity)
        {
            peak = Math.Max(peak, value);
            maxDrawdown = Math.Max(maxDrawdown, peak == 0 ? 0 : (peak - value) / peak);
        }

        var returns = new List<double>(equity.Count);
        var previous = (double)initial;
        foreach (var value in equity.Select(v => (double)v))
        {
            returns.Add(previous == 0 ? 0 : value / previous - 1);
            previous = value;
        }

        var mean = returns.Count == 0 ? 0 : returns.Average();
        var deviation = returns.Count < 2 ? 0 : Math.Sqrt(returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1));
        var closed = result.Trades.Where(t => t.Quantity > 0 && t.EntryTime != t.ExitTime).ToList();
        var traded = result.Trades.Sum(t => Math.Abs(t.Quantity) * (t.EntryPrice + (t.EntryTime == t.ExitTime ? 0 : t.ExitPrice)));
        var averageEquity = equity.Count == 0 ? (double)initial : equity.Average(v => (double)v);

        return new PerformanceMetrics(
            initial == 0 ? 0 : final / initial - 1,
            initial == 0 || final <= 0 ? -1 : Math.Pow((double)(final / initial), 1 / years) - 1,
            maxDrawdown,
            deviation == 0 ? 0 : mean / deviation * Math.Sqrt(PeriodsPerYear),
            closed.Count,
            closed.Count == 0 ? 0 : closed.Count(t => t.NetProfit > 0) / (double)closed.Count,
            result.Equity.Count == 0 ? 0 : result.Equity.Average(p => (double)p.Exposure),
            averageEquity == 0 ? 0 : (double)traded / averageEquity,
            result.Trades.Sum(t => t.Fees),
            result.Trades.Sum(t => t.SlippageCost));
    }
}
