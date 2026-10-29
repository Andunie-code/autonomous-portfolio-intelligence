using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Allocator;

/// <summary>Day-end values of one paper run: the real (T11) run and its shadow engine (m8-protocol §6.2).</summary>
public sealed record DailyMark(DateOnly Day, decimal Real, decimal Shadow);

/// <summary>Window counts of the operational side of the evaluation (visible to Claude Code, m8-protocol §7).</summary>
public sealed record ShadowCounts(int Calls, int ValidProposals, int SafetyEvents);

public sealed record NonInferiorityResult(
    QualificationVerdict Verdict,
    string Reason,
    int Days,
    double MeanDifference,
    double LowerBound,
    IReadOnlyList<(double Real, double Shadow)> Drawdowns);

/// <summary>
/// The pre-registered decision rule of the AllocationProposal qualification (m8-protocol §6.2–§6.3; A-1…A-5, U
/// 2026-10-29). d_t is the daily log return of the shadow engine minus that of the real run, averaged over the runs; the
/// one-sided (1 − α) lower bound of mean d comes from the Kernel's stationary block bootstrap.
/// </summary>
public static class NonInferiority
{
    /// <summary>A-1: annualised margin on the mean daily net-return difference.</summary>
    public const double AnnualMargin = 0.03;

    public const double DailyMargin = AnnualMargin / 365;

    /// <summary>z(0.95) + z(0.80).</summary>
    public const double PowerFactor = 2.487;

    public const int MinimumDays = 28;

    public const int MaximumDays = 56;

    /// <summary>A-3.</summary>
    public const double MinimumValidRate = 0.80;

    /// <summary>A-5.</summary>
    public const int MinimumProposals = 10;

    public const double Alpha = 0.05;

    public const int Replicates = 10_000;

    /// <summary>A-4: window length from the pilot's standard deviation of d; above <see cref="MaximumDays"/> the window is underpowered.</summary>
    public static (int Days, bool Underpowered) PlanDays(double pilotSigma)
    {
        var required = pilotSigma <= 0 ? MinimumDays : (int)Math.Ceiling(Math.Pow(PowerFactor * pilotSigma / DailyMargin, 2));
        var days = Math.Max(MinimumDays, required);
        return days > MaximumDays ? (MaximumDays, true) : (days, false);
    }

    /// <summary>Daily differences d_t over the days every run has, in day order.</summary>
    public static IReadOnlyList<double> Differences(IReadOnlyList<IReadOnlyList<DailyMark>> runs)
    {
        var perRun = runs.Select(run =>
        {
            var ordered = run.OrderBy(m => m.Day).ToList();
            var d = new Dictionary<DateOnly, double>();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i - 1].Real > 0 && ordered[i - 1].Shadow > 0 && ordered[i].Real > 0 && ordered[i].Shadow > 0)
                {
                    d[ordered[i].Day] = Math.Log((double)(ordered[i].Shadow / ordered[i - 1].Shadow)) - Math.Log((double)(ordered[i].Real / ordered[i - 1].Real));
                }
            }

            return d;
        }).ToList();
        if (perRun.Count == 0)
        {
            return [];
        }

        var days = perRun.Skip(1).Aggregate(perRun[0].Keys.ToHashSet(), (set, d) => { set.IntersectWith(d.Keys); return set; });
        return days.Order().Select(day => perRun.Average(d => d[day])).ToList();
    }

    public static double SampleStandardDeviation(IReadOnlyList<double> x)
    {
        if (x.Count < 2)
        {
            return 0;
        }

        var mean = x.Average();
        return Math.Sqrt(x.Sum(v => (v - mean) * (v - mean)) / (x.Count - 1));
    }

    /// <summary>Maximum drawdown of a value series (fraction of the running peak).</summary>
    public static double MaxDrawdown(IEnumerable<decimal> values)
    {
        double peak = 0, worst = 0;
        foreach (var v in values.Select(v => (double)v))
        {
            peak = Math.Max(peak, v);
            if (peak > 0)
            {
                worst = Math.Max(worst, (peak - v) / peak);
            }
        }

        return worst;
    }

    /// <summary>The verdict in the pre-registered order (m8-protocol §6.2).</summary>
    public static NonInferiorityResult Evaluate(IReadOnlyList<IReadOnlyList<DailyMark>> runs, int plannedDays, bool underpowered, ShadowCounts counts, int seed)
    {
        var d = Differences(runs);
        var mean = d.Count == 0 ? 0 : d.Average();
        var lower = d.Count == 0 ? double.NegativeInfinity : StationaryBootstrap.Bounds(mean, StationaryBootstrap.Distribution(d, Replicates, seed, 1.0), Alpha).Lower;
        var drawdowns = runs.Select(r => (MaxDrawdown(r.OrderBy(m => m.Day).Select(m => m.Real)), MaxDrawdown(r.OrderBy(m => m.Day).Select(m => m.Shadow)))).ToList();
        NonInferiorityResult Result(QualificationVerdict verdict, string reason) => new(verdict, reason, d.Count, mean, lower, drawdowns);

        if (counts.SafetyEvents > 0)
        {
            return Result(QualificationVerdict.Failed, "safety event");
        }

        if (underpowered)
        {
            return Result(QualificationVerdict.Inconclusive, "underpowered: the duration rule exceeded 56 days");
        }

        if (d.Count < plannedDays)
        {
            return Result(QualificationVerdict.Inconclusive, "window incomplete");
        }

        if (counts.ValidProposals < MinimumProposals)
        {
            return Result(QualificationVerdict.Inconclusive, "fewer than 10 valid proposals");
        }

        if ((double)counts.ValidProposals / counts.Calls < MinimumValidRate)
        {
            return Result(QualificationVerdict.Failed, "valid-proposal rate below 0.80");
        }

        return lower > -DailyMargin && drawdowns.All(x => x.Item2 - x.Item1 <= 0)
            ? Result(QualificationVerdict.Passed, "non-inferior")
            : Result(QualificationVerdict.Failed, "not non-inferior");
    }
}
