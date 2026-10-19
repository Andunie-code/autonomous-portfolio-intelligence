using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;
using static PortfolioIntelligence.Backtest.Tests.Support.Synthetic;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>
/// Step engine (ADR-0018): portfolio-wide limits over sleeves, the experimental-pair cap (testing §7.3), operational
/// step inputs (m5-protocol §2.4), event streams and replay determinism. Synthetic data only.
/// </summary>
public class PortfolioEngineTests
{
    private const int History = -240;

    private static decimal DayPrice(int i) => 100m * (Math.Abs(Math.Floor(i / 6.0)) % 2 == 0 ? 1m : 1.02m);

    private static List<Candle> Series(string symbol, int bars, Func<int, decimal>? price = null) =>
        Enumerable.Range(History, bars - History).Select(i =>
        {
            var p = price?.Invoke(i) ?? DayPrice(i);
            return Bar(symbol, At(i), p, p * 1.01m, p * 0.99m, p);
        }).ToList();

    private static ScriptedStrategy Scripted(int[] entries, int[] exits, decimal stop = 0.5m) =>
        new(entries.Select(At).ToHashSet(), exits.Select(At).ToHashSet(), stop);

    /// <summary>Scripted per symbol: enters and exits at given (symbol, bar) pairs.</summary>
    private sealed class Keyed(string name, (string Symbol, int Bar)[] entries, (string Symbol, int Bar)[] exits, decimal stop = 0.5m) : IStrategy
    {
        private (string, int)[] Entries { get; } = entries;

        private (string, int)[] Exits { get; } = exits;

        private decimal Stop { get; } = stop;

        public string Name => name;

        public string Version => "test";

        public string Definition => "keyed test strategy";

        public int WarmupBars => 1;

        public IStrategyInstance CreateInstance() => new Instance(this);

        private sealed class Instance(Keyed owner) : IStrategyInstance
        {
            public bool EntrySignal { get; private set; }

            public bool ExitSignal { get; private set; }

            public decimal? TrailingStop => null;

            public void Update(Candle candle)
            {
                EntrySignal = owner.Entries.Contains((candle.Symbol, Index(candle.OpenTime)));
                ExitSignal = owner.Exits.Contains((candle.Symbol, Index(candle.OpenTime)));
            }

            public decimal InitialStop(decimal entryFill) => entryFill * (1 - owner.Stop);

            public void Reset()
            {
                EntrySignal = false;
                ExitSignal = false;
            }

            private static int Index(DateTimeOffset time) => (int)((time - Start).TotalHours / 4);
        }
    }

    private static PortfolioEngine Engine(RiskConfig risk, string[] symbols, ISet<string>? experimental = null, params IStrategy[] sleeves) =>
        new(new PortfolioSetup(
            sleeves.Select((s, i) => new SleeveSpec($"s{i}", s, 10_000m / sleeves.Length)).ToList(),
            CostModel.V1, risk, symbols, CandleInterval.FourHours, Start, experimental?.ToHashSet(), RecordEvents: true));

    private static void Replay(PortfolioEngine engine, List<Candle>[] series, Func<DateTimeOffset, StepContext>? context = null, int? until = null)
    {
        var end = until is { } u ? At(u) : series.Max(s => s[^1].OpenTime) + TimeSpan.FromHours(4);
        PortfolioReplay.Run(engine, series.Select(s => s[0].Symbol).ToList(), series.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s), null, TimeSpan.FromHours(4), end, context, final: false);
    }

    private static List<EngineEvent> Decisions(PortfolioEngine engine, string symbol) =>
        engine.Events.Where(e => e.Symbol == symbol && e.IsDecision).ToList();

    [Fact]
    public void Events_are_recorded_without_changing_the_result()
    {
        var series = new[] { RandomWalk("AUSDT", 1400, 5), RandomWalk("BUSDT", 1400, 6) };
        var input = new BacktestInput(series.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s), new Dictionary<string, int> { ["AUSDT"] = 1, ["BUSDT"] = 2 }, At(300), At(1400));
        var plain = new BacktestEngine(new SupertrendStrategy(), CostModel.V1, RiskSettings.Policy).Run(input);
        var withEvents = new BacktestEngine(new SupertrendStrategy(), CostModel.V1, RiskSettings.Policy).RunWithEvents(input);

        Assert.Equal(plain.Hash(), withEvents.Result(0).Hash());
        Assert.Contains(withEvents.Events, e => e.Kind == EngineEventKind.EntryFill);
        Assert.Contains(withEvents.Events, e => e.Kind == EngineEventKind.StopRaised);
        Assert.Contains(withEvents.Events, e => e.Kind == EngineEventKind.Approved);
        Assert.Equal(withEvents.Events.Count(e => e.Kind == EngineEventKind.EntryFill), plain.Trades.Count);
    }

    [Fact]
    public void Replaying_the_same_steps_rebuilds_the_same_state()
    {
        var series = new[] { RandomWalk("AUSDT", 1200, 11), RandomWalk("BUSDT", 1200, 12), RandomWalk("CUSDT", 1200, 13) };
        var symbols = series.Select(s => s[0].Symbol).ToArray();
        StepContext Context(DateTimeOffset t) => t == At(600) ? new StepContext(EntriesBlockedReason: "kill-switch") : StepContext.Normal;
        var first = Engine(RiskConfig.Policy, symbols, null, new SupertrendStrategy(), new EmaTrendStrategy());
        var second = Engine(RiskConfig.Policy, symbols, null, new SupertrendStrategy(), new EmaTrendStrategy());
        Replay(first, series, Context, until: 900);
        Replay(second, series, Context, until: 900);

        Assert.Equal(first.StateHash(), second.StateHash());
        Assert.Equal(EngineEvents.Hash(first.Events, false), EngineEvents.Hash(second.Events, false));
        Assert.Equal(EngineEvents.Hash(first.Events, true), EngineEvents.Hash(second.Events, true));
        Assert.Throws<InvalidOperationException>(() => first.Step(At(899), new Dictionary<string, Candle>(), null, StepContext.Normal));
    }

    [Fact]
    public void Experimental_cap_accepts_the_exact_limit_and_clips_or_rejects_above_it()
    {
        // Two symbols with equal volatility enter together: weights 0.2 each, i.e. 2000 USDT targets.
        var series = new[] { Series("AUSDT", 20), Series("EUSDT", 20) };
        var exact = Engine(RiskConfig.Policy with { ExperimentalPairsMaxExposure = 0.20m }, ["AUSDT", "EUSDT"], new HashSet<string> { "EUSDT" }, Scripted([0], []));
        var clipped = Engine(RiskConfig.Policy, ["AUSDT", "EUSDT"], new HashSet<string> { "EUSDT" }, Scripted([0], []));
        var tiny = Engine(RiskConfig.Policy with { ExperimentalPairsMaxExposure = 0.0005m }, ["AUSDT", "EUSDT"], new HashSet<string> { "EUSDT" }, Scripted([0], []));
        foreach (var engine in new[] { exact, clipped, tiny })
        {
            Replay(engine, series, until: 3);
        }

        Assert.Equal(EngineEventKind.Approved, Decisions(exact, "EUSDT").Single().Kind);
        var clip = Decisions(clipped, "EUSDT").Single();
        Assert.Equal(EngineEventKind.Clipped, clip.Kind);
        Assert.Equal("experimental-cap", clip.Detail);
        Assert.Equal(1000m, clip.After);
        var reject = Decisions(tiny, "EUSDT").Single();
        Assert.Equal(EngineEventKind.Rejected, reject.Kind);
        Assert.Equal(5m, reject.After);
        // Core limits are unchanged: the core symbol keeps its 20 % target in all three runs.
        Assert.All(new[] { exact, clipped, tiny }, e => Assert.Equal(EngineEventKind.Approved, Decisions(e, "AUSDT").Single().Kind));
        Assert.Equal(2000m, Decisions(clipped, "AUSDT").Single().After);
        Assert.Single(tiny.Positions());
    }

    [Fact]
    public void Price_drift_above_the_experimental_cap_forces_no_sale_blocks_new_experimental_entries_and_keeps_exits_free()
    {
        // EUSDT enters at bar 0 (clipped to 1000), then doubles; FUSDT signals at bar 10; EUSDT exits at bar 14.
        var series = new[]
        {
            Series("EUSDT", 30, i => i >= 4 ? 2 * DayPrice(i) : DayPrice(i)),
            Series("FUSDT", 30),
        };
        var engine = Engine(RiskConfig.Policy, ["EUSDT", "FUSDT"], new HashSet<string> { "EUSDT", "FUSDT" },
            new ScriptedStrategy(new HashSet<DateTimeOffset> { At(0), At(10) }, new HashSet<DateTimeOffset> { At(14) }, 0.9m));
        Replay(engine, series, until: 13);

        var e = Assert.Single(engine.Positions(), p => p.Symbol == "EUSDT");
        Assert.True(e.Quantity * e.LastClose > 0.10m * engine.TotalEquity, "the experimental position is above the cap after the rise");
        Assert.DoesNotContain(engine.Trades(0), t => t.Symbol == "EUSDT");
        var blocked = Decisions(engine, "FUSDT").Last();
        Assert.Equal(EngineEventKind.Rejected, blocked.Kind);
        Assert.Equal("experimental-cap", blocked.Detail);

        Replay(engine, series.Select(s => s.Where(c => c.OpenTime >= At(13)).ToList()).ToArray(), until: 17);
        Assert.Contains(engine.Trades(0), t => t.Symbol == "EUSDT" && t.Reason == ExitReason.Signal);
    }

    [Fact]
    public void Position_limit_and_asset_cap_apply_to_the_whole_portfolio_in_sleeve_order()
    {
        var symbols = Enumerable.Range(0, 4).Select(i => $"S{i}USDT").ToArray();
        var series = symbols.Select(s => Series(s, 20)).ToArray();
        var risk = RiskConfig.Policy with { Settings = RiskSettings.Policy with { MaxOpenPositions = 5 } };
        var engine = Engine(risk, symbols, null, Scripted([0], []), Scripted([0], []));
        Replay(engine, series, until: 3);

        var positions = engine.Positions().ToList();
        Assert.Equal(5, positions.Count);
        Assert.Equal(4, positions.Count(p => p.Sleeve == 0));
        Assert.Single(positions, p => p.Sleeve == 1);
        Assert.Contains(Decisions(engine, "S1USDT"), d => d is { Sleeve: 1, Kind: EngineEventKind.Rejected, Detail: "max-positions" });
        Assert.Equal(2, positions.Count(p => p.Symbol == "S0USDT"));
    }

    [Fact]
    public void Asset_cap_counts_the_holdings_of_other_sleeves()
    {
        // Sleeve 0 buys AUSDT at bar 1, which then triples; sleeve 1 signals AUSDT at bar 6.
        var series = new[] { Series("AUSDT", 20, i => i >= 4 ? 3 * DayPrice(i) : DayPrice(i)) };
        var engine = Engine(RiskConfig.Policy, ["AUSDT"], null, new Keyed("a", [("AUSDT", 0)], []), new Keyed("b", [("AUSDT", 6)], []));
        Replay(engine, series, until: 9);

        var decision = Decisions(engine, "AUSDT").Single(d => d.Sleeve == 1);
        Assert.Equal((EngineEventKind.Rejected, "asset-cap"), (decision.Kind, decision.Detail));
        Assert.Single(engine.Positions());
    }

    [Fact]
    public void Kill_switch_cancels_pending_entries_and_keeps_protective_stops()
    {
        // BUSDT enters at bar 1; AUSDT signals at bar 2 (would fill at bar 3); the kill switch is on from bar 3.
        var series = new[]
        {
            Series("BUSDT", 20, i => i == 5 ? 40m : DayPrice(i)),
            Series("AUSDT", 20),
        };
        var strategy = new Keyed("k", [("BUSDT", 0), ("AUSDT", 2)], [], 0.2m);
        var engine = Engine(RiskConfig.Policy, ["BUSDT", "AUSDT"], null, strategy);
        Replay(engine, series, t => t >= At(3) ? new StepContext(EntriesBlockedReason: "kill-switch") : StepContext.Normal, until: 8);

        Assert.Contains(engine.Events, e => e is { Kind: EngineEventKind.Canceled, Detail: "kill-switch" });
        Assert.DoesNotContain(engine.Positions(), p => p.Symbol == "AUSDT");
        Assert.Contains(engine.Trades(0), t => t.Symbol == "BUSDT" && t.Reason == ExitReason.Stop);
    }

    [Fact]
    public void Disabled_sleeve_and_manual_close_exit_at_the_next_open()
    {
        var series = new[] { Series("AUSDT", 20), Series("BUSDT", 20) };
        var engine = Engine(RiskConfig.Policy, ["AUSDT", "BUSDT"], null, Scripted([0, 6], []), Scripted([0, 6], []));
        Replay(engine, series, until: 4);
        Assert.Equal(4, engine.Positions().Count());

        var disabled = new HashSet<int> { 1 };
        Replay(engine, series.Select(s => s.Where(c => c.OpenTime >= At(4)).ToList()).ToArray(),
            t => t == At(4) ? new StepContext(DisabledSleeves: disabled, ManualCloses: new HashSet<(int, string)> { (0, "AUSDT"), (1, "BUSDT") }) : new StepContext(DisabledSleeves: disabled),
            until: 9);

        var manual = Assert.Single(engine.Trades(0));
        Assert.Equal((ExitReason.Manual, At(4)), (manual.Reason, manual.ExitTime));
        Assert.Equal(series[0].Single(c => c.OpenTime == At(4)).Open * (1 - CostModel.V1.OpenSlippage), manual.ExitPrice);
        Assert.Contains(engine.Events, e => e is { Sleeve: 1, Symbol: "BUSDT", Kind: EngineEventKind.Rejected, Detail: "sleeve-disabled" });
        Assert.Single(engine.Positions(), p => p.Sleeve == 1);
        Assert.Contains(engine.Positions(), p => p is { Sleeve: 0, Symbol: "AUSDT" });
    }

    [Fact]
    public void Stale_data_blocks_entries_but_exit_signals_and_stops_run()
    {
        var series = new[] { Series("AUSDT", 20), Series("BUSDT", 20, i => i == 6 ? 88m : DayPrice(i)) };
        var engine = Engine(RiskConfig.Policy, ["AUSDT", "BUSDT"], null,
            new Keyed("k", [("AUSDT", 0), ("BUSDT", 0), ("AUSDT", 7)], [("AUSDT", 5)], 0.2m));
        Replay(engine, series, t => t >= At(3) ? new StepContext(EntriesBlockedReason: "stale-data") : StepContext.Normal, until: 10);

        Assert.Contains(engine.Trades(0), t => t is { Symbol: "AUSDT", Reason: ExitReason.Signal });
        Assert.Contains(engine.Trades(0), t => t is { Symbol: "BUSDT", Reason: ExitReason.Stop });
        Assert.Contains(engine.Events, e => e is { Symbol: "AUSDT", Kind: EngineEventKind.Rejected, Detail: "stale-data" });
        Assert.Empty(engine.Positions());
    }

    [Fact]
    public void Cluster_cap_is_off_by_default_and_clips_a_correlated_cluster_when_enabled()
    {
        var symbols = new[] { "AUSDT", "BUSDT", "CUSDT" };
        var series = symbols.Select(s => Series(s, 20)).ToArray();
        var off = Engine(RiskConfig.Policy, symbols, null, Scripted([0], []));
        var on = Engine(RiskConfig.Policy with { ClusterCap = new ClusterCapSettings(0.7, 0.30m) }, symbols, null, Scripted([0], []));
        Replay(off, series, until: 3);
        Replay(on, series, until: 3);

        Assert.DoesNotContain(off.Events, e => e.Detail == "cluster-cap");
        Assert.Equal(3, off.Positions().Count());
        var clusterValue = on.Positions().Sum(p => p.Quantity * p.EntryPrice);
        Assert.True(clusterValue <= 0.30m * 10_000m * 1.001m);
        Assert.Contains(on.Events, e => e.Detail == "cluster-cap");
    }

    [Fact]
    public void Correlation_clusters_and_effective_count()
    {
        var start = new DateOnly(2024, 1, 1);
        var random = new Random(3);
        var common = Enumerable.Range(0, 40).Select(_ => random.NextDouble() - 0.5).ToArray();
        IReadOnlyDictionary<DateOnly, decimal> Path(Func<int, double> shock)
        {
            var closes = new Dictionary<DateOnly, decimal>();
            var price = 100.0;
            for (var d = 0; d < 40; d++)
            {
                price *= 1 + 0.02 * shock(d);
                closes[start.AddDays(d)] = (decimal)price;
            }

            return closes;
        }

        var noise = new Random(9);
        var closes = new Dictionary<string, IReadOnlyDictionary<DateOnly, decimal>>
        {
            ["A"] = Path(d => common[d]),
            ["B"] = Path(d => common[d] + 0.05 * (noise.NextDouble() - 0.5)),
            ["C"] = Path(_ => noise.NextDouble() - 0.5),
            ["D"] = new Dictionary<DateOnly, decimal> { [start] = 1m },
        };

        var clusters = CorrelationClusters.Assign(["A", "B", "C", "D"], closes, start.AddDays(39), 30, 0.7);
        Assert.Equal(clusters["A"], clusters["B"]);
        Assert.NotEqual(clusters["A"], clusters["C"]);
        Assert.Equal(3, clusters["D"]);
        Assert.Equal(1.0, CorrelationClusters.Pearson([1, 2, 3], [2, 4, 6]), 12);
        Assert.Equal(2.0, CorrelationClusters.EffectiveCount([0.5, 0.5], new double[,] { { 1, 0 }, { 0, 1 } }), 12);
        Assert.Equal(1.0, CorrelationClusters.EffectiveCount([0.5, 0.5], new double[,] { { 1, 1 }, { 1, 1 } }), 12);
    }
}

/// <summary>Operational manifest reader (ADR-0018 §3): the declared definition hash must match the definition.</summary>
public class CohortManifestReaderTests
{
    [Fact]
    public void Kohort_0_is_read_and_a_tampered_definition_is_rejected()
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, "docs", "cohorts", "cohort-0.json");
        var json = File.ReadAllText(path);
        var manifest = CohortManifestReader.Read(json);

        Assert.Equal(["ema-trend", "supertrend"], manifest.Members.Select(m => m.Name));
        Assert.Equal(20, manifest.Pairs.Count);
        Assert.Empty(manifest.ExperimentalSymbols);
        Assert.Equal(RiskSettings.Policy, manifest.Risk);
        Assert.Equal(10_000m, manifest.InitialCapital);
        Assert.Throws<FormatException>(() => CohortManifestReader.Read(json.Replace("\"period\": 50", "\"period\": 51")));
    }
}
