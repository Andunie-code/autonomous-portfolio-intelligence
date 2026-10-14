namespace PortfolioIntelligence.Kernel.MarketData;

public enum CoverageLabel
{
    Continuous,
    Ended,
    LateStart,
    InternalGap,
    LowCoverage,
}

public sealed record CoverageResult(
    CandleInterval Interval,
    int Expected,
    int Observed,
    int Missing,
    int LongestGapSlots,
    int LeadingGapSlots,
    int TrailingGapSlots,
    CoverageLabel Label)
{
    public bool Passes => Label == CoverageLabel.Continuous;
}

/// <summary>
/// Coverage and gap rules of docs/m1-data-policy.md §6 (ADR-0012): at least 99 % of the expected candles present
/// and no single gap longer than 72 hours. The denominator is fixed; gaps touching the window start or end count.
/// Missing candles are never filled. No outage exemptions are applied.
/// </summary>
public static class CoverageAnalyzer
{
    public const decimal MinimumCoverage = 0.99m;

    public static readonly TimeSpan MaximumGap = TimeSpan.FromHours(72);

    public static CoverageResult Analyze(IEnumerable<DateTimeOffset> observedOpens, CandleInterval interval, TimeRange window)
    {
        if (interval.FixedLength is not { } length)
        {
            throw new ArgumentException("Coverage is defined for fixed-length intervals only.", nameof(interval));
        }

        var observed = observedOpens.Where(o => window.ContainsCandle(o, interval)).ToHashSet();
        var expected = interval.MemberOpens(window).ToList();
        var maxGapSlots = (int)(MaximumGap.Ticks / length.Ticks);
        var minimumObserved = (int)Math.Ceiling(expected.Count * MinimumCoverage);

        int longest = 0, run = 0, leading = 0;
        var seenAny = false;
        foreach (var open in expected)
        {
            if (observed.Contains(open))
            {
                if (!seenAny)
                {
                    leading = run;
                    seenAny = true;
                }

                run = 0;
                continue;
            }

            run++;
            longest = Math.Max(longest, run);
        }

        var trailing = run;
        if (!seenAny)
        {
            leading = run;
        }

        var present = expected.Count(observed.Contains);
        var label = Classify(present, minimumObserved, longest, leading, trailing, maxGapSlots);
        return new CoverageResult(interval, expected.Count, present, expected.Count - present, longest, leading, trailing, label);
    }

    private static CoverageLabel Classify(int present, int minimumObserved, int longest, int leading, int trailing, int maxGapSlots)
    {
        if (trailing > maxGapSlots)
        {
            return CoverageLabel.Ended;
        }

        if (leading > maxGapSlots)
        {
            return CoverageLabel.LateStart;
        }

        if (longest > maxGapSlots)
        {
            return CoverageLabel.InternalGap;
        }

        return present < minimumObserved ? CoverageLabel.LowCoverage : CoverageLabel.Continuous;
    }
}
