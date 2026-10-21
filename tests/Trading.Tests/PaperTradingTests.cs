using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Live;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// Paper trading on synthetic live candles (m5-protocol §2, §5, §8): L1/L2 parity with the kernel across restarts,
/// reconciliation, stale data, operational actions (D11, D23, V9) and the strategy hash guard (V14). Requires Docker.
/// </summary>
public class PaperTradingTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private static readonly DateTimeOffset Now0 = new(2026, 3, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TradingStart = PaperTrader.NextBoundary(Now0);
    private static readonly DateTimeOffset WarmupStart = TradingStart - PaperTrader.Warmup;
    private const int Bars = 45 * 6 + 40 * 6;

    private static DateTimeOffset Bar(int index) => WarmupStart.AddHours(4 * index);

    private static PaperTrader Trader(OpsDatabase.OpsDb db) =>
        new(new PaperStore(db.As("trading_app")), new LiveCandleSource(new LiveCandleStore(db.As("trading_app"))), CostModel.V1);

    private async Task<(OpsDatabase.OpsDb Db, OperationalManifest Manifest, Dictionary<string, IReadOnlyList<Candle>> Candles)> SetupAsync()
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async();
        var candles = manifest.Pairs.Select((p, i) => (p.Symbol, Series: (IReadOnlyList<Candle>)LiveSynthetic.Walk(p.Symbol, WarmupStart, Bars, 500 + i)))
            .ToDictionary(x => x.Symbol, x => x.Series);
        await db.AddCandlesAsync(candles.Values.SelectMany(c => c));
        return (db, manifest, candles);
    }

    /// <summary>
    /// Uninterrupted operation: a cycle every 8 hours (the oldest new bar is never more than 2 bars late), each by a
    /// new trader, i.e. a restart that rebuilds from the step log and reconciles first.
    /// </summary>
    private static async Task RunFreshAsync(OpsDatabase.OpsDb db, DateTimeOffset until)
    {
        for (var now = TradingStart.AddSeconds(15); now <= until; now = now.AddHours(8))
        {
            var outcome = await Trader(db).ProcessAsync(now, default);
            Assert.True(outcome.ReconciliationOk);
            Assert.False(outcome.Stale);
        }
    }

    private static PortfolioEngine Kernel(OperationalManifest manifest, Dictionary<string, IReadOnlyList<Candle>> candles, DateTimeOffset end)
    {
        var symbols = manifest.Pairs.Select(p => p.Symbol).ToList();
        var engine = new PortfolioEngine(new PortfolioSetup(
            manifest.Members.Select(m => new SleeveSpec(m.Name, DslStrategy.FromJson(m.DefinitionJson), 5_000m)).ToList(),
            CostModel.V1, PaperTrader.InitialRisk(manifest).ToConfig(), symbols, CandleInterval.FourHours, TradingStart, new HashSet<string>(), RecordEvents: true));
        PortfolioReplay.Run(engine, symbols, candles, candles["BTCUSDT"], TimeSpan.FromHours(4), end, final: false);
        return engine;
    }

    private static async Task<List<EngineEvent>> StoredEventsAsync(OpsDatabase.OpsDb db)
    {
        await using var connection = new NpgsqlConnection(db.Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT event_time, sleeve, symbol, kind, value, detail, before, after FROM trading.engine_events ORDER BY bar_time, seq", connection);
        var events = new List<EngineEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new EngineEvent(reader.GetFieldValue<DateTimeOffset>(0), reader.GetInt32(1), reader.GetString(2), Enum.Parse<EngineEventKind>(reader.GetString(3)),
                reader.GetDecimal(4), reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7)));
        }

        return events;
    }

    [Fact]
    public async Task Paper_trading_reproduces_the_kernel_event_streams_across_restarts()
    {
        var (db, manifest, candles) = await SetupAsync();

        // A new trader per call is a restart: it rebuilds from the step log and reconciles before going on.
        var first = await Trader(db).ProcessAsync(Now0, default);
        Assert.True(first.ReconciliationOk);
        Assert.True(first.BarsProcessed > 200);
        await RunFreshAsync(db, Now0.AddDays(25));

        var lastBar = await db.ScalarAsync<DateTime>("SELECT max(bar_time) FROM trading.steps");
        var kernel = Kernel(manifest, candles, new DateTimeOffset(lastBar, TimeSpan.Zero).AddHours(4));
        var stored = await StoredEventsAsync(db);

        Assert.Contains(stored, e => e.Kind == EngineEventKind.EntryFill);
        Assert.Contains(stored, e => e.Kind == EngineEventKind.ExitFill);
        Assert.Equal(EngineEvents.Canonical(kernel.Events, false), EngineEvents.Canonical(stored, false));
        Assert.Equal(EngineEvents.Canonical(kernel.Events, true), EngineEvents.Canonical(stored, true));
        Assert.Equal(kernel.StateHash(), await db.ScalarAsync<string>("SELECT state_hash FROM trading.steps ORDER BY bar_time DESC LIMIT 1"));
        Assert.Equal(kernel.Fills.Count, await db.ScalarAsync<long>("SELECT count(*) FROM trading.fills"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM (SELECT 1 FROM trading.fills GROUP BY run_id, sleeve, symbol, fill_time, side HAVING count(*) > 1) d"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.reconciliation_runs WHERE NOT ok"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.paper_runs"));
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM trading.correlation_snapshots") > 0);
        var ledger = await new PaperStore(db.As("trading_app")).LedgerAsync(1, default);
        foreach (var sleeve in new[] { 0, 1 })
        {
            Assert.True(Math.Abs(kernel.Cash(sleeve) - (5_000m + ledger.GetValueOrDefault(sleeve).Cash)) <= PaperTrader.LedgerTolerance);
        }
    }

    [Fact]
    public async Task Late_bars_after_an_outage_are_processed_in_order_with_entries_blocked()
    {
        var (db, _, _) = await SetupAsync();
        await Trader(db).ProcessAsync(Now0, default);

        // The machine was off for ten days: the missed bars are processed in order; those older than 2 bars are stale.
        var outcome = await Trader(db).ProcessAsync(Now0.AddDays(10), default);

        Assert.True(outcome.Stale);
        Assert.True(outcome.CatchUp);
        Assert.Contains("stale-data", outcome.Degraded);
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM trading.steps WHERE context ->> 'blocked' = 'stale-data'") > 50);
        Assert.Equal(0L, await db.ScalarAsync<long>("""
            SELECT count(*) FROM trading.engine_events e JOIN trading.steps s USING (run_id, bar_time)
            WHERE e.kind IN ('Approved', 'Clipped') AND s.context ->> 'blocked' = 'stale-data'
            """));
        var fresh = await Trader(db).ProcessAsync(Now0.AddDays(10).AddHours(4), default);
        Assert.False(fresh.Stale);
    }

    [Fact]
    public async Task Kill_switch_disable_and_close_all_are_operational_and_audited_and_touch_no_cohort_record()
    {
        var (db, _, _) = await SetupAsync();
        var ops = new OpsService(db.As("ops_api"));
        await Trader(db).ProcessAsync(Now0.AddDays(12), default);
        var cohortStatus = await db.ScalarAsync<long>("SELECT count(*) FROM research.cohort_status");

        // Kill switch (D11): no new entries; protective stops and exits keep running. Only the user releases it.
        await ops.SetKillSwitchAsync(true, "test", default);
        await Trader(db).ProcessAsync(Now0.AddDays(15), default);
        Assert.Equal(0L, await db.ScalarAsync<long>("""
            SELECT count(*) FROM trading.engine_events e JOIN trading.steps s USING (run_id, bar_time)
            WHERE e.kind = 'EntryFill' AND s.context ->> 'blocked' = 'kill-switch'
            """));
        var release = await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("INSERT INTO trading.kill_switch_events (engaged, actor, reason) VALUES (false, 'system', 'x')", "trading_app"));
        Assert.Contains("released only by the user", release.MessageText);
        await ops.SetKillSwitchAsync(false, "test over", default);

        // Disable supertrend, then close everything at the next open (t+1 rule, reason Manual).
        await ops.DisableStrategyAsync("supertrend", "test", default);
        await Trader(db).ProcessAsync(Now0.AddDays(15).AddHours(4), default);
        const string HeldNow = "SELECT coalesce(sum(jsonb_array_length(positions)), 0)::bigint FROM trading.balance_snapshots WHERE bar_time = (SELECT max(bar_time) FROM trading.steps)";
        var held = await db.ScalarAsync<long>(HeldNow);
        await ops.ClosePositionsAsync(null, null, null, "test", default);
        await Trader(db).ProcessAsync(Now0.AddDays(15).AddHours(8), default);

        Assert.Equal(0L, await db.ScalarAsync<long>(HeldNow));
        Assert.Equal(held > 0, await db.ScalarAsync<long>("SELECT count(*) FROM trading.fills WHERE reason = 'Manual'") > 0);
        Assert.Equal(0L, await db.ScalarAsync<long>("""
            SELECT count(*) FROM trading.fills f JOIN trading.steps s USING (run_id, bar_time)
            WHERE f.sleeve = 1 AND f.side = 'buy' AND (s.context -> 'disabled') @> '[1]'
            """));
        foreach (var e in new[] { "trading_kill_switch_engaged", "trading_kill_switch_released", "strategy_disabled", "close_all_requested" })
        {
            Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM audit.events WHERE event = '{e}'"));
        }

        // V9: operational actions never write cohort records, and these roles cannot.
        Assert.Equal(cohortStatus, await db.ScalarAsync<long>("SELECT count(*) FROM research.cohort_status"));
        foreach (var role in new[] { "ops_api", "trading_app" })
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("INSERT INTO research.cohort_status (cohort_id, status) VALUES (1, 'sealed')", role));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
    }

    [Fact]
    public async Task A_definition_that_does_not_match_the_manifest_hash_is_not_run_and_raises_an_alarm()
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async(cohortId: 1);
        // Cohort 2 claims member 1 (ema-trend in the manifest) but points at the supertrend definition.
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cohort-0.json"));
        await db.ExecuteAsync("""
            INSERT INTO research.frozen_strategies (cohort_id, manifest_hash, member_index, name, definition_hash, manifest)
            VALUES (2, $1, 1, 'ema-trend', $2, $3)
            """, "research_app", manifest.Hash, manifest.Members[1].DefinitionHash, json);
        var id = await db.ScalarAsync<long>("SELECT id FROM research.frozen_strategies WHERE cohort_id = 2");
        await db.ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'Candidate', 'Frozen', 't', '{}')", "research_app", id);
        await db.ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'Frozen', 'PaperTrading', 't', '{\"parityReportHash\": \"x\"}')", "research_app", id);

        var (members, rejected) = await Trader(db).LoadMembersAsync(r => r.State == "PaperTrading", default);

        Assert.Equal(["ema-trend", "supertrend"], members.Select(m => m.Record.Name));
        Assert.All(members, m => Assert.Equal(1, m.Record.CohortId));
        Assert.Equal(["ema-trend"], rejected);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'strategy_hash_mismatch'"));
    }

    [Fact]
    public async Task A_ledger_that_does_not_reconcile_blocks_new_entries()
    {
        var (db, _, _) = await SetupAsync();
        await Trader(db).ProcessAsync(Now0, default);
        await RunFreshAsync(db, Now0.AddDays(20));
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM trading.fills") > 0);

        // A foreign fill appears in the books.
        await db.ExecuteAsync("""
            WITH f AS (
              INSERT INTO trading.fills (run_id, bar_time, sleeve, strategy, cohort_id, symbol, side, fill_time, price, quantity, fee, reason)
              SELECT run_id, bar_time, 0, 'ema-trend', 1, 'BTCUSDT', 'sell', bar_time - interval '1 second', 1, 1, 0, 'Foreign'
              FROM trading.steps ORDER BY bar_time DESC LIMIT 1 RETURNING id, run_id)
            INSERT INTO trading.ledger_entries (run_id, fill_id, sleeve, kind, cash_delta) SELECT run_id, id, 0, 'trade', 1 FROM f
            """, "trading_app");
        var outcome = await Trader(db).ProcessAsync(Now0.AddDays(22), default);

        Assert.False(outcome.ReconciliationOk);
        Assert.Contains("reconciliation", outcome.Degraded);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.reconciliation_runs WHERE NOT ok AND detail ->> 'reason' = 'ledger'"));
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM trading.steps WHERE context ->> 'blocked' = 'reconciliation'") > 0);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("""
            INSERT INTO trading.fills (run_id, bar_time, sleeve, strategy, cohort_id, symbol, side, fill_time, price, quantity, fee, reason)
            SELECT run_id, bar_time, sleeve, strategy, cohort_id, symbol, side, fill_time, price, quantity, fee, reason FROM trading.fills LIMIT 1
            """, "trading_app"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }
}
