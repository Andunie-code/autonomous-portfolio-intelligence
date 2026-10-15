using System.Globalization;
using System.Text;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.HoldoutEvaluator;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Window data provider. M3 has only the synthetic implementation; the archive importer arrives in M5.</summary>
public interface IWindowSource
{
    string Kind { get; }

    IReadOnlyDictionary<string, IReadOnlyList<Candle>> Load(IReadOnlyList<string> symbols, DateTimeOffset start, DateTimeOffset end);
}

/// <summary>
/// Seeded synthetic 4h candles (no network, no real data). A per-symbol drift can be planted so tests can produce
/// every outcome; the same seed always gives the same candles.
/// </summary>
public sealed class SyntheticWindowSource(int seed, double drift = 0, double sigma = 0.012, IReadOnlySet<string>? missing = null) : IWindowSource
{
    public string Kind => "synthetic";

    public IReadOnlyDictionary<string, IReadOnlyList<Candle>> Load(IReadOnlyList<string> symbols, DateTimeOffset start, DateTimeOffset end)
    {
        var result = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            var random = new Random(unchecked(seed * 31 + StableHash(symbol)));
            var list = new List<Candle>();
            var close = 100.0;
            for (var t = start; t < end; t = t.AddHours(4))
            {
                var open = close;
                close = open * Math.Exp(drift + sigma * Gaussian(random));
                var high = Math.Max(open, close) * Math.Exp(Math.Abs(Gaussian(random)) * sigma * 0.5);
                var low = Math.Min(open, close) * Math.Exp(-Math.Abs(Gaussian(random)) * sigma * 0.5);
                if (missing?.Contains($"{symbol}@{t:O}") == true)
                {
                    continue;
                }

                list.Add(new Candle(symbol, CandleInterval.FourHours, t, P(open), P(high), P(low), P(close), 1000, 1_000_000, 100));
            }

            result[symbol] = list;
        }

        return result;
    }

    private static decimal P(double value) => Math.Round((decimal)value, 8);

    private static int StableHash(string s) => s.Aggregate(17, (h, c) => unchecked(h * 31 + c));

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());
}

public sealed record MemberMetrics(int MemberIndex, string Strategy, string ResultHash, double PrimaryMetric, double MaxDrawdown, int Trades, double PValue, BootstrapDistribution Distribution);

/// <summary>Sorted bootstrap distribution of the primary metric; bounds are taken at the corrected level later.</summary>
public sealed record BootstrapDistribution(double[] Sorted);

public sealed record MemberDecision(int MemberIndex, string Status, string? Detail, double PrimaryMetric, double Lower, double Upper, double MaxDrawdown, int Trades, double Delta, double Alpha)
{
    /// <summary>Revealed summary (m3-protocol §9): percentages to one decimal, trade count, status. No raw data.</summary>
    public IReadOnlyDictionary<string, object> RoundedSummary() => new SortedDictionary<string, object>(StringComparer.Ordinal)
    {
        ["status"] = Status,
        ["primaryMetricPct"] = Math.Round(PrimaryMetric * 100, 1),
        ["maxDrawdownPct"] = Math.Round(MaxDrawdown * 100, 1),
        ["trades"] = Trades,
    };
}

/// <summary>Data quality of a window (m1-data-policy §6 applied to the window): every expected candle present per pair.</summary>
public static class WindowQuality
{
    public const double MinCoverage = 0.99;

    public static (bool Accepted, double WorstCoverage) Check(IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles, DateTimeOffset start, DateTimeOffset end)
    {
        var expected = (int)((end - start).TotalHours / 4);
        var worst = candles.Values.Select(c => (double)c.Count(x => x.OpenTime >= start && x.OpenTime < end) / expected).DefaultIfEmpty(0).Min();
        return (worst >= MinCoverage, worst);
    }

    public static string Digest(IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles)
    {
        var s = new StringBuilder();
        foreach (var (symbol, list) in candles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var c in list)
            {
                s.Append(string.Create(CultureInfo.InvariantCulture, $"{symbol},{c.OpenTime:O},{c.Open},{c.High},{c.Low},{c.Close},{c.QuoteVolume}\n"));
            }
        }

        return DslCanonical.Sha256(s.ToString());
    }
}

/// <summary>
/// Prospective evaluation of one cohort (architecture §6.5, m3-protocol §4): the kernel engine runs each member on
/// the window [A_c, A_c + L_c) with warm-up data before A_c; the decision order is applied with Bonferroni within
/// the family (p × attempt) and Holm within the cohort. Pure and deterministic: same inputs, same result hash.
/// </summary>
public static class ProspectiveEvaluation
{
    public static readonly TimeSpan WarmUp = TimeSpan.FromDays(90);

    public static IReadOnlyList<MemberMetrics> Measure(CohortManifest manifest, IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles, DateTimeOffset aC, DateTimeOffset windowEnd)
    {
        var symbols = manifest.Pairs.Select(p => p.Symbol).ToList();
        var traded = symbols.ToDictionary(s => s, s => candles[s], StringComparer.Ordinal);
        candles.TryGetValue("BTCUSDT", out var reference);
        var input = new BacktestInput(traded, symbols.Select((s, i) => (s, i + 1)).ToDictionary(x => x.s, x => x.Item2), aC, windowEnd, manifest.InitialCapital, reference);
        var result = new List<MemberMetrics>();
        for (var i = 0; i < manifest.Strategies.Count; i++)
        {
            var run = new BacktestEngine(manifest.Strategies[i].Strategy, CostModel.V1, manifest.Risk).Run(input);
            var returns = ReturnStatistics.LogReturns(run.InitialCapital, run.Equity);
            var metric = ReturnStatistics.AnnualisedMean(returns);
            var distribution = StationaryBootstrap.Distribution(returns, manifest.Protocol.BootstrapReplicates, manifest.Protocol.BootstrapSeed + i, ReturnStatistics.PeriodsPerYear);
            var bounds = StationaryBootstrap.Bounds(metric, distribution, 0.05);
            result.Add(new MemberMetrics(i + 1, manifest.Strategies[i].Strategy.Name, run.Hash(), metric,
                ReturnStatistics.MaxDrawdown(run.InitialCapital, run.Equity), run.Trades.Count, bounds.PValue, new BootstrapDistribution(distribution)));
        }

        return result;
    }

    /// <summary>The decision order of architecture §6.5 for every member (first matching rule wins).</summary>
    public static IReadOnlyList<MemberDecision> Decide(CohortManifest manifest, IReadOnlyList<MemberMetrics> metrics, IReadOnlyList<int> attempts, bool dataAccepted)
    {
        var alpha = manifest.Protocol.Alpha ?? 0.05;
        var adjusted = metrics.Select((m, i) => Math.Min(1, m.PValue * attempts[i])).ToList();
        var levels = Holm.Levels(adjusted, alpha);
        var decisions = new List<MemberDecision>();
        for (var i = 0; i < metrics.Count; i++)
        {
            var m = metrics[i];
            var level = levels[i].Alpha / attempts[i];
            var bounds = StationaryBootstrap.Bounds(m.PrimaryMetric, m.Distribution.Sorted, level);
            var delta = manifest.Strategies[i].Delta;
            var (status, detail) = Rule(manifest, m, bounds, delta, levels[i].Eligible, dataAccepted);
            decisions.Add(new MemberDecision(m.MemberIndex, status, detail, m.PrimaryMetric, bounds.Lower, bounds.Upper, m.MaxDrawdown, m.Trades, delta, level));
        }

        return decisions;
    }

    private static (string Status, string? Detail) Rule(CohortManifest manifest, MemberMetrics m, BootstrapBounds bounds, double delta, bool eligible, bool dataAccepted)
    {
        if (!dataAccepted)
        {
            return ("Invalid", "data quality not accepted");
        }

        if (!manifest.ProtocolComplete)
        {
            return ("Inconclusive", "protocol incomplete");
        }

        if (m.MaxDrawdown > manifest.Protocol.MaxDrawdown)
        {
            return ("Failed", "maximum drawdown rule");
        }

        if (bounds.Upper < delta)
        {
            return ("Failed", "upper bound below delta");
        }

        if (manifest.Underpowered || m.Trades < manifest.PlannedTrades)
        {
            return ("Inconclusive", manifest.Underpowered ? "underpowered" : "fewer trades than planned");
        }

        var deltaMet = manifest.Protocol.DeltaMode == "b" ? bounds.Lower >= delta : m.PrimaryMetric >= delta;
        return eligible && bounds.Lower > 0 && deltaMet ? ("Validated", null) : ("Inconclusive", null);
    }

    public static string ResultHash(IReadOnlyList<MemberMetrics> metrics, IReadOnlyList<MemberDecision> decisions)
    {
        var s = new StringBuilder();
        for (var i = 0; i < metrics.Count; i++)
        {
            s.Append(string.Create(CultureInfo.InvariantCulture, $"{metrics[i].MemberIndex}|{metrics[i].ResultHash}|{decisions[i].Status}|{decisions[i].Detail}\n"));
        }

        return DslCanonical.Sha256(s.ToString());
    }
}

/// <summary>
/// Diagnostic evaluation on [C, A) (D10): eliminates only. Rule (proposed m3-protocol v1.1 addendum, pending user
/// approval before any real diagnostic evaluation): Rejected when the maximum drawdown exceeds the protocol limit or
/// the one-sided 95 % upper bound of the primary metric is below zero; NotRejected otherwise.
/// </summary>
public static class DiagnosticEvaluation
{
    public static (string Verdict, double Metric, double Upper, double MaxDrawdown, int Trades, string ResultHash) Evaluate(
        DslStrategy strategy, IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles, IReadOnlyList<string> symbols,
        DateTimeOffset start, DateTimeOffset end, double maxDrawdown, int seed)
    {
        var traded = symbols.ToDictionary(s => s, s => candles[s], StringComparer.Ordinal);
        candles.TryGetValue("BTCUSDT", out var reference);
        var input = new BacktestInput(traded, symbols.Select((s, i) => (s, i + 1)).ToDictionary(x => x.s, x => x.Item2), start, end, 10_000m, reference);
        var run = new BacktestEngine(strategy, CostModel.V1, RiskSettings.Policy).Run(input);
        var returns = ReturnStatistics.LogReturns(run.InitialCapital, run.Equity);
        var metric = ReturnStatistics.AnnualisedMean(returns);
        var bounds = StationaryBootstrap.Bounds(metric, StationaryBootstrap.Distribution(returns, 10_000, seed, ReturnStatistics.PeriodsPerYear), 0.05);
        var drawdown = ReturnStatistics.MaxDrawdown(run.InitialCapital, run.Equity);
        var verdict = drawdown > maxDrawdown || bounds.Upper < 0 ? "Rejected" : "NotRejected";
        return (verdict, metric, bounds.Upper, drawdown, run.Trades.Count, run.Hash());
    }
}
