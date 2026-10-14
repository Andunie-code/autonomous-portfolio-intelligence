namespace PortfolioIntelligence.Kernel.MarketData;

/// <summary>Half-open UTC interval [Start, End).</summary>
public readonly record struct TimeRange
{
    public TimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Time ranges must be expressed in UTC (offset 0).");
        }

        if (end < start)
        {
            throw new ArgumentException("End must not precede start.");
        }

        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    /// <summary>Whole-interval membership (T4): S ≤ o and o + Δ ≤ E.</summary>
    public bool ContainsCandle(DateTimeOffset open, CandleInterval interval) =>
        Start <= open && interval.CloseOf(open) <= End;

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public override string ToString() => $"[{Start:O}, {End:O})";
}
