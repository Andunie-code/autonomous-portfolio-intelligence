namespace PortfolioIntelligence.Kernel.MarketData;

public sealed record RankingStats(string Symbol, int Expected, int Observed, int Unknown, decimal? MedianQuoteVolume)
{
    public decimal UnknownShare => Expected == 0 ? 1m : (decimal)Unknown / Expected;
}

public enum RankingStatus
{
    Ranked,
    InsufficientObservations,
    TooManyUnknownDays,
}

public sealed record RankedSymbol(string Symbol, RankingStats Stats, RankingStatus Status, int? Rank);

/// <summary>
/// Volume ranking in the ranking window W (docs/m1-data-policy.md §5). A day without a candle is UNKNOWN and is
/// never counted as zero volume. Rankable: at least 90 observed days and at most 10 % UNKNOWN days.
/// </summary>
public static class CoreRanking
{
    public const int MinimumObservedDays = 90;

    public const decimal MaximumUnknownShare = 0.10m;

    /// <param name="dailyCandles">1d candles of one symbol; only members of W are used.</param>
    /// <param name="firstOpenArchive">Open time of the symbol's first 1h candle in the archive.</param>
    public static RankingStats ComputeStats(string symbol, IEnumerable<Candle> dailyCandles, DateTimeOffset firstOpenArchive)
    {
        var window = ResearchCutoff.RankingWindow;
        var firstDay = new DateTimeOffset(firstOpenArchive.UtcDateTime.Date, TimeSpan.Zero);
        var expectedDays = CandleInterval.OneDay.MemberOpens(window).Where(d => d >= firstDay).ToHashSet();
        var observed = dailyCandles
            .Where(c => c.Interval == CandleInterval.OneDay && expectedDays.Contains(c.OpenTime))
            .Select(c => c.QuoteVolume)
            .ToList();

        return new RankingStats(symbol, expectedDays.Count, observed.Count, expectedDays.Count - observed.Count, Median(observed));
    }

    public static IReadOnlyList<RankedSymbol> Rank(IEnumerable<RankingStats> stats)
    {
        var all = stats.ToList();
        var rankable = all
            .Where(s => StatusOf(s) == RankingStatus.Ranked)
            .OrderByDescending(s => s.MedianQuoteVolume!.Value)
            .ThenBy(s => s.Symbol, StringComparer.Ordinal)
            .Select((s, i) => new RankedSymbol(s.Symbol, s, RankingStatus.Ranked, i + 1));
        var unranked = all
            .Where(s => StatusOf(s) != RankingStatus.Ranked)
            .OrderBy(s => s.Symbol, StringComparer.Ordinal)
            .Select(s => new RankedSymbol(s.Symbol, s, StatusOf(s), null));

        return rankable.Concat(unranked).ToList();
    }

    public static RankingStatus StatusOf(RankingStats stats)
    {
        if (stats.Observed < MinimumObservedDays || stats.MedianQuoteVolume is null)
        {
            return RankingStatus.InsufficientObservations;
        }

        return stats.UnknownShare > MaximumUnknownShare ? RankingStatus.TooManyUnknownDays : RankingStatus.Ranked;
    }

    /// <summary>Median; the mean of the two middle values for an even count; null when empty.</summary>
    public static decimal? Median(IReadOnlyCollection<decimal> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2m;
    }
}
