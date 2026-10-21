using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Live;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// One paper run per cohort (ADR-0019 §3, §4): a second cohort reaching PaperTrading gets its own run with its own
/// warm-up, the first run's event streams do not change, both runs reproduce their kernel streams across restarts,
/// and operational actions address the right run. Requires Docker.
/// </summary>
public class MultiRunTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private static readonly DateTimeOffset Now0 = new(2026, 4, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start1 = PaperTrader.NextBoundary(Now0);
    private static readonly DateTimeOffset Now1 = Now0.AddDays(10);
    private static readonly DateTimeOffset Start2 = PaperTrader.NextBoundary(Now1);
    private static readonly DateTimeOffset CandleStart = Start1.AddDays(-60);
    private static readonly DateTimeOffset End = Now0.AddDays(40);

    private static PaperTrader Trader(OpsDatabase.OpsDb db) =>
        new(new PaperStore(db.As("trading_app")), new LiveCandleSource(new LiveCandleStore(db.As("trading_app"))), CostModel.V1);

    private static async Task RunAsync(OpsDatabase.OpsDb db, DateTimeOffset from, DateTimeOffset until)
    {
        for (var now = from; now <= until; now = now.AddHours(8))
        {
            var outcome = await Trader(db).ProcessAsync(now, default);
            Assert.True(outcome.ReconciliationOk);
        }
    }

    private static PortfolioEngine Kernel(OperationalManifest manifest, Dictionary<string, IReadOnlyList<Candle>> candles, DateTimeOffset warmupStart, DateTimeOffset tradingStart, DateTimeOffset end)
    {
        var symbols = manifest.Pairs.Select(p => p.Symbol).ToList();
        var slice = candles.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Candle>)kv.Value.Where(c => c.OpenTime >= warmupStart).ToList());
        var capital = Math.Round(PaperRunConfig.NominalCapital / manifest.Members.Count, 8);
        var engine = new PortfolioEngine(new PortfolioSetup(
            manifest.Members.Select(m => new SleeveSpec(m.Name, DslStrategy.FromJson(m.DefinitionJson), capital)).ToList(),
            CostModel.V1, PaperTrader.InitialRisk(manifest).ToConfig(), symbols, CandleInterval.FourHours, tradingStart, new HashSet<string>(), RecordEvents: true));
        PortfolioReplay.Run(engine, symbols, slice, slice["BTCUSDT"], TimeSpan.FromHours(4), end, final: false);
        return engine;
    }

    private static async Task<List<EngineEvent>> EventsAsync(OpsDatabase.OpsDb db, long runId)
    {
        await using var connection = new NpgsqlConnection(db.Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_time, sleeve, symbol, kind, value, detail, before, after FROM trading.engine_events WHERE run_id = $1 ORDER BY bar_time, seq", connection);
        command.Parameters.AddWithValue(runId);
        var events = new List<EngineEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new EngineEvent(reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1), reader.GetString(2), Enum.Parse<EngineEventKind>(reader.GetString(3)),
                reader.GetDecimal(4), reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7)));
        }

        return events;
    }

    private static async Task<DateTimeOffset> LastBarAsync(OpsDatabase.OpsDb db, long runId) =>
        new(await db.ScalarAsync<DateTime>($"SELECT max(bar_time) FROM trading.steps WHERE run_id = {runId}"), TimeSpan.Zero);

    [Fact]
    public void The_warm_up_covers_the_longest_indicator_including_btc_sourced_ones()
    {
        Assert.Equal(PaperTrader.Warmup, PaperTrader.WarmupFor([DslBaselines.EmaTrend(), DslBaselines.Supertrend()]));
        // H08: BTC SMA(300) needs 301 bars (50 days 4 hours) plus one day, in whole days.
        Assert.Equal(TimeSpan.FromDays(52), PaperTrader.WarmupFor([DslStrategy.FromJson(TestCohorts.H08), DslStrategy.FromJson(TestCohorts.H04), DslStrategy.FromJson(TestCohorts.H07)]));
    }

    [Fact]
    public async Task A_second_cohort_gets_its_own_run_and_leaves_the_first_run_unchanged()
    {
        var db = await fixture.NewAsync();
        var kohort0 = await db.SeedKohort0Async(cohortId: 1);
        var kohort1 = await db.SeedCohortAsync(TestCohorts.Kohort1Json(), cohortId: 2, paper: false);
        var bars = (int)((End - CandleStart).TotalHours / 4);
        var candles = kohort0.Pairs.Select((p, i) => (p.Symbol, Series: (IReadOnlyList<Candle>)LiveSynthetic.Walk(p.Symbol, CandleStart, bars, 900 + i)))
            .ToDictionary(x => x.Symbol, x => x.Series);
        await db.AddCandlesAsync(candles.Values.SelectMany(c => c));

        // An action requested before any run exists is not applied by runs created later.
        var ops = new OpsService(db.As("ops_api"));
        await ops.ClosePositionsAsync(null, null, null, "before the runs", default);

        // Cohort 1 alone; cohort 2 is frozen but not yet in PaperTrading.
        await RunAsync(db, Now0, Now1.AddHours(-8));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.paper_runs"));
        var run1Before = await EventsAsync(db, 1);

        // Cohort 2 passes parity: its own run, with the 52-day warm-up of H08.
        foreach (var index in new[] { 1, 2, 3 })
        {
            var id = await db.ScalarAsync<long>($"SELECT id FROM research.frozen_strategies WHERE cohort_id = 2 AND member_index = {index}");
            await db.ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'Frozen', 'PaperTrading', 'test', '{\"parityReportHash\": \"x\"}')", "research_app", id);
        }

        await RunAsync(db, Now1, End);

        var runs = await new PaperStore(db.As("trading_app")).RunsAsync(default);
        Assert.Equal(2, runs.Count);
        Assert.Equal([1L, 1L], runs[0].Config.Sleeves.Select(s => s.CohortId));
        Assert.Equal(["h08-smabtc-n150-b300", "h04-macd-f12-s26-g12", "h07-volbo-n50-v2p5"], runs[1].Config.Sleeves.Select(s => s.Name));
        Assert.All(runs[1].Config.Sleeves, s => Assert.Equal(2L, s.CohortId));
        Assert.Equal(Start2, runs[1].TradingStart);
        Assert.Equal(Start2 - TimeSpan.FromDays(52), runs[1].WarmupStart);
        Assert.Equal(PaperTrader.Warmup, runs[0].TradingStart - runs[0].WarmupStart);
        var action = await db.ScalarAsync<long>("SELECT max(id) FROM trading.ops_actions");
        Assert.Equal(action, runs[0].Config.ActionsAfter);
        Assert.Equal(action, runs[1].Config.ActionsAfter);
        Assert.Equal(kohort1.Hash, runs[1].Config.ManifestHash);

        // The first run's earlier events are untouched, and each run reproduces its own kernel streams.
        Assert.Equal(EngineEvents.Canonical(run1Before, false), EngineEvents.Canonical((await EventsAsync(db, 1)).Take(run1Before.Count).ToList(), false));
        foreach (var (run, manifest) in new[] { (runs[0], kohort0), (runs[1], kohort1) })
        {
            var kernel = Kernel(manifest, candles, run.WarmupStart, run.TradingStart, (await LastBarAsync(db, run.Id)).AddHours(4));
            var stored = await EventsAsync(db, run.Id);
            Assert.Contains(stored, e => e.Kind == EngineEventKind.EntryFill);
            Assert.Equal(EngineEvents.Canonical(kernel.Events, false), EngineEvents.Canonical(stored, false));
            Assert.Equal(EngineEvents.Canonical(kernel.Events, true), EngineEvents.Canonical(stored, true));
        }

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.steps WHERE (context ->> 'closeAll')::boolean"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'paper_run_created'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.reconciliation_runs WHERE NOT ok"));
    }

    [Fact]
    public async Task A_position_close_names_its_run_once_there_are_two_runs()
    {
        var db = await fixture.NewAsync();
        await db.SeedKohort0Async(cohortId: 1);
        await db.SeedCohortAsync(TestCohorts.Kohort1Json(), cohortId: 2);
        var store = new PaperStore(db.As("trading_app"));
        foreach (var cohort in new[] { 1, 2 })
        {
            await store.CreateRunAsync(Start1, Start1 - PaperTrader.Warmup, new PaperRunConfig(
                "x", [new SleeveConfig(cohort, 1, $"s{cohort}", new string('0', 64), 5_000m)], ["BTCUSDT"], [], "4h", "BTCUSDT", "cost-model v1",
                RiskConfigDocument.From(RiskConfig.Policy)), default);
        }

        var ops = new OpsService(db.As("ops_api"));
        var missing = await Assert.ThrowsAsync<OpsRequestException>(() => ops.ClosePositionsAsync(null, 0, "BTCUSDT", "test", default));
        Assert.Contains("runId is required", missing.Message);
        await Assert.ThrowsAsync<OpsRequestException>(() => ops.ClosePositionsAsync(2, 1, "BTCUSDT", "test", default));
        await ops.ClosePositionsAsync(2, 0, "BTCUSDT", "test", default);

        var actions = await store.ActionsAfterAsync(0, default);
        Assert.Equal([new OpsAction(actions[0].Id, "close-position", 2, 0, "BTCUSDT")], actions);
    }
}
