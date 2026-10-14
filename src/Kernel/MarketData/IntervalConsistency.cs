namespace PortfolioIntelligence.Kernel.MarketData;

public sealed record IntervalAnomaly(DateTimeOffset FourHourOpen, IReadOnlyList<string> MismatchedFields);

public sealed record IntervalConsistencyResult(int Verified, int Unverifiable, IReadOnlyList<IntervalAnomaly> Anomalies)
{
    public int Matching => Verified - Anomalies.Count;
}

/// <summary>
/// Compares each 4h candle with the four 1h candles of the same block (docs/m1-data-policy.md §6). Mismatches are
/// reported as anomalies and never corrected; a block with a missing 1h candle is unverifiable, not an error.
/// Comparison is exact decimal equality; no tolerance is applied.
/// </summary>
public static class IntervalConsistency
{
    public static IntervalConsistencyResult Check(IEnumerable<Candle> oneHour, IEnumerable<Candle> fourHour)
    {
        var hourly = oneHour.Where(c => c.Interval == CandleInterval.OneHour).ToDictionary(c => c.OpenTime);
        var verified = 0;
        var unverifiable = 0;
        var anomalies = new List<IntervalAnomaly>();

        foreach (var block in fourHour.Where(c => c.Interval == CandleInterval.FourHours).OrderBy(c => c.OpenTime))
        {
            var parts = new List<Candle>(4);
            for (var i = 0; i < 4; i++)
            {
                if (hourly.TryGetValue(block.OpenTime.AddHours(i), out var part))
                {
                    parts.Add(part);
                }
            }

            if (parts.Count != 4)
            {
                unverifiable++;
                continue;
            }

            verified++;
            var mismatched = new List<string>();
            Compare("open", block.Open, parts[0].Open, mismatched);
            Compare("high", block.High, parts.Max(p => p.High), mismatched);
            Compare("low", block.Low, parts.Min(p => p.Low), mismatched);
            Compare("close", block.Close, parts[3].Close, mismatched);
            Compare("base_volume", block.BaseVolume, parts.Sum(p => p.BaseVolume), mismatched);
            Compare("quote_volume", block.QuoteVolume, parts.Sum(p => p.QuoteVolume), mismatched);
            if (mismatched.Count > 0)
            {
                anomalies.Add(new IntervalAnomaly(block.OpenTime, mismatched));
            }
        }

        return new IntervalConsistencyResult(verified, unverifiable, anomalies);
    }

    private static void Compare(string field, decimal expected, decimal actual, List<string> mismatched)
    {
        if (expected != actual)
        {
            mismatched.Add(field);
        }
    }
}
