using System.Globalization;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.MarketData.Archive;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.Research.Evaluation;

/// <summary>
/// P1 (docs/m5-protocol.md §9, pre-registered): historical impact of a binding correlation-cluster exposure cap on
/// the Kohort 0 baselines over the research window. Policy run, one sleeve, 10,000 USDT, cost model v1. Not a
/// strategy trial: definitions are unchanged and no result enters a gate or a cohort. Research data (&lt; C) only.
/// </summary>
internal static class P1Analysis
{
    public static readonly double[] Thresholds = [0.70, 0.60, 0.80];

    public static async Task<int> RunAsync(string connectionString, string manifestPath, string engineSha)
    {
        var manifest = CohortManifestReader.Read(await File.ReadAllTextAsync(manifestPath));
        var store = new CandleStore(connectionString);
        var range = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);
        var candles = new Dictionary<string, IReadOnlyList<Candle>>();
        foreach (var (symbol, _) in manifest.Pairs)
        {
            candles[symbol] = await store.ReadAsync(symbol, manifest.Interval, range, CancellationToken.None);
        }

        var symbols = manifest.Pairs.Select(p => p.Symbol).ToList();
        var rows = new List<P1Row>();
        foreach (var member in manifest.Members)
        {
            // The uncapped run is the same for every threshold; it is measured once per threshold for the share statistics.
            foreach (var threshold in Thresholds)
            {
                foreach (var cap in P1Rule.Caps)
                {
                    rows.Add(Run(member, symbols, candles, manifest, threshold, cap));
                    Console.WriteLine($"{member.Name} rho*={threshold:F2} cap={(cap is { } c ? c.ToString("P0", CultureInfo.InvariantCulture) : "none")}: M {rows[^1].M:P2}, DD {rows[^1].MaxDrawdown:P2}");
                }
            }
        }

        var recommendation = P1Rule.Recommend(rows.Where(r => r.Threshold == 0.70).ToList());
        var reports = Directory.CreateDirectory(Path.Combine(ArchiveCacheDirectory.DefaultRoot, "reports")).FullName;
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(reports, $"m5-p1-analysis-{stamp}.json");
        var json = JsonSerializer.Serialize(new { manifest = manifest.Hash, engineSha, rows, recommendation }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        Console.WriteLine($"Recommendation (pre-registered rule, rho* = 0.70): {recommendation.Text}");
        Console.WriteLine($"Report {DslCanonical.Sha256(json)}: {path}");
        return 0;
    }

    private static P1Row Run(FrozenMember member, IReadOnlyList<string> symbols, IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles, OperationalManifest manifest, double threshold, decimal? cap)
    {
        var risk = new RiskConfig(manifest.Risk, ClusterCap: cap is { } c ? new ClusterCapSettings(threshold, c) : null);
        var engine = new PortfolioEngine(new PortfolioSetup(
            [new SleeveSpec(member.Name, DslStrategy.FromJson(member.DefinitionJson), manifest.InitialCapital)],
            CostModel.V1, risk, symbols, manifest.Interval, ResearchCutoff.U, manifest.ExperimentalSymbols, RecordEvents: true));
        var length = manifest.Interval.FixedLength!.Value;
        var byTime = candles.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(x => x.OpenTime));
        var dayEnd = symbols.ToDictionary(s => s, _ => new Dictionary<DateOnly, decimal>());
        var start = candles.Values.SelectMany(x => x).Min(x => x.OpenTime);
        var effective = new List<double>();
        var shares = new List<double>();
        for (var t = start; t < ResearchCutoff.C; t += length)
        {
            var bars = symbols.Where(s => byTime[s].ContainsKey(t)).ToDictionary(s => s, s => byTime[s][t]);
            engine.Step(t, bars, bars.GetValueOrDefault("BTCUSDT"), t + length >= ResearchCutoff.C ? StepContext.Final : StepContext.Normal);
            var close = t + length;
            foreach (var (symbol, bar) in bars.Where(_ => close.TimeOfDay == TimeSpan.Zero))
            {
                dayEnd[symbol][DateOnly.FromDateTime(t.UtcDateTime)] = bar.Close;
            }

            if (t < ResearchCutoff.U || close.TimeOfDay != TimeSpan.Zero)
            {
                continue;
            }

            // Day-end sample: clusters and effective count of the held positions (no look-ahead).
            var asOf = DateOnly.FromDateTime(close.UtcDateTime).AddDays(-1);
            var closes = dayEnd.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<DateOnly, decimal>)kv.Value);
            var equity = (double)engine.TotalEquity;
            var exposure = engine.Positions().GroupBy(p => p.Symbol).ToDictionary(g => g.Key, g => (double)g.Sum(p => p.Quantity * p.LastClose) / equity);
            var clusters = CorrelationClusters.Assign(symbols, closes, asOf, InverseVolatilityAllocation.VolatilityDays, threshold);
            shares.Add(clusters.GroupBy(x => x.Value).Select(g => g.Sum(x => exposure.GetValueOrDefault(x.Key))).DefaultIfEmpty(0).Max());
            var (complete, matrix) = CorrelationClusters.Matrix(symbols, closes, asOf, InverseVolatilityAllocation.VolatilityDays);
            if (exposure.Count > 0)
            {
                effective.Add(CorrelationClusters.EffectiveCount(complete.Select(s => exposure.GetValueOrDefault(s)).ToList(), matrix));
            }
        }

        var result = engine.Result(0);
        var equitySeries = result.Equity;
        var worst = 0.0;
        for (var i = 0; i + 180 < equitySeries.Count; i++)
        {
            worst = Math.Min(worst, (double)(equitySeries[i + 180].Equity / equitySeries[i].Equity) - 1);
        }

        shares.Sort();
        double Quantile(double q) => shares.Count == 0 ? 0 : shares[(int)Math.Min(shares.Count - 1, Math.Floor(q * shares.Count))];
        return new P1Row(member.Name, threshold, cap, ReturnStatistics.PrimaryMetric(result), ReturnStatistics.MaxDrawdown(result.InitialCapital, equitySeries), worst,
            result.Trades.Count,
            engine.Events.Count(e => e is { Kind: EngineEventKind.Clipped, Detail: "cluster-cap" }),
            engine.Events.Count(e => e is { Kind: EngineEventKind.Rejected, Detail: "cluster-cap" }),
            equitySeries.Count == 0 ? 0 : equitySeries.Average(p => (double)p.Exposure),
            effective.Count == 0 ? 0 : effective.Average(),
            Quantile(0.5), Quantile(0.95), shares.Count == 0 ? 0 : shares[^1]);
    }
}
