using System.Collections.Concurrent;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Research.Evaluation;

/// <summary>Calibration settings (m3-protocol §7). The defaults are the pre-registered values.</summary>
public sealed record CalibrationOptions(
    int NullStrategies = 1000,
    int NullUniverses = 20,
    int TruePositiveUniverses = 50,
    int StrategySeed = 1,
    int NullUniverseSeedBase = 10_000,
    int TruePositiveSeedBase = 20_000,
    int CalibrationTrials = 100,
    double TargetFalsePositive = 0.05,
    double TargetTruePositive = 0.80,
    double TrendKappa = 0.063,
    int TrendMeanBars = 300,
    double TrendUpShare = 0.5,
    int MaxParallelism = 4,
    int ProtocolVersion = 1);

public sealed record GridOutcome(GateThresholds Thresholds, double FalsePositive, double TruePositive, bool Admissible);

public sealed record KappaSearch(double Kappa, double MeanT, double TargetT);

public sealed record CalibrationReport(
    CalibrationOptions Options,
    string RiskVariant,
    KappaSearch? Kappa,
    string TruePositiveStrategyHash,
    double NullSharpeVariance,
    IReadOnlyList<CandidateStatistics> Null,
    IReadOnlyList<CandidateStatistics> TruePositive,
    double TruePositiveMeanMetric,
    IReadOnlyList<GridOutcome> Grid,
    GateThresholds? Selected);

/// <summary>
/// Synthetic calibration of the research gates (m3-protocol §7): random strategies on null universes give the
/// false-positive rate, a trend strategy on planted-edge universes gives the true-positive rate; the candidate
/// grid is then filtered (FP ≤ target, TP ≥ target) and the selection rule picks the thresholds. Calibration runs
/// are synthetic and are not trials.
/// </summary>
public sealed class CalibrationRunner(CostModel costs, RiskSettings risk, int bootstrapReplicates = 10_000)
{
    public static readonly int[] MinTradesGrid = [20, 30, 50, 100];
    public static readonly double[] WalkForwardGrid = [0.50, 0.75, 1.00];
    public static readonly double[] NeighbourhoodGrid = [0.50, 0.60, 0.70, 0.80];
    public static readonly double[] DeflatedSharpeGrid = [0.90, 0.95, 0.975];

    /// <summary>Fixed trend strategy for the planted-edge universes (EMA 20/60 cross, 3 × ATR(14) stop).</summary>
    public const string TruePositiveJson = """
        {
          "dsl": "1", "name": "tp-trend", "interval": "4h",
          "indicators": [
            { "id": "fast", "type": "ema", "params": { "period": 20 } },
            { "id": "slow", "type": "ema", "params": { "period": 60 } },
            { "id": "atr", "type": "atr", "params": { "period": 14 } }
          ],
          "entry": { "op": "cross_above", "args": [ { "ref": "fast.value" }, { "ref": "slow.value" } ] },
          "exit":  { "op": "cross_below", "args": [ { "ref": "fast.value" }, { "ref": "slow.value" } ] },
          "stop":  { "type": "atr", "ref": "atr.value", "multiple": 3 }
        }
        """;

    /// <summary>Protocol v2 §3: t* = b(N) + z(0.90) + z(0.80), the smallest edge the most lenient DSR threshold catches with 80 % power.</summary>
    public static double TargetT(int trials) =>
        DeflatedSharpe.Benchmark(trials, 1.0) + Normal.InverseCdf(DeflatedSharpeGrid.Min()) + Normal.InverseCdf(0.80);

    public static IReadOnlyList<double> KappaGrid { get; } = Enumerable.Range(20, 61).Select(k => k / 1000.0).ToList();

    /// <summary>Protocol v2 §3: the κ whose mean tp-trend t-statistic over the true-positive universes is closest to t* (ties: smaller κ).</summary>
    public KappaSearch SearchKappa(CalibrationOptions options, Action<string>? progress = null)
    {
        var evaluator = new ResearchGateEvaluator(costs, risk, bootstrapReplicates);
        var strategy = DslParser.Parse(TruePositiveJson);
        var target = TargetT(options.CalibrationTrials);
        KappaSearch? best = null;
        foreach (var kappa in KappaGrid)
        {
            var t = new double[options.TruePositiveUniverses];
            RunByUniverse(options.TruePositiveUniverses, options.MaxParallelism,
                u => SyntheticMarkets.Trending(options.TruePositiveSeedBase + u, kappa, options.TrendMeanBars, options.TrendUpShare),
                (u, input) => t[u] = evaluator.TStatistic(new DslStrategy(strategy), input));
            var candidate = new KappaSearch(kappa, t.Average(), target);
            progress?.Invoke(FormattableString.Invariant($"kappa {kappa:0.000}: mean t {candidate.MeanT:0.00} (target {target:0.00})"));
            if (best is null || Math.Abs(candidate.MeanT - target) < Math.Abs(best.MeanT - target))
            {
                best = candidate;
            }
        }

        return best!;
    }

    public CalibrationReport Run(CalibrationOptions options, Action<string>? progress = null)
    {
        KappaSearch? kappa = null;
        if (options.ProtocolVersion >= 2)
        {
            kappa = SearchKappa(options, progress);
            options = options with { TrendKappa = kappa.Kappa };
        }

        var evaluator = new ResearchGateEvaluator(costs, risk, bootstrapReplicates);
        var generator = new RandomStrategies(options.StrategySeed);
        var strategies = Enumerable.Range(0, options.NullStrategies).Select(_ => generator.Next()).ToList();

        // Pass 1: null strategies, grouped by universe so each universe is generated once.
        var nullStats = new CandidateStatistics[strategies.Count];
        RunByUniverse(options.NullUniverses, options.MaxParallelism, u => SyntheticMarkets.Null(options.NullUniverseSeedBase + u), (u, input) =>
        {
            for (var i = u; i < strategies.Count; i += options.NullUniverses)
            {
                nullStats[i] = evaluator.Measure(new DslStrategy(strategies[i]), input);
            }

            progress?.Invoke($"null universe {u + 1}/{options.NullUniverses}");
        });

        var variance = Variance(nullStats.Select(s => s.PeriodSharpe).ToList());
        var trials = new TrialContext(options.CalibrationTrials, variance, EstimatorVariance: options.ProtocolVersion >= 2);
        for (var i = 0; i < nullStats.Length; i++)
        {
            nullStats[i] = nullStats[i].WithTrials(trials);
        }

        // Pass 2: parameter neighbourhood only where the other gates pass at the most lenient thresholds.
        RunByUniverse(options.NullUniverses, options.MaxParallelism, u => SyntheticMarkets.Null(options.NullUniverseSeedBase + u), (u, input) =>
        {
            for (var i = u; i < strategies.Count; i += options.NullUniverses)
            {
                if (nullStats[i].NeedsNeighbourhood)
                {
                    nullStats[i] = nullStats[i] with { NeighbourhoodShare = evaluator.NeighbourhoodShare(strategies[i], input) };
                }
            }
        }, skip: u => !Enumerable.Range(0, strategies.Count).Where(i => i % options.NullUniverses == u).Any(i => nullStats[i].NeedsNeighbourhood));

        // True positives: the fixed trend strategy on planted-edge universes.
        var tpDefinition = DslParser.Parse(TruePositiveJson);
        var tpStats = new CandidateStatistics[options.TruePositiveUniverses];
        RunByUniverse(options.TruePositiveUniverses, options.MaxParallelism,
            u => SyntheticMarkets.Trending(options.TruePositiveSeedBase + u, options.TrendKappa, options.TrendMeanBars, options.TrendUpShare), (u, input) =>
            {
                var s = evaluator.Measure(new DslStrategy(tpDefinition), input).WithTrials(trials);
                tpStats[u] = s.NeedsNeighbourhood ? s with { NeighbourhoodShare = evaluator.NeighbourhoodShare(tpDefinition, input) } : s;
                progress?.Invoke($"true-positive universe {u + 1}/{options.TruePositiveUniverses}");
            });

        var grid = Grid(nullStats, tpStats, options);
        return new CalibrationReport(options, risk.Variant, kappa, DslCanonical.DefinitionHash(tpDefinition), variance, nullStats, tpStats,
            tpStats.Average(s => s.PrimaryMetric), grid, Select(grid));
    }

    public static IReadOnlyList<GridOutcome> Grid(IReadOnlyList<CandidateStatistics> nulls, IReadOnlyList<CandidateStatistics> positives, CalibrationOptions options)
    {
        var outcomes = new List<GridOutcome>();
        foreach (var n in MinTradesGrid)
        foreach (var wf in WalkForwardGrid)
        foreach (var nb in NeighbourhoodGrid)
        foreach (var dsr in DeflatedSharpeGrid)
        {
            var t = new GateThresholds(n, wf, nb, dsr);
            var fp = (double)nulls.Count(s => s.PassesAll(t)) / nulls.Count;
            var tp = (double)positives.Count(s => s.PassesAll(t)) / positives.Count;
            outcomes.Add(new GridOutcome(t, fp, tp, fp <= options.TargetFalsePositive && tp >= options.TargetTruePositive));
        }

        return outcomes;
    }

    /// <summary>Highest TP among admissible thresholds; ties go to the stricter (τ_dsr, τ_wf, τ_nb, N_min, larger first).</summary>
    public static GateThresholds? Select(IReadOnlyList<GridOutcome> grid) =>
        grid.Where(g => g.Admissible)
            .OrderByDescending(g => g.TruePositive)
            .ThenByDescending(g => g.Thresholds.DeflatedSharpe)
            .ThenByDescending(g => g.Thresholds.WalkForward)
            .ThenByDescending(g => g.Thresholds.Neighbourhood)
            .ThenByDescending(g => g.Thresholds.MinTrades)
            .Select(g => g.Thresholds)
            .FirstOrDefault();

    private static void RunByUniverse(int universes, int parallelism, Func<int, BacktestInput> build, Action<int, BacktestInput> work, Func<int, bool>? skip = null)
    {
        var errors = new ConcurrentQueue<Exception>();
        Parallel.For(0, universes, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, u =>
        {
            if (skip?.Invoke(u) == true)
            {
                return;
            }

            try
            {
                work(u, build(u));
            }
            catch (Exception e)
            {
                errors.Enqueue(e);
            }
        });

        if (!errors.IsEmpty)
        {
            throw new AggregateException(errors);
        }
    }

    private static double Variance(IReadOnlyList<double> x)
    {
        var mean = x.Average();
        return x.Sum(v => (v - mean) * (v - mean)) / (x.Count - 1);
    }
}
