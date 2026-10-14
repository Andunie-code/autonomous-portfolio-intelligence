namespace PortfolioIntelligence.Kernel.MarketData;

/// <summary>
/// Plans which monthly archive files may be requested (docs/m1-data-policy.md §7, T6).
/// A request reaching past C is rejected before anything else happens (H2). A file is planned only when every
/// candle it can contain closes at or before C; a file with a single boundary candle (e.g. the December 2025
/// weekly file) is skipped as a whole and never downloaded.
/// </summary>
public static class MonthlyFilePlanner
{
    public static IReadOnlyList<ArchiveFileKey> Plan(string symbol, CandleInterval interval, TimeRange range)
    {
        if (range.End > ResearchCutoff.C)
        {
            throw new CutoffViolationException(
                $"Requested range {range} reaches past the research cutoff {ResearchCutoff.C:O}; nothing was requested.");
        }

        var files = new List<ArchiveFileKey>();
        var month = new DateTimeOffset(range.Start.Year, range.Start.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (; month < range.End; month = month.AddMonths(1))
        {
            var key = new ArchiveFileKey(symbol, interval, month.Year, month.Month);
            if (IsEntirelyBeforeCutoff(key))
            {
                files.Add(key);
            }
        }

        return files;
    }

    /// <summary>True when the latest candle the file can contain closes at or before C.</summary>
    public static bool IsEntirelyBeforeCutoff(ArchiveFileKey key)
    {
        var lastOpen = key.Interval.LastOpenBefore(key.MonthStart, key.MonthEnd);
        return lastOpen is null || key.Interval.CloseOf(lastOpen.Value) <= ResearchCutoff.C;
    }
}
