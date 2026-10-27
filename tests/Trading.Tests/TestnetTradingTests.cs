using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// End to end on synthetic candles with a fake Testnet (m6-protocol §4–§6, §9): after the user's activation a
/// cohort gets a Testnet run beside its unchanged paper run; entries go out as IOC limits through the outbox, every
/// holding gets a protective stop on the exchange right after its fill, stops that the exchange triggers close the
/// positions in the next step, the account reconciles every cycle, no intent reaches the exchange twice and the
/// kill switch keeps the stops. Requires Docker.
/// </summary>
public class TestnetTradingTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private static async Task<List<T>> Rows<T>(OpsDatabase.OpsDb db, string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(db.Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>Paper for ten days, then both members TestnetActive and a cycle every 4 hours for <paramref name="days"/> days.</summary>
    private static async Task<(TestnetHarness H, long PaperRun, long TestnetRun, DateTimeOffset Activated, DateTimeOffset End)> RunAsync(OpsDatabase fixture, int days, Action<FakeBinance>? exchange = null)
    {
        var h = await TestnetHarness.CreateAsync(fixture, days: 10 + days + 2);
        exchange?.Invoke(h.Fake);
        for (var now = TestnetHarness.TradingStart.AddSeconds(15); now <= TestnetHarness.Now0.AddDays(10); now = now.AddHours(8))
        {
            Assert.True((await h.CycleAsync(now)).ReconciliationOk);
        }

        await h.ActivateAsync(1, 2);
        var activated = TestnetHarness.Now0.AddDays(10);
        var start = Kernel(activated);
        DateTimeOffset? previous = null;
        var end = start.AddDays(days);
        for (var now = start; now <= end; now = now.AddHours(4))
        {
            var outcome = await h.CycleAsync(now, previous);
            Assert.True(outcome.ReconciliationOk, $"reconciliation at {now:O}: {string.Join(',', outcome.Degraded)}");
            previous = now;
        }

        var runs = await Rows(h.Db, "SELECT id, venue FROM trading.paper_runs ORDER BY id", r => (r.GetInt64(0), r.GetString(1)));
        return (h, runs.Single(r => r.Item2 == "paper").Item1, runs.Single(r => r.Item2 == "testnet").Item1, activated, end);

        static DateTimeOffset Kernel(DateTimeOffset t) => PortfolioIntelligence.Execution.PaperTrader.NextBoundary(t).AddSeconds(15);
    }

    [Fact]
    public async Task A_testnet_run_trades_through_the_outbox_with_a_stop_for_every_holding_and_reconciles()
    {
        var (h, paperRun, testnetRun, activated, end) = await RunAsync(fixture, days: 24);

        // The Testnet run mirrors the paper run's sleeves; the paper run keeps trading both members (shadow, U-M6-4).
        Assert.Equal(1L, await h.Db.ScalarAsync<long>("SELECT count(*) FROM trading.testnet_baselines"));
        Assert.Equal(0L, await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.steps WHERE run_id = {paperRun} AND processed_at IS NOT NULL AND jsonb_array_length(context -> 'disabled') > 0"));
        Assert.True(await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.steps WHERE run_id = {paperRun} AND bar_time > '{activated:O}'") > 100);

        // Orders went out; entries filled on the exchange; the engine applied them in the next step.
        var orders = await Rows(h.Db, $"SELECT client_order_id, purpose, trading.order_status(id), sleeve, symbol, decision_bar FROM trading.exchange_orders WHERE run_id = {testnetRun}",
            r => (Id: r.GetString(0), Purpose: r.GetString(1), Status: r.GetString(2), Sleeve: r.GetInt32(3), Symbol: r.GetString(4), Bar: r.GetFieldValue<DateTimeOffset>(5)));
        Assert.Contains(orders, o => o.Purpose == "Entry" && o.Status == "Filled");
        Assert.Contains(orders, o => o.Purpose == "Stop" && o.Status == "Filled"); // the exchange triggered stops
        Assert.True(await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.fills WHERE run_id = {testnetRun} AND reason = 'Stop'") > 0);
        Assert.True(await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.engine_events WHERE run_id = {testnetRun} AND kind = 'EntryFill' AND detail = 'exchange'") > 0);
        // Raised (trailing) stops were replaced on the exchange with cancel-replace (m6-protocol §5.2).
        Assert.True(await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.engine_events WHERE run_id = {testnetRun} AND kind = 'StopRaised'") > 0);
        Assert.True(await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.exchange_orders WHERE run_id = {testnetRun} AND purpose = 'Stop' AND replaces IS NOT NULL") > 0);

        // No duplicate: every client order id reached the exchange at most once, and each intent has at most one order there.
        Assert.All(orders, o => Assert.True(h.Fake.Accepted(o.Id) <= 1));
        Assert.All(orders.GroupBy(o => (o.Sleeve, o.Symbol, o.Bar, o.Purpose)), g => Assert.True(g.Count(o => h.Fake.Accepted(o.Id) > 0) <= 1));
        Assert.Equal(h.Fake.Trades.Count, (int)await h.Db.ScalarAsync<long>("SELECT count(*) FROM trading.exchange_fills"));
        Assert.Equal(0L, await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.reconciliation_runs WHERE run_id = {testnetRun} AND NOT ok"));
        Assert.Equal(0L, await h.Db.ScalarAsync<long>("SELECT count(*) FROM trading.exchange_orders o WHERE trading.order_status(o.id) IN ('Unknown', 'Submitting')"));

        // Every holding on the exchange has one accepted stop at the engine's level (rounded up to the tick).
        var held = await h.Executor.HeldAsync(testnetRun, null, default);
        var positions = JsonDocument.Parse(await h.Db.ScalarAsync<string>($"""
            SELECT coalesce(jsonb_agg(p.value || jsonb_build_object('sleeve', b.sleeve)), '[]')::text FROM trading.balance_snapshots b, jsonb_array_elements(b.positions) p
            WHERE b.run_id = {testnetRun} AND b.bar_time = (SELECT max(bar_time) FROM trading.steps WHERE run_id = {testnetRun})
            """)).RootElement.EnumerateArray().ToList();
        var stops = await Rows(h.Db, $"SELECT sleeve, symbol, stop_price, quantity FROM trading.exchange_orders o WHERE run_id = {testnetRun} AND purpose = 'Stop' AND trading.order_status(o.id) = 'Accepted'",
            r => (Sleeve: r.GetInt32(0), Symbol: r.GetString(1), Stop: r.GetDecimal(2), Quantity: r.GetDecimal(3)));
        Assert.NotEmpty(positions);
        foreach (var p in positions)
        {
            var (sleeve, symbol) = (p.GetProperty("sleeve").GetInt32(), p.GetProperty("symbol").GetString()!);
            var stop = Assert.Single(stops, s => s.Sleeve == sleeve && s.Symbol == symbol);
            Assert.Equal(ExchangeFilters.RoundUp(p.GetProperty("stop").GetDecimal(), h.Fake.TickSize), stop.Stop);
            Assert.Equal(h.Executor.Tradable(symbol, held[(sleeve, symbol)], market: false), stop.Quantity);
        }

        Assert.Equal(positions.Count, stops.Count);
        var testnet = JsonDocument.Parse(await new DashboardQueries(h.Db.As("ops_api")).TestnetAsync(true, default)).RootElement;
        Assert.Equal(0, testnet.GetProperty("unprotected").GetInt32());
        Assert.Equal(positions.Count, testnet.GetProperty("positions").GetArrayLength());
        Assert.True(testnet.GetProperty("deviations").GetProperty("stop_fills_below_level").GetInt64() >= 0);

        // Kill switch (D11 rev.): no new entry order goes out, every protective stop stays on the exchange.
        await new OpsService(h.Db.As("ops_api")).SetKillSwitchAsync(true, "drill", default);
        var entriesBefore = orders.Count(o => o.Purpose == "Entry");
        var acceptedStops = h.Fake.Orders.Count(o => o.Type == "STOP_LOSS" && o.Status == "NEW");
        var now = end.AddHours(4);
        await h.CycleAsync(now, end, trigger: false);
        await h.CycleAsync(now.AddHours(4), now, trigger: false);
        Assert.Equal(entriesBefore, await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.exchange_orders WHERE run_id = {testnetRun} AND purpose = 'Entry'"));
        Assert.Equal(acceptedStops, h.Fake.Orders.Count(o => o.Type == "STOP_LOSS" && o.Status == "NEW"));
    }

    [Fact]
    public async Task Partly_filled_entries_get_a_stop_for_the_filled_quantity()
    {
        var (h, _, testnetRun, _, _) = await RunAsync(fixture, days: 10, exchange: fake => fake.FillFraction = 0.5m);

        var partial = await h.Db.ScalarAsync<long>($"SELECT count(*) FROM trading.exchange_orders o WHERE run_id = {testnetRun} AND purpose = 'Entry' AND trading.order_status(o.id) = 'Expired' AND EXISTS (SELECT 1 FROM trading.exchange_fills f WHERE f.order_id = o.id)");
        Assert.True(partial > 0);
        Assert.Equal(partial, await h.Db.ScalarAsync<long>("SELECT partial_fills FROM (" + TestnetDeviations.Sql + ") d"));
        var held = await h.Executor.HeldAsync(testnetRun, null, default);
        var stops = await Rows(h.Db, $"SELECT sleeve, symbol, quantity FROM trading.exchange_orders o WHERE run_id = {testnetRun} AND purpose = 'Stop' AND trading.order_status(o.id) = 'Accepted'",
            r => (r.GetInt32(0), r.GetString(1), r.GetDecimal(2)));
        Assert.NotEmpty(stops);
        Assert.All(stops, s => Assert.Equal(h.Executor.Tradable(s.Item2, held[(s.Item1, s.Item2)], market: false), s.Item3));
    }

    [Fact]
    public async Task A_rejected_stop_leaves_the_position_unprotected_blocks_entries_and_is_placed_again()
    {
        var h = await TestnetHarness.CreateAsync(fixture, days: 45);
        for (var now = TestnetHarness.TradingStart.AddSeconds(15); now <= TestnetHarness.Now0.AddDays(10); now = now.AddHours(8))
        {
            await h.CycleAsync(now);
        }

        await h.ActivateAsync(1, 2);
        foreach (var symbol in h.Manifest.Pairs.Select(p => p.Symbol))
        {
            h.Fake.RejectStops.Add(symbol);
        }

        DateTimeOffset? previous = null;
        var t = PortfolioIntelligence.Execution.PaperTrader.NextBoundary(TestnetHarness.Now0.AddDays(10)).AddSeconds(15);
        CycleOutcome outcome;
        do
        {
            outcome = await h.CycleAsync(t, previous, trigger: false);
            previous = t;
            t = t.AddHours(4);
        }
        while (!outcome.Degraded.Contains("unprotected-stop") && t < TestnetHarness.Now0.AddDays(40));

        Assert.Contains("unprotected-stop", outcome.Degraded);
        Assert.True(await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'protective_stop_failed'") > 0);
        Assert.True(await h.Db.ScalarAsync<long>("SELECT count(*) FROM trading.exchange_orders o WHERE purpose = 'Stop' AND trading.order_status(o.id) = 'ExchangeRejected'") > 0);
        var lastEntry = await h.Db.ScalarAsync<long>("SELECT count(*) FROM trading.exchange_orders WHERE purpose = 'Entry'");

        // The next step records the unprotected positions and blocks new entries ("unprotected").
        outcome = await h.CycleAsync(t, previous, trigger: false);
        Assert.Contains("unprotected-stop", outcome.Degraded);
        Assert.True(await h.Db.ScalarAsync<long>("""
            SELECT count(*) FROM trading.steps s JOIN trading.paper_runs r ON r.id = s.run_id
            WHERE r.venue = 'testnet' AND s.context ->> 'blocked' = 'unprotected' AND jsonb_array_length(s.context -> 'external' -> 'unprotected') > 0
            """) > 0);
        Assert.Equal(lastEntry, await h.Db.ScalarAsync<long>("SELECT count(*) FROM trading.exchange_orders WHERE purpose = 'Entry'"));

        // The exchange accepts stops again: the next cycle places them, the positions are protected.
        h.Fake.RejectStops.Clear();
        outcome = await h.CycleAsync(t.AddHours(1), t, trigger: false);
        Assert.DoesNotContain("unprotected-stop", outcome.Degraded);
        Assert.Equal(0, JsonDocument.Parse(await new DashboardQueries(h.Db.As("ops_api")).TestnetAsync(true, default)).RootElement.GetProperty("unprotected").GetInt32());
    }

    [Fact]
    public async Task A_testnet_reset_is_detected_and_the_users_rebaseline_starts_a_new_run_and_baseline()
    {
        var (h, _, oldRun, _, end) = await RunAsync(fixture, days: 8);

        h.Fake.Reset(usdt: 100_000m); // about monthly, without notice (S-B19)
        var outcome = await h.CycleAsync(end.AddHours(4), end, trigger: false);
        Assert.Contains("testnet-reset-suspected", outcome.Degraded);
        Assert.False(outcome.ReconciliationOk);
        Assert.Equal("testnet-reset-suspected", await h.Db.ScalarAsync<string>($"SELECT detail ->> 'reason' FROM trading.reconciliation_runs WHERE run_id = {oldRun} ORDER BY id DESC LIMIT 1"));
        Assert.Equal(1L, await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'testnet_reconciliation_failed'"));

        await new OpsService(h.Db.As("ops_api")).RebaselineTestnetAsync("Testnet reset", confirm: true, default);
        outcome = await h.CycleAsync(end.AddHours(8), end.AddHours(4), trigger: false);

        Assert.True(outcome.ReconciliationOk, string.Join(',', outcome.Degraded));
        Assert.Equal($"rebaseline:{await h.Db.ScalarAsync<long>("SELECT id FROM trading.ops_actions WHERE action = 'testnet-rebaseline'")}",
            await h.Db.ScalarAsync<string>($"SELECT reason FROM trading.run_closures WHERE run_id = {oldRun}"));
        var newRun = await h.Db.ScalarAsync<long>("SELECT max(id) FROM trading.paper_runs WHERE venue = 'testnet'");
        Assert.NotEqual(oldRun, newRun);
        Assert.StartsWith("rebaseline:", await h.Db.ScalarAsync<string>("SELECT source FROM trading.testnet_baselines ORDER BY id DESC LIMIT 1"));
        Assert.Equal(1L, await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'testnet_runs_closed'"));

        // HTTP 418 from the exchange engages the kill switch like the market data ban (risk-model §3.1).
        h.Fake.FaultOnce("GET", "/api/v3/time", Fault.IpBanned);
        var store = new PaperStore(h.Db.As("trading_app"));
        var cycle = new TradingCycle(store, h.Trader(), null, new TradingHealth(), h.Time);
        var banned = await cycle.RunAsync(end.AddHours(9), default);
        Assert.Contains("kill-switch", banned.Degraded);
        Assert.True(await store.KillSwitchEngagedAsync(default));
        Assert.Equal(1L, await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'exchange_ip_banned'"));
    }

    [Fact]
    public async Task Without_stop_loss_the_fallback_is_a_banded_stop_loss_limit()
    {
        var h = await TestnetHarness.CreateAsync(fixture, days: 40);
        foreach (var symbol in h.Manifest.Pairs.Select(p => p.Symbol))
        {
            h.Fake.NoStopLoss.Add(symbol);
        }

        for (var now = TestnetHarness.TradingStart.AddSeconds(15); now <= TestnetHarness.Now0.AddDays(10); now = now.AddHours(8))
        {
            await h.CycleAsync(now);
        }

        await h.ActivateAsync(1, 2);
        DateTimeOffset? previous = null;
        for (var t = PortfolioIntelligence.Execution.PaperTrader.NextBoundary(TestnetHarness.Now0.AddDays(10)).AddSeconds(15); t < TestnetHarness.Now0.AddDays(30); t = t.AddHours(4))
        {
            Assert.True((await h.CycleAsync(t, previous)).ReconciliationOk);
            previous = t;
        }

        var stops = await Rows(h.Db, "SELECT order_type, price, stop_price, time_in_force FROM trading.exchange_orders WHERE purpose = 'Stop'",
            r => (Type: r.GetString(0), Price: r.GetDecimal(1), Stop: r.GetDecimal(2), Tif: r.GetString(3)));
        Assert.NotEmpty(stops);
        Assert.All(stops, s =>
        {
            Assert.Equal("StopLossLimit", s.Type);
            Assert.Equal("GTC", s.Tif);
            Assert.True(s.Price <= s.Stop * (1 - TestnetExecutor.StopLimitBand) && s.Price > s.Stop * (1 - TestnetExecutor.StopLimitBand) - 2 * h.Fake.TickSize);
        });
        Assert.True(await h.Db.ScalarAsync<long>("SELECT stop_limit_fallback FROM (" + TestnetDeviations.Sql + ") d") > 0);
    }
}
