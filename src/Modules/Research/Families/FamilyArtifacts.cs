using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Research.Families;

/// <summary>
/// Representative artefacts of ADR-0015 on the research window [U, C): per pair, a bitset of entry events on the
/// 1h grid (≈ 4.4 KB) and an int8 exposure vector on the 1d grid (share of the day's six 4h candles with an open
/// position, scaled to 0–127). Size depends on the number of pairs only, never on the number of trials.
/// </summary>
public sealed class FamilyArtifacts
{
    public static readonly DateTimeOffset Start = ResearchCutoff.U;
    public static readonly DateTimeOffset End = ResearchCutoff.C;
    public static readonly int Hours = (int)(End - Start).TotalHours;
    public static readonly int Days = (int)(End - Start).TotalDays;

    public FamilyArtifacts(IReadOnlyDictionary<string, (byte[] Entries, sbyte[] Exposure)> pairs)
    {
        if (pairs.Values.Any(p => p.Entries.Length != (Hours + 7) / 8 || p.Exposure.Length != Days))
        {
            throw new ArgumentException("Artefact sizes do not match the research window.");
        }

        Pairs = pairs;
    }

    public IReadOnlyDictionary<string, (byte[] Entries, sbyte[] Exposure)> Pairs { get; }

    public static FamilyArtifacts From(BacktestResult result)
    {
        var pairs = new Dictionary<string, (byte[], sbyte[])>(StringComparer.Ordinal);
        foreach (var group in result.Trades.GroupBy(t => t.Symbol))
        {
            var entries = new byte[(Hours + 7) / 8];
            var bars = new int[Days];
            foreach (var trade in group)
            {
                var hour = (int)(trade.EntryTime - Start).TotalHours;
                if (hour >= 0 && hour < Hours)
                {
                    entries[hour / 8] |= (byte)(1 << (hour % 8));
                }

                for (var t = trade.EntryTime; t < trade.ExitTime; t = t.AddHours(4))
                {
                    var day = (int)(t - Start).TotalDays;
                    if (day >= 0 && day < Days)
                    {
                        bars[day]++;
                    }
                }
            }

            pairs[group.Key] = (entries, bars.Select(b => (sbyte)Math.Round(Math.Min(b, 6) / 6.0 * 127, MidpointRounding.AwayFromZero)).ToArray());
        }

        return new FamilyArtifacts(pairs);
    }

    /// <summary>Jaccard similarity of the (pair, hour) entry events; 0 when both sets are empty.</summary>
    public static double Jaccard(FamilyArtifacts a, FamilyArtifacts b)
    {
        long intersection = 0, union = 0;
        foreach (var symbol in a.Pairs.Keys.Union(b.Pairs.Keys))
        {
            var x = a.Pairs.TryGetValue(symbol, out var pa) ? pa.Entries : null;
            var y = b.Pairs.TryGetValue(symbol, out var pb) ? pb.Entries : null;
            for (var i = 0; i < (Hours + 7) / 8; i++)
            {
                var u = x?[i] ?? 0;
                var v = y?[i] ?? 0;
                intersection += System.Numerics.BitOperations.PopCount((uint)(u & v));
                union += System.Numerics.BitOperations.PopCount((uint)(u | v));
            }
        }

        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>Pearson correlation of the exposure vectors on the common 1d grid; pairs one side lacks count as zero.</summary>
    public static double Correlation(FamilyArtifacts a, FamilyArtifacts b)
    {
        double n = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        foreach (var symbol in a.Pairs.Keys.Union(b.Pairs.Keys).Order(StringComparer.Ordinal))
        {
            var x = a.Pairs.TryGetValue(symbol, out var pa) ? pa.Exposure : null;
            var y = b.Pairs.TryGetValue(symbol, out var pb) ? pb.Exposure : null;
            for (var i = 0; i < Days; i++)
            {
                double u = x?[i] ?? 0, v = y?[i] ?? 0;
                n++;
                sx += u;
                sy += v;
                sxx += u * u;
                syy += v * v;
                sxy += u * v;
            }
        }

        var cov = sxy - sx * sy / n;
        var vx = sxx - sx * sx / n;
        var vy = syy - sy * sy / n;
        return n == 0 || vx <= 0 || vy <= 0 ? 0 : cov / Math.Sqrt(vx * vy);
    }

    public long Bytes => Pairs.Values.Sum(p => (long)p.Entries.Length + p.Exposure.Length);
}
