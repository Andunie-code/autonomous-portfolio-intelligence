namespace PortfolioIntelligence.Kernel.Statistics;

public sealed record RegimeEstimate(string Regime, int Days, double LongRunSigma, double TradesPerDay);

public sealed record DurationPlan(
    IReadOnlyList<RegimeEstimate> Regimes,
    double WorstSigma,
    double WorstTradesPerDay,
    IReadOnlyList<(int Months, double Power)> PowerCurve,
    int? PowerMonths,
    int? RequiredMonths,
    int CohortMonths,
    int PlannedTrades,
    bool Underpowered);

/// <summary>
/// Duration and power tool of m3-protocol §5. Inputs come only from the research window: the strategy's daily
/// log returns (no-halt planning run), its entry dates and BTC day-end closes (including history before the window).
/// Power is P(lower bound &gt; 0) when the true annual effect is δ, by normal approximation with the stationary
/// bootstrap long-run σ of the worst regime; the δ condition (a) is applied separately at decision time.
/// </summary>
public static class DurationPlanner
{
    public const int MinRegimeDays = 60;
    public const int MinMonths = 6;
    public const int MaxMonths = 12;
    public const int SearchMonths = 24;
    public const int MinPlannedTrades = 10;
    public const double DaysPerMonth = 365.25 / 12;

    public static DurationPlan Plan(
        IReadOnlyDictionary<DateOnly, double> dailyReturns,
        IReadOnlyCollection<DateOnly> entryDates,
        IReadOnlyDictionary<DateOnly, decimal> btcDayEndCloses,
        double delta,
        double planningAlpha,
        double targetPower,
        int seed,
        int replicates = 10_000)
    {
        var labels = Label(dailyReturns.Keys.Order().ToList(), btcDayEndCloses);
        var entriesByDay = entryDates.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        var regimes = new List<RegimeEstimate>();
        foreach (var group in labels.GroupBy(l => l.Value).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var days = group.Select(g => g.Key).Order().ToList();
            if (days.Count < MinRegimeDays)
            {
                continue;
            }

            var returns = days.Select(d => dailyReturns[d]).ToArray();
            var distribution = StationaryBootstrap.Distribution(returns, replicates, seed, 1.0);
            var variance = Variance(distribution);
            var trades = days.Sum(d => entriesByDay.GetValueOrDefault(d));
            regimes.Add(new RegimeEstimate(group.Key, days.Count, Math.Sqrt(variance * days.Count), (double)trades / days.Count));
        }

        if (regimes.Count == 0)
        {
            return new DurationPlan(regimes, double.NaN, 0, [], null, null, MaxMonths, MinPlannedTrades, Underpowered: true);
        }

        var worstSigma = regimes.Max(r => r.LongRunSigma);
        var worstFrequency = regimes.Min(r => r.TradesPerDay);
        var z = Normal.InverseCdf(1 - planningAlpha);
        var curve = new List<(int, double)>();
        int? powerMonths = null;
        for (var months = MinMonths; months <= SearchMonths; months++)
        {
            var n = Math.Round(months * DaysPerMonth);
            var power = worstSigma > 0 ? Normal.Cdf(delta / 365 * Math.Sqrt(n) / worstSigma - z) : 1.0;
            curve.Add((months, power));
            powerMonths ??= power >= targetPower ? months : null;
        }

        int? tradeMonths = worstFrequency > 0 ? (int)Math.Ceiling(MinPlannedTrades / worstFrequency / DaysPerMonth) : null;
        int? required = powerMonths is { } p && tradeMonths is { } t ? Math.Max(p, t) : null;
        var underpowered = required is null or > MaxMonths;
        var cohortMonths = underpowered ? MaxMonths : Math.Max(required!.Value, MinMonths);
        var planned = Math.Max(MinPlannedTrades, (int)Math.Ceiling(0.5 * worstFrequency * cohortMonths * DaysPerMonth));
        return new DurationPlan(regimes, worstSigma, worstFrequency, curve, powerMonths, required, cohortMonths, planned, underpowered);
    }

    /// <summary>
    /// Regime of each day: sign of BTC's 90-day log return (up when ≥ 0) × tercile of BTC's 30-day realised
    /// volatility over the labelled days. Days without the needed BTC history are not labelled.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, string> Label(IReadOnlyList<DateOnly> days, IReadOnlyDictionary<DateOnly, decimal> btc)
    {
        var raw = new List<(DateOnly Day, bool Up, double Vol)>();
        foreach (var day in days)
        {
            if (!btc.TryGetValue(day, out var close) || !btc.TryGetValue(day.AddDays(-90), out var past) || close <= 0 || past <= 0)
            {
                continue;
            }

            var closes = new List<double>(31);
            for (var d = day.AddDays(-30); d <= day; d = d.AddDays(1))
            {
                if (!btc.TryGetValue(d, out var c) || c <= 0)
                {
                    break;
                }

                closes.Add((double)c);
            }

            if (closes.Count != 31)
            {
                continue;
            }

            var logs = closes.Zip(closes.Skip(1), (a, b) => Math.Log(b / a)).ToList();
            raw.Add((day, Math.Log((double)close / (double)past) >= 0, Math.Sqrt(SampleVariance(logs))));
        }

        var ranked = raw.OrderBy(r => r.Vol).ThenBy(r => r.Day).ToList();
        var result = new Dictionary<DateOnly, string>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var tercile = Math.Min(2, 3 * i / ranked.Count);
            result[ranked[i].Day] = $"{(ranked[i].Up ? "up" : "down")}-vol{tercile + 1}";
        }

        return result;
    }

    private static double Variance(double[] x)
    {
        var mean = ReturnStatistics.Mean(x);
        var sum = 0.0;
        foreach (var v in x)
        {
            sum += (v - mean) * (v - mean);
        }

        return sum / x.Length;
    }

    private static double SampleVariance(IReadOnlyList<double> x)
    {
        var mean = ReturnStatistics.Mean(x);
        var sum = 0.0;
        foreach (var v in x)
        {
            sum += (v - mean) * (v - mean);
        }

        return sum / (x.Count - 1);
    }
}
