using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Research.Evaluation;

/// <summary>Calibrated gate thresholds (m3-protocol §6–§7).</summary>
public sealed record GateThresholds(int MinTrades, double WalkForward, double Neighbourhood, double DeflatedSharpe)
{
    public const double MultiAssetPositiveShare = 0.30;
    public const double MultiAssetMaxContribution = 0.50;
    public const double ConfidenceAlpha = 0.05;

    /// <summary>The most lenient thresholds of the candidate grid; statistics stricter gates need are computed only past these.</summary>
    public static GateThresholds MostLenient { get; } = new(20, 0.50, 0.50, 0.90);

    public override string ToString() => $"N_min={MinTrades}, τ_wf={WalkForward}, τ_nb={Neighbourhood}, τ_dsr={DeflatedSharpe}";
}

/// <summary>Statistics of one candidate on the research window, before thresholds are applied.</summary>
public sealed record CandidateStatistics(
    string Strategy,
    double PrimaryMetric,
    int Trades,
    BootstrapBounds Bounds,
    double WalkForwardShare,
    double? NeighbourhoodShare,
    bool MultiAsset,
    double PeriodSharpe,
    int Observations,
    double Skewness,
    double Kurtosis,
    double DeflatedSharpeProbability,
    DateTimeOffset? HaltedAt = null)
{
    /// <summary>The same statistics with the deflated Sharpe probability for a given trial context.</summary>
    public CandidateStatistics WithTrials(TrialContext trials) => this with
    {
        DeflatedSharpeProbability = DeflatedSharpe.Probability(PeriodSharpe, Observations, Skewness, Kurtosis, trials.Trials,
            trials.EstimatorVariance ? 1.0 / Math.Max(1, Observations - 1) : trials.SharpeVariance),
    };

    /// <summary>t-statistic of the per-period Sharpe ratio: SR̂·√(T − 1).</summary>
    public double TStatistic => PeriodSharpe * Math.Sqrt(Math.Max(0, Observations - 1));

    /// <summary>Whether the candidate passes every gate except G4 at the most lenient thresholds (G4 is then worth computing).</summary>
    public bool NeedsNeighbourhood
    {
        get
        {
            var lenient = GateThresholds.MostLenient;
            return G1 && G2(lenient) && G3(lenient) && G5 && G6(lenient);
        }
    }

    public bool G1 => PrimaryMetric > 0;

    public bool G2(GateThresholds t) => Trades >= t.MinTrades && Bounds.Lower > 0;

    public bool G3(GateThresholds t) => WalkForwardShare >= t.WalkForward;

    public bool G4(GateThresholds t) => NeighbourhoodShare is { } share && share >= t.Neighbourhood;

    public bool G5 => MultiAsset;

    public bool G6(GateThresholds t) => DeflatedSharpeProbability >= t.DeflatedSharpe;

    public bool PassesAll(GateThresholds t) => G1 && G2(t) && G3(t) && G4(t) && G5 && G6(t);

    public IReadOnlyList<string> Failures(GateThresholds t)
    {
        var failures = new List<string>();
        if (!G1) failures.Add("G1 cost-adjusted return");
        if (!G2(t)) failures.Add("G2 trades and confidence");
        if (!G3(t)) failures.Add("G3 walk-forward");
        if (!G4(t)) failures.Add("G4 parameter neighbourhood");
        if (!G5) failures.Add("G5 multi-asset");
        if (!G6(t)) failures.Add("G6 deflated Sharpe");
        return failures;
    }
}

/// <summary>
/// Trial context for the deflated Sharpe ratio: global trial count and the variance of trial Sharpe ratios.
/// Protocol v2 §2: with <paramref name="EstimatorVariance"/> the variance is the null estimator variance 1/(T − 1)
/// of each candidate (same per-period scale as SR̂) and <paramref name="SharpeVariance"/> is ignored.
/// </summary>
public sealed record TrialContext(int Trials, double SharpeVariance, bool EstimatorVariance = false);

/// <summary>
/// Research gates G1–G6 of m3-protocol §6 on the research window, policy run, cost model v1. The parameter
/// neighbourhood (G4) is the expensive part and is evaluated only when the candidate passes the most lenient
/// thresholds of the other gates; otherwise it is reported as not evaluated (fails).
/// </summary>
public sealed class ResearchGateEvaluator(CostModel costs, RiskSettings risk, int bootstrapReplicates = 10_000, int bootstrapSeed = 20260101)
{
    public CandidateStatistics Evaluate(DslStrategy strategy, BacktestInput input, TrialContext trials, bool alwaysEvaluateNeighbourhood = false)
    {
        var statistics = Measure(strategy, input).WithTrials(trials);
        return alwaysEvaluateNeighbourhood || statistics.NeedsNeighbourhood
            ? statistics with { NeighbourhoodShare = NeighbourhoodShare(strategy.Source, input) }
            : statistics;
    }

    /// <summary>Per-period Sharpe t-statistic of one run, without the bootstrap (κ search of protocol v2 §3).</summary>
    public double TStatistic(DslStrategy strategy, BacktestInput input)
    {
        var result = new BacktestEngine(strategy, costs, risk).Run(input);
        var returns = ReturnStatistics.LogReturns(result.InitialCapital, result.Equity);
        return ReturnStatistics.Sharpe(returns) * Math.Sqrt(Math.Max(0, returns.Length - 1));
    }

    /// <summary>One run (policy or no-halt, per the evaluator's risk settings) and every statistic except G4 and the trial-dependent deflated Sharpe probability.</summary>
    public CandidateStatistics Measure(DslStrategy strategy, BacktestInput input)
    {
        var result = new BacktestEngine(strategy, costs, risk).Run(input);
        var returns = ReturnStatistics.LogReturns(result.InitialCapital, result.Equity);
        var metric = ReturnStatistics.AnnualisedMean(returns);
        var bounds = metric > 0
            ? StationaryBootstrap.Bounds(metric, StationaryBootstrap.Distribution(returns, bootstrapReplicates, bootstrapSeed, ReturnStatistics.PeriodsPerYear), GateThresholds.ConfidenceAlpha)
            : new BootstrapBounds(metric, double.NaN, double.NaN, double.NaN); // G1 fails anyway; the bootstrap is skipped
        var (skew, kurtosis) = ReturnStatistics.Moments(returns);
        return new CandidateStatistics(strategy.Name, metric, result.Trades.Count, bounds, WalkForwardShare(result), null,
            MultiAsset(result), ReturnStatistics.Sharpe(returns), returns.Length, skew, kurtosis, 0, result.HaltedAt);
    }

    /// <summary>Share of the neighbours (each numeric parameter ±10 %) with a positive primary metric.</summary>
    public double NeighbourhoodShare(DslDefinition definition, BacktestInput input)
    {
        var neighbours = ParameterNeighbourhood.Neighbours(definition);
        if (neighbours.Count == 0)
        {
            return 1;
        }

        var positive = neighbours.Count(n => ReturnStatistics.PrimaryMetric(new BacktestEngine(new DslStrategy(n), costs, risk).Run(input)) > 0);
        return (double)positive / neighbours.Count;
    }

    /// <summary>G3: share of calendar years (by candle open time) with a positive annualised mean log return.</summary>
    public static double WalkForwardShare(BacktestResult result)
    {
        var returns = ReturnStatistics.LogReturns(result.InitialCapital, result.Equity);
        var years = result.Equity
            .Select((p, i) => (Year: p.Time.AddTicks(-1).Year, Return: returns[i]))
            .GroupBy(x => x.Year)
            .Select(g => ReturnStatistics.AnnualisedMean(g.Select(x => x.Return).ToList()))
            .ToList();
        return years.Count == 0 ? 0 : (double)years.Count(m => m > 0) / years.Count;
    }

    /// <summary>G5: ≥ 30 % of traded pairs profitable and no pair above 50 % of the total positive profit.</summary>
    public static bool MultiAsset(BacktestResult result)
    {
        var byPair = result.Trades.GroupBy(t => t.Symbol).Select(g => g.Sum(t => t.NetProfit)).ToList();
        if (byPair.Count == 0)
        {
            return false;
        }

        var positive = byPair.Where(p => p > 0).ToList();
        var totalPositive = positive.Sum();
        return totalPositive > 0
            && (double)positive.Count / byPair.Count >= GateThresholds.MultiAssetPositiveShare
            && positive.Max() / totalPositive <= (decimal)GateThresholds.MultiAssetMaxContribution;
    }
}

/// <summary>Parameter neighbours of a definition (m3-protocol §6, G4).</summary>
public static class ParameterNeighbourhood
{
    /// <summary>
    /// Each numeric parameter moved by ±10 % one at a time: indicator parameters (integers by at least ±1, clamped
    /// to their range), non-zero constants, explicit multipliers other than 1 and the stop's pct or multiple.
    /// Neighbours that fail validation (e.g. MACD fast ≥ slow) or equal the original are skipped.
    /// </summary>
    public static IReadOnlyList<DslDefinition> Neighbours(DslDefinition definition)
    {
        var originalHash = DslCanonical.DefinitionHash(definition);
        var seen = new HashSet<string>(StringComparer.Ordinal) { originalHash };
        var result = new List<DslDefinition>();
        foreach (var candidate in Candidates(definition))
        {
            try
            {
                var valid = DslValidator.Validate(candidate);
                if (seen.Add(DslCanonical.DefinitionHash(valid)))
                {
                    result.Add(valid);
                }
            }
            catch (DslException)
            {
                // Out of range or inconsistent: not a neighbour.
            }
        }

        return result;
    }

    private static IEnumerable<DslDefinition> Candidates(DslDefinition d)
    {
        for (var i = 0; i < d.Indicators.Count; i++)
        {
            var indicator = d.Indicators[i];
            var spec = DslCatalog.Indicators[indicator.Type];
            foreach (var p in spec.Parameters)
            {
                foreach (var value in Moves(indicator.Params[p.Name], p.Integer))
                {
                    var clamped = Math.Clamp(value, p.Min, p.Max);
                    var parameters = new SortedDictionary<string, decimal>(indicator.Params.ToDictionary(), StringComparer.Ordinal) { [p.Name] = clamped };
                    var indicators = d.Indicators.ToList();
                    indicators[i] = indicator with { Params = parameters };
                    yield return d with { Indicators = indicators };
                }
            }
        }

        foreach (var (entry, exit) in ConditionMoves(d.Entry, d.Exit))
        {
            yield return d with { Entry = entry, Exit = exit };
        }

        if (d.Stop is { Pct: { } pct } stop)
        {
            foreach (var value in Moves(pct, false))
            {
                yield return d with { Stop = stop with { Pct = value } };
            }
        }

        if (d.Stop is { Multiple: { } multiple } atrStop)
        {
            foreach (var value in Moves(multiple, false))
            {
                yield return d with { Stop = atrStop with { Multiple = value } };
            }
        }
    }

    private static IEnumerable<decimal> Moves(decimal value, bool integer)
    {
        if (integer)
        {
            var step = Math.Max(1, Math.Round(Math.Abs(value) * 0.1m, MidpointRounding.AwayFromZero));
            yield return value - step;
            yield return value + step;
        }
        else
        {
            yield return Math.Round(value * 0.9m, 6);
            yield return Math.Round(value * 1.1m, 6);
        }
    }

    private static IEnumerable<(DslCondition Entry, DslCondition Exit)> ConditionMoves(DslCondition entry, DslCondition exit)
    {
        foreach (var moved in Moves(entry))
        {
            yield return (moved, exit);
        }

        foreach (var moved in Moves(exit))
        {
            yield return (entry, moved);
        }
    }

    /// <summary>All variants of the tree with exactly one constant or multiplier moved.</summary>
    private static IEnumerable<DslCondition> Moves(DslCondition condition)
    {
        for (var i = 0; i < condition.Args.Count; i++)
        {
            IEnumerable<DslNode> variants = condition.Args[i] switch
            {
                DslCondition child => Moves(child),
                DslConst { Value: not 0m } k => Moves(k.Value, false).Select(v => (DslNode)new DslConst(v)),
                DslRef { Mult: not 1m } r => Moves(r.Mult, false).Select(v => (DslNode)(r with { Mult = v })),
                DslPrice { Mult: not 1m } p => Moves(p.Mult, false).Select(v => (DslNode)(p with { Mult = v })),
                _ => [],
            };
            foreach (var variant in variants)
            {
                var args = condition.Args.ToList();
                args[i] = variant;
                yield return condition with { Args = args };
            }
        }
    }
}
