using System.Text.Json;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Execution;

/// <summary>L3 comparison of one sleeve (ADR-0015): normalised values only, never absolute order sizes.</summary>
public sealed record SleeveDifference(
    string Strategy,
    int IsolatedTrades,
    int PortfolioTrades,
    double TimingMatchRatio,
    double IsolatedMeanTradeReturnPct,
    double PortfolioMeanTradeReturnPct,
    double IsolatedReturnPerExposure,
    double PortfolioReturnPerExposure,
    IReadOnlyDictionary<string, int> PortfolioConstraintDecisions);

public sealed record ParityReport(
    string ManifestHash,
    DateTimeOffset TradingStart,
    DateTimeOffset End,
    int Steps,
    int Restarts,
    int Fills,
    string KernelL1Hash,
    string KernelL2Hash,
    string PaperL1Hash,
    string PaperL2Hash,
    bool L1Equal,
    bool L2Equal,
    string? FirstDifference,
    bool ReconciliationOk,
    IReadOnlyList<SleeveDifference> L3)
{
    public bool Passed => L1Equal && L2Equal && ReconciliationOk;

    public string Json() => JsonSerializer.Serialize(this, PaperJson.Options);

    public string Hash() => DslCanonical.Sha256(Json());
}

/// <summary>
/// Parity replay for Frozen → PaperTrading (m5-protocol §5.1, ADR-0015 L1/L2): (a) the kernel engine in one batch
/// and (b) the paper trader's real cycle code, fed on an 8-hour cycle schedule from the same candles and restarted
/// every <c>restartEvery</c>, must produce byte-identical L1 and L2 event streams. L3 compares each sleeve with the
/// isolated, evaluator-style single-strategy run (fixed initial capital) on normalised values; it is a report only.
/// </summary>
public static class ParityReplay
{
    public static async Task<ParityReport> RunAsync(
        string manifestJson,
        long cohortId,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles,
        DateTimeOffset tradingStart,
        DateTimeOffset end,
        TimeSpan restartEvery,
        CancellationToken ct)
    {
        var manifest = CohortManifestReader.Read(manifestJson);
        var symbols = manifest.Pairs.Select(p => p.Symbol).ToList();
        var interval = manifest.Interval;
        var length = interval.FixedLength!.Value;
        var warmupStart = candles.Values.SelectMany(c => c).Min(c => c.OpenTime);
        var frozen = manifest.Members.Select(m => new FrozenRecord(m.Index, cohortId, m.Index, m.Name, m.DefinitionHash, manifest.Hash, manifestJson, "PaperTrading")).ToList();
        var store = new InMemoryPaperStore(frozen, manifest.Members.ToDictionary(m => m.DefinitionHash, m => m.DefinitionJson));
        var source = new MemoryCandleSource(candles);

        // (b) Paper trader: first cycle just before the trading start, then every 8 hours; a new trader is a restart.
        var trader = new PaperTrader(store, source, CostModel.V1, tradingStart - warmupStart);
        await trader.ProcessAsync(tradingStart.AddHours(-1), ct);
        var restarts = 0;
        var lastRestart = tradingStart;
        var reconciliationOk = true;
        for (var now = tradingStart.AddSeconds(15); now < end; now = now.AddHours(8))
        {
            if (now - lastRestart >= restartEvery)
            {
                trader = new PaperTrader(store, source, CostModel.V1, tradingStart - warmupStart);
                lastRestart = now;
                restarts++;
            }

            var outcome = await trader.ProcessAsync(now, ct);
            reconciliationOk &= outcome.ReconciliationOk && !outcome.Stale;
        }

        var paperEvents = store.Written.SelectMany(s => s.Events).ToList();
        var lastBar = store.Written.Count == 0 ? tradingStart : store.Written[^1].BarTime;

        // (a) Kernel batch run with the same configuration.
        var risk = new RiskConfig(manifest.Risk);
        var capital = Math.Round(PaperRunConfig.NominalCapital / manifest.Members.Count, 8);
        var kernel = new PortfolioEngine(new PortfolioSetup(
            manifest.Members.Select(m => new SleeveSpec(m.Name, DslStrategy.FromJson(m.DefinitionJson), capital)).ToList(),
            CostModel.V1, risk, symbols, interval, tradingStart, manifest.ExperimentalSymbols, RecordEvents: true));
        var reference = candles.GetValueOrDefault(PaperTrader.ReferenceSymbol);
        PortfolioReplay.Run(kernel, symbols, candles, reference, length, lastBar + length, final: false);

        var kernelL1 = EngineEvents.Canonical(kernel.Events, false);
        var paperL1 = EngineEvents.Canonical(paperEvents, false);
        var kernelL2 = EngineEvents.Canonical(kernel.Events, true);
        var paperL2 = EngineEvents.Canonical(paperEvents, true);

        // L3: each sleeve against its isolated single-strategy run (evaluator-style, fixed initial capital).
        var l3 = manifest.Members.Select((m, k) =>
        {
            var isolated = new PortfolioEngine(new PortfolioSetup(
                [new SleeveSpec(m.Name, DslStrategy.FromJson(m.DefinitionJson), manifest.InitialCapital)],
                CostModel.V1, risk, symbols, interval, tradingStart, manifest.ExperimentalSymbols, RecordEvents: true));
            PortfolioReplay.Run(isolated, symbols, candles, reference, length, lastBar + length, final: false);
            return Difference(m.Name, isolated, 0, kernel, k);
        }).ToList();

        return new ParityReport(
            manifest.Hash, tradingStart, lastBar + length, store.Written.Count, restarts, paperEvents.Count(e => e.Kind is EngineEventKind.EntryFill or EngineEventKind.ExitFill),
            Sha(kernelL1), Sha(kernelL2), Sha(paperL1), Sha(paperL2), kernelL1 == paperL1, kernelL2 == paperL2,
            FirstDifference(kernelL1, paperL1) ?? FirstDifference(kernelL2, paperL2),
            reconciliationOk && store.Reconciliations.All(r => r.Ok), l3);
    }

    private static string Sha(string text) => DslCanonical.Sha256(text);

    private static string? FirstDifference(string a, string b)
    {
        if (a == b)
        {
            return null;
        }

        var left = a.Split('\n');
        var right = b.Split('\n');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var l = i < left.Length ? left[i] : "<end>";
            var r = i < right.Length ? right[i] : "<end>";
            if (l != r)
            {
                return $"line {i + 1}: kernel '{l}' vs paper '{r}'";
            }
        }

        return "length differs";
    }

    private static SleeveDifference Difference(string name, PortfolioEngine isolated, int isolatedSleeve, PortfolioEngine portfolio, int sleeve)
    {
        var a = isolated.Trades(isolatedSleeve);
        var b = portfolio.Trades(sleeve);
        var bKeys = b.Select(t => (t.Symbol, t.EntryTime)).ToHashSet();
        double MeanReturn(IReadOnlyList<Trade> trades) => trades.Count == 0 ? 0 : trades.Average(t => (double)(t.ExitPrice / t.EntryPrice - 1)) * 100;
        double PerExposure(IReadOnlyList<EquityPoint> equity, decimal initial)
        {
            if (equity.Count == 0)
            {
                return 0;
            }

            var exposure = equity.Average(p => (double)p.Exposure);
            return exposure <= 0 ? 0 : ((double)(equity[^1].Equity / initial) - 1) / exposure;
        }

        var constraints = portfolio.Events
            .Where(e => e.Sleeve == sleeve && e.Kind is EngineEventKind.Rejected or EngineEventKind.Clipped)
            .GroupBy(e => e.Detail)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
        return new SleeveDifference(
            name, a.Count, b.Count,
            a.Count == 0 ? 1 : a.Count(t => bKeys.Contains((t.Symbol, t.EntryTime))) / (double)a.Count,
            Math.Round(MeanReturn(a), 4), Math.Round(MeanReturn(b), 4),
            Math.Round(PerExposure(isolated.Equity(isolatedSleeve), isolated.Result(isolatedSleeve).InitialCapital), 6),
            Math.Round(PerExposure(portfolio.Equity(sleeve), portfolio.Result(sleeve).InitialCapital), 6),
            constraints);
    }

    private sealed class MemoryCandleSource(IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles) : ICandleSource
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<Candle>>> ReadAsync(IReadOnlyList<string> symbols, CandleInterval interval, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            var result = new Dictionary<string, IReadOnlyList<Candle>>();
            foreach (var symbol in symbols)
            {
                var series = candles.TryGetValue(symbol, out var s) ? s : [];
                var first = LowerBound(series, from);
                var last = LowerBound(series, to);
                var slice = new List<Candle>(Math.Max(0, last - first));
                for (var i = first; i < last; i++)
                {
                    slice.Add(series[i]);
                }

                result[symbol] = slice;
            }

            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Candle>>>(result);
        }

        private static int LowerBound(IReadOnlyList<Candle> series, DateTimeOffset time)
        {
            int lo = 0, hi = series.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (series[mid].OpenTime < time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }
}
