using System.Globalization;
using System.Text;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Allocator;

/// <summary>One decision of a shadow window as stored (trading.allocator_proposals): outcome and safety events only.</summary>
public sealed record ShadowDecisionRow(long RunId, DateTimeOffset BarTime, string Outcome, int SafetyEvents, int Vetoes, int Clamped, int Adjusted);

/// <summary>One shadow step of the window (trading.allocator_shadow_steps): the shadow and real run values after the bar.</summary>
public sealed record ShadowValueRow(long RunId, DateTimeOffset BarTime, decimal ShadowEquity, decimal RealEquity);

/// <summary>Report of one shadow window, split as m8-protocol §7 requires: operational fields for everyone, performance for the user only.</summary>
public sealed record ShadowReport(string Operational, string UserOnly, int Days, double PilotSigma, ShadowCounts Counts);

/// <summary>
/// Shadow-window evaluation (m8-protocol §6): daily marks from the day-end shadow steps (the 20:00 bar closes the UTC day),
/// the counts of calls, valid proposals and safety events, the pilot's σ̂_d with the duration rule, and the verdict.
/// </summary>
public static class ShadowEvaluation
{
    public static IReadOnlyList<IReadOnlyList<DailyMark>> DailyMarks(IEnumerable<ShadowValueRow> rows) =>
        rows.Where(r => r.BarTime.UtcDateTime.Hour == 20)
            .GroupBy(r => r.RunId)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<DailyMark>)g.OrderBy(r => r.BarTime).Select(r => new DailyMark(DateOnly.FromDateTime(r.BarTime.UtcDateTime), r.RealEquity, r.ShadowEquity)).ToList())
            .ToList();

    public static ShadowCounts Counts(IEnumerable<ShadowDecisionRow> decisions)
    {
        var list = decisions.ToList();
        return new ShadowCounts(
            list.Count(d => AllocationOutcome.IsCall(d.Outcome)),
            list.Count(d => d.Outcome == AllocationOutcome.Proposal),
            list.Sum(d => d.SafetyEvents));
    }

    public static ShadowReport Report(AllocatorProtocol protocol, IReadOnlyList<ShadowDecisionRow> decisions, IReadOnlyList<ShadowValueRow> values, decimal costUsd)
    {
        var marks = DailyMarks(values);
        var d = NonInferiority.Differences(marks);
        var sigma = NonInferiority.SampleStandardDeviation(d);
        var counts = Counts(decisions);
        var outcomes = decisions.GroupBy(x => x.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}");
        var operational = new StringBuilder();
        operational.AppendLine(CultureInfo.InvariantCulture, $"protocol={protocol.Hash} role={protocol.Role} planned_days={protocol.Days}");
        operational.AppendLine(CultureInfo.InvariantCulture, $"bars={values.Count} days={d.Count} runs={marks.Count}");
        operational.AppendLine(CultureInfo.InvariantCulture, $"decisions={decisions.Count} outcomes={string.Join(",", outcomes)}");
        operational.AppendLine(CultureInfo.InvariantCulture,
            $"calls={counts.Calls} valid_proposals={counts.ValidProposals} valid_rate={(counts.Calls == 0 ? "-" : ((double)counts.ValidProposals / counts.Calls).ToString("0.000", CultureInfo.InvariantCulture))} safety_events={counts.SafetyEvents}");
        operational.AppendLine(CultureInfo.InvariantCulture,
            $"adjusted_items={decisions.Sum(x => x.Adjusted)} clamped_items={decisions.Sum(x => x.Clamped)} vetoes={decisions.Sum(x => x.Vetoes)} cost_usd={costUsd.ToString("0.000000", CultureInfo.InvariantCulture)}");
        if (protocol.Role == AllocatorProtocol.Pilot)
        {
            var (days, underpowered) = NonInferiority.PlanDays(sigma);
            operational.AppendLine(CultureInfo.InvariantCulture, $"qualification_window_days={days} underpowered={(underpowered ? "true" : "false")} (rule A-4)");
        }

        var user = new StringBuilder();
        user.AppendLine("USER ONLY (m8-protocol §7): performance derived from paper runs of unrevealed cohorts; not research input, not for the repository.");
        user.AppendLine(CultureInfo.InvariantCulture, $"mean_daily_difference={(d.Count == 0 ? 0 : d.Average()).ToString("G6", CultureInfo.InvariantCulture)} sigma_d={sigma.ToString("G6", CultureInfo.InvariantCulture)}");
        foreach (var (run, i) in marks.Select((m, i) => (m, i)))
        {
            user.AppendLine(CultureInfo.InvariantCulture,
                $"run[{i}] max_drawdown real={NonInferiority.MaxDrawdown(run.Select(m => m.Real)).ToString("0.0000", CultureInfo.InvariantCulture)} shadow={NonInferiority.MaxDrawdown(run.Select(m => m.Shadow)).ToString("0.0000", CultureInfo.InvariantCulture)}");
        }

        return new ShadowReport(operational.ToString(), user.ToString(), d.Count, sigma, counts);
    }

    /// <summary>
    /// The TaskQualification record of a closed qualification window: verdict, counts and cost only (no performance value
    /// reaches the ai schema, which research_app can read): trials = calls, successes = valid proposals, baseline = days.
    /// </summary>
    public static (NonInferiorityResult Result, TaskQualification Record) Qualify(AllocatorProtocol protocol, IReadOnlyList<ShadowDecisionRow> decisions,
        IReadOnlyList<ShadowValueRow> values, UsdAmount cost, DateTimeOffset at)
    {
        if (protocol.Role != AllocatorProtocol.Qualification)
        {
            throw new InvalidOperationException("Only a qualification window gives a TaskQualification; a pilot never qualifies.");
        }

        var counts = Counts(decisions);
        var result = NonInferiority.Evaluate(DailyMarks(values), protocol.Days, protocol.Underpowered, counts, protocol.Seed);
        var record = new TaskQualification(0, protocol.Hash, at, result.Verdict, counts.Calls, counts.ValidProposals, result.Days, 0, double.NaN,
            counts.SafetyEvents, cost, result.Reason);
        return (result, record);
    }
}
