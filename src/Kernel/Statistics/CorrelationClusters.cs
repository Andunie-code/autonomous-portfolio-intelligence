namespace PortfolioIntelligence.Kernel.Statistics;

/// <summary>
/// Return correlation, average-linkage clustering and the effective number of independent positions
/// (m5-protocol §2.7 and §9). Uses only day-end closes up to the as-of date (no look-ahead).
/// </summary>
public static class CorrelationClusters
{
    /// <summary>
    /// Daily log returns of the <paramref name="days"/> days ending at <paramref name="asOf"/> (days + 1 consecutive
    /// closes), or null if any close is missing or not positive.
    /// </summary>
    public static double[]? DailyReturns(IReadOnlyDictionary<DateOnly, decimal> closes, DateOnly asOf, int days)
    {
        var returns = new double[days];
        if (!closes.TryGetValue(asOf.AddDays(-days), out var previous) || previous <= 0)
        {
            return null;
        }

        for (var i = 0; i < days; i++)
        {
            if (!closes.TryGetValue(asOf.AddDays(i - days + 1), out var close) || close <= 0)
            {
                return null;
            }

            returns[i] = Math.Log((double)close / (double)previous);
            previous = close;
        }

        return returns;
    }

    /// <summary>Pearson correlation; 0 when either series has zero variance.</summary>
    public static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var meanA = a.Average();
        var meanB = b.Average();
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            sab += (a[i] - meanA) * (b[i] - meanB);
            saa += (a[i] - meanA) * (a[i] - meanA);
            sbb += (b[i] - meanB) * (b[i] - meanB);
        }

        return saa <= 0 || sbb <= 0 ? 0 : sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>Correlation matrix of the symbols with complete returns; others are absent from the result.</summary>
    public static (IReadOnlyList<string> Symbols, double[,] Matrix) Matrix(IReadOnlyList<string> symbols, IReadOnlyDictionary<string, IReadOnlyDictionary<DateOnly, decimal>> closes, DateOnly asOf, int days)
    {
        var series = symbols
            .Select(s => (Symbol: s, Returns: closes.TryGetValue(s, out var c) ? DailyReturns(c, asOf, days) : null))
            .Where(x => x.Returns is not null)
            .ToList();
        var matrix = new double[series.Count, series.Count];
        for (var i = 0; i < series.Count; i++)
        {
            matrix[i, i] = 1;
            for (var j = i + 1; j < series.Count; j++)
            {
                matrix[i, j] = matrix[j, i] = Pearson(series[i].Returns!, series[j].Returns!);
            }
        }

        return (series.Select(x => x.Symbol).ToList(), matrix);
    }

    /// <summary>
    /// Average-linkage agglomerative clustering with distance 1 − ρ: the two clusters with the highest average
    /// pairwise correlation are merged while it is ≥ <paramref name="threshold"/> (ties: lowest member indices).
    /// Returns symbol → cluster id (the lowest symbol index in the cluster). Symbols without complete data are singletons.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Assign(IReadOnlyList<string> symbols, IReadOnlyDictionary<string, IReadOnlyDictionary<DateOnly, decimal>> closes, DateOnly asOf, int days, double threshold)
    {
        var (complete, matrix) = Matrix(symbols, closes, asOf, days);
        var clusters = complete.Select((_, i) => new List<int> { i }).ToList();
        while (clusters.Count > 1)
        {
            var best = double.NegativeInfinity;
            (int A, int B) pair = (-1, -1);
            for (var a = 0; a < clusters.Count; a++)
            {
                for (var b = a + 1; b < clusters.Count; b++)
                {
                    var sum = 0.0;
                    foreach (var i in clusters[a])
                    {
                        foreach (var j in clusters[b])
                        {
                            sum += matrix[i, j];
                        }
                    }

                    var average = sum / (clusters[a].Count * clusters[b].Count);
                    if (average > best)
                    {
                        best = average;
                        pair = (a, b);
                    }
                }
            }

            if (best < threshold)
            {
                break;
            }

            clusters[pair.A].AddRange(clusters[pair.B]);
            clusters.RemoveAt(pair.B);
        }

        var index = symbols.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        var result = symbols.ToDictionary(s => s, s => index[s]);
        foreach (var cluster in clusters)
        {
            var id = cluster.Min(i => index[complete[i]]);
            foreach (var i in cluster)
            {
                result[complete[i]] = id;
            }
        }

        return result;
    }

    /// <summary>Effective number of independent positions: (Σw)² / Σᵢ Σⱼ wᵢ wⱼ ρᵢⱼ; 0 without exposure.</summary>
    public static double EffectiveCount(IReadOnlyList<double> weights, double[,] correlation)
    {
        double total = weights.Sum(), quadratic = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            for (var j = 0; j < weights.Count; j++)
            {
                quadratic += weights[i] * weights[j] * correlation[i, j];
            }
        }

        return quadratic <= 0 ? 0 : total * total / quadratic;
    }
}
