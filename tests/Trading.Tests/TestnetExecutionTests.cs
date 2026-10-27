using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// The outbox under fault injection (m6-protocol §3, §5, §9): timeouts, 5xx, -1007, broken connections, late
/// visibility and restarts never produce a second order for an intent; an order the exchange never received becomes
/// NeverAccepted only after timestamp + recvWindow + 60 s and two "does not exist" answers; filter violations send
/// nothing; the kill switch cancels entries and keeps stops; an exit after a filled stop sells nothing; the account
/// reconciliation catches unknown orders, balance drift and a Testnet reset. Requires Docker.
/// </summary>
public class TestnetExecutionTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private static readonly DateTimeOffset Decision = new(2026, 4, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextBar = Decision.AddHours(4);

    private async Task<(TestnetHarness H, PaperRun Run)> SetupAsync()
    {
        var h = await TestnetHarness.CreateAsync(fixture, days: 40);
        h.Time.Now = NextBar.AddSeconds(15);
        var config = new PaperRunConfig(h.Manifest.Hash, [new SleeveConfig(1, 1, "ema-trend", "x", 5_000m), new SleeveConfig(1, 2, "supertrend", "y", 5_000m)],
            h.Manifest.Pairs.Select(p => p.Symbol).ToList(), [], "4h", "BTCUSDT", "cost-model v1", RiskConfigDocument.From(RiskConfig.Policy), Venue: PaperRunConfig.TestnetVenue);
        var run = await new PaperStore(h.Db.As("trading_app")).CreateRunAsync(NextBar, NextBar.AddDays(-45), config, default);
        await h.Executor.PrepareAsync(config.Symbols, default);
        await h.Executor.BaselineAsync(run.Id, $"run-start:{run.Id}", await h.Executor.AccountAsync(default), default);
        return (h, run);
    }

    private static TestnetExecutor.OrderDraftSpec Entry(TestnetHarness h, string symbol = "BTCUSDT", decimal notional = 500m)
    {
        var reference = h.ReferencePrice(symbol);
        var limit = reference * (1 + TestnetExecutor.EntryBand);
        return new TestnetExecutor.OrderDraftSpec(symbol, OrderSide.Buy, OrderType.Limit, notional / limit, limit, null, "IOC", reference);
    }

    private static TestnetExecutor.OrderDraftSpec Stop(TestnetHarness h, decimal quantity, string symbol = "BTCUSDT") =>
        new(symbol, OrderSide.Sell, OrderType.StopLoss, quantity, null, h.ReferencePrice(symbol) * 0.9m, null, h.ReferencePrice(symbol));

    [Theory]
    [InlineData(Fault.TimeoutAfterApply, true)]
    [InlineData(Fault.ServerErrorAfterApply, true)]
    [InlineData(Fault.BackendTimeoutAfterApply, true)]
    [InlineData(Fault.HiddenAfterApply, true)]
    [InlineData(Fault.TimeoutBeforeApply, false)]
    [InlineData(Fault.ConnectionErrorBeforeApply, false)]
    public async Task An_unknown_order_is_resolved_by_its_id_and_never_duplicated(Fault fault, bool applied)
    {
        var (h, run) = await SetupAsync();
        h.Fake.FaultOnce("POST", "/api/v3/order", fault);

        var order = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h), NextBar, null, default);
        Assert.Equal(OrderStatus.Unknown, order.Status);

        // While Unknown, the intent cannot get a second order: the database refuses it (duplicate guard of migration 0009).
        var second = await Assert.ThrowsAsync<PostgresException>(() => h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h), NextBar, null, default));
        Assert.Contains("duplicate", second.MessageText);

        // Maintenance before the horizon: a hidden or missing order stays Unknown (never NeverAccepted early).
        h.Time.Now += TimeSpan.FromSeconds(20);
        await h.Executor.MaintainAsync(run, NextBar, false, default);
        await h.Executor.MaintainAsync(run, NextBar, false, default);
        var early = await h.Store.OrderAsync(order.ClientOrderId, default);
        Assert.Equal(applied && fault != Fault.HiddenAfterApply ? OrderStatus.Filled : OrderStatus.Unknown, early.Status);

        // After timestamp + recvWindow + 60 s: found (applied) or, after two misses, NeverAccepted.
        h.Time.Now += TimeSpan.FromSeconds(70);
        await h.Executor.MaintainAsync(run, NextBar, false, default);
        await h.Executor.MaintainAsync(run, NextBar, false, default);
        var resolved = await h.Store.OrderAsync(order.ClientOrderId, default);
        Assert.Equal(applied ? OrderStatus.Filled : OrderStatus.NeverAccepted, resolved.Status);

        if (!applied)
        {
            // Only now may the intent go out again, with the next attempt (a new id).
            var retry = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h), NextBar, null, default);
            Assert.Equal(2, retry.Attempt);
            Assert.NotEqual(order.ClientOrderId, retry.ClientOrderId);
            Assert.Equal(OrderStatus.Filled, retry.Status);
        }

        Assert.Single(h.Fake.Orders); // exactly one order reached the exchange for the intent
        Assert.Equal(h.Fake.Trades.Count, (int)await h.Db.ScalarAsync<long>("SELECT count(*) FROM trading.exchange_fills"));
        Assert.Equal(NextBar, await h.Db.ScalarAsync<DateTime>("SELECT max(step_bar) FROM trading.exchange_fills"));
        var check = await h.Executor.ReconcileAsync([run], default);
        Assert.True(check.Ok, check.Reason);
    }

    [Fact]
    public async Task A_restart_in_submitting_resolves_the_order_without_sending_it_again()
    {
        var (h, run) = await SetupAsync();

        // Crash after the outbox row, before the request: nothing reached the exchange.
        var lost = ClientOrderIds.For(run.Id, 0, "BTCUSDT", Decision, OrderPurpose.Entry, 1);
        await h.Store.CreateAsync(run.Id, 0, OrderPurpose.Entry, Decision, 1, new OrderDraft("BTCUSDT", lost, OrderSide.Buy, OrderType.Limit, 0.01m, 101m, null, "IOC"),
            OrderStatus.Submitting, h.Clock.Now, null, new { }, default);
        // Crash after the request, before its answer was stored: the exchange has the order.
        var sent = ClientOrderIds.For(run.Id, 1, "ETHUSDT", Decision, OrderPurpose.Entry, 1);
        var request = new OrderRequest("ETHUSDT", sent, OrderSide.Buy, OrderType.Limit, 1m, h.ReferencePrice("ETHUSDT") * 1.01m, null, "IOC");
        await h.Store.CreateAsync(run.Id, 1, OrderPurpose.Entry, Decision, 1, new OrderDraft("ETHUSDT", sent, OrderSide.Buy, OrderType.Limit, 1m, request.Price, null, "IOC"),
            OrderStatus.Submitting, h.Clock.Now, null, new { }, default);
        await h.Adapter.PlaceAsync(request, default);

        // A new executor after the restart (same store, same exchange).
        var paper = new PaperStore(h.Db.As("trading_app"));
        var restarted = new TestnetExecutor(h.Adapter, h.Store, h.Clock, (e, p, ct) => paper.AuditAsync("trading", e, p, ct));
        await restarted.PrepareAsync(run.Config.Symbols, default);
        Assert.Equal(1, await restarted.MaintainAsync(run, NextBar, false, default));
        Assert.Equal(OrderStatus.Unknown, (await h.Store.OrderAsync(lost, default)).Status);
        Assert.Equal(OrderStatus.Filled, (await h.Store.OrderAsync(sent, default)).Status);

        h.Time.Now += TimeSpan.FromMinutes(2);
        await restarted.MaintainAsync(run, NextBar, false, default);
        await restarted.MaintainAsync(run, NextBar, false, default);
        Assert.Equal(OrderStatus.NeverAccepted, (await h.Store.OrderAsync(lost, default)).Status);
        Assert.Equal(1, h.Fake.Accepted(sent));
        Assert.Equal(0, h.Fake.Accepted(lost));
        Assert.Equal(1L, await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'exchange_order_never_accepted'"));
    }

    [Fact]
    public async Task Rejections_are_terminal_local_filters_send_nothing_and_clock_skew_resyncs()
    {
        var (h, run) = await SetupAsync();
        var posts = () => h.Fake.Log.Count(l => l == "POST /api/v3/order");

        var tiny = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h, notional: 1m), NextBar, null, default);
        Assert.Equal(OrderStatus.LocalRejected, tiny.Status);
        Assert.Equal("filter:NOTIONAL", tiny.Reason);
        Assert.Equal(0, posts());

        h.Fake.FaultOnce("POST", "/api/v3/order", Fault.ClockSkew);
        var times = h.Fake.Log.Count(l => l == "GET /api/v3/time");
        var skewed = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision.AddHours(4), Entry(h), NextBar, null, default);
        Assert.Equal(OrderStatus.ExchangeRejected, skewed.Status);
        Assert.Equal("exchange-rejected:-1021", skewed.Reason);
        Assert.Equal(times + 1, h.Fake.Log.Count(l => l == "GET /api/v3/time"));

        h.Fake.FaultOnce("POST", "/api/v3/order", Fault.RateLimited);
        var limited = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision.AddHours(8), Entry(h), NextBar, null, default);
        Assert.Equal("exchange-rejected:429", limited.Reason);

        Assert.True(TestnetExecutor.Retryable(skewed) && TestnetExecutor.Retryable(limited)); // never reached the engine: may go out again
        Assert.False(TestnetExecutor.Retryable(tiny));

        h.Fake.FaultOnce("POST", "/api/v3/order", Fault.IpBanned);
        await Assert.ThrowsAsync<ExchangeIpBannedException>(() => h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision.AddHours(12), Entry(h), NextBar, null, default));
        Assert.Empty(h.Fake.Orders);
    }

    [Fact]
    public async Task The_kill_switch_cancels_open_entries_and_leaves_protective_stops()
    {
        var (h, run) = await SetupAsync();
        var bought = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h), NextBar, null, default);
        Assert.Equal(OrderStatus.Filled, bought.Status);
        var stop = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Stop, Decision, Stop(h, bought.ExecutedQuantity), NextBar, null, default);
        Assert.Equal(OrderStatus.Accepted, stop.Status);
        h.Fake.RestingIoc.Add("ETHUSDT"); // an entry that stays open on the exchange
        var open = await h.Executor.PlaceAsync(run, 1, OrderPurpose.Entry, Decision, Entry(h, "ETHUSDT"), NextBar, null, default);
        Assert.Equal(OrderStatus.Accepted, open.Status);

        await h.Executor.MaintainAsync(run, NextBar, killSwitch: true, default);

        Assert.Equal(OrderStatus.Canceled, (await h.Store.OrderAsync(open.ClientOrderId, default)).Status);
        Assert.Equal(OrderStatus.Accepted, (await h.Store.OrderAsync(stop.ClientOrderId, default)).Status);
        Assert.Equal("NEW", h.Fake.Orders.Single(o => o.ClientOrderId == stop.ClientOrderId).Status);
        Assert.Equal(1L, await h.Db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'testnet_entries_cancelled'"));
        Assert.True((await h.Executor.ReconcileAsync([run], default)).Ok);
    }

    [Fact]
    public async Task An_exit_after_the_stop_filled_sells_nothing_and_a_partial_exit_keeps_the_rest()
    {
        var (h, run) = await SetupAsync();
        var bought = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h), NextBar, null, default);
        var stop = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Stop, Decision, Stop(h, bought.ExecutedQuantity), NextBar, null, default);
        h.Fake.TriggerStops("BTCUSDT", 0.01m, 1_000_000m, h.Time.Now); // the exchange stop sells first (race)

        await h.Executor.ExitAsync(run, 0, "BTCUSDT", OrderPurpose.Exit, Decision.AddHours(4), NextBar.AddHours(4), default);

        Assert.Equal(OrderStatus.Filled, (await h.Store.OrderAsync(stop.ClientOrderId, default)).Status);
        Assert.DoesNotContain(h.Fake.Orders, o => o.Type == "MARKET");
        Assert.Equal(0m, (await h.Executor.HeldAsync(run.Id, null, default)).GetValueOrDefault((0, "BTCUSDT")));
        Assert.True((await h.Executor.ReconcileAsync([run], default)).Ok);

        // Exit sequence with a stop in place: the stop is cancelled first, then the holding is sold at market.
        var again = await h.Executor.PlaceAsync(run, 1, OrderPurpose.Entry, Decision, Entry(h, "ETHUSDT"), NextBar, null, default);
        var ethStop = await h.Executor.PlaceAsync(run, 1, OrderPurpose.Stop, Decision, Stop(h, again.ExecutedQuantity, "ETHUSDT"), NextBar, null, default);
        await h.Executor.ExitAsync(run, 1, "ETHUSDT", OrderPurpose.Close, Decision.AddHours(4), NextBar.AddHours(4), default);
        Assert.Equal(OrderStatus.Canceled, (await h.Store.OrderAsync(ethStop.ClientOrderId, default)).Status);
        var sell = Assert.Single(h.Fake.Orders, o => o.Type == "MARKET");
        Assert.Equal("FILLED", sell.Status);
        Assert.StartsWith("pi-c-", sell.ClientOrderId);
        Assert.True((await h.Executor.ReconcileAsync([run], default)).Ok);
    }

    [Fact]
    public async Task A_cancel_replace_that_half_fails_leaves_the_old_stop_cancelled_and_the_new_one_rejected()
    {
        var (h, run) = await SetupAsync();
        var bought = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h), NextBar, null, default);
        var stop = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Stop, Decision, Stop(h, bought.ExecutedQuantity), NextBar, null, default);
        h.Fake.RejectStops.Add("BTCUSDT");

        // The stop is raised at the next close; the exchange cancels the old one but refuses the new one (-2021, HTTP 409).
        var raised = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Stop, NextBar, Stop(h, bought.ExecutedQuantity), NextBar.AddHours(4), stop.ClientOrderId, default);

        Assert.Equal(OrderStatus.Canceled, (await h.Store.OrderAsync(stop.ClientOrderId, default)).Status);
        Assert.Equal(OrderStatus.ExchangeRejected, raised.Status);
        Assert.Equal("exchange-rejected:-2010", raised.Reason);
        Assert.DoesNotContain(h.Fake.Orders, o => o.Type == "STOP_LOSS" && o.Status == "NEW"); // unprotected: the stop synchronisation retries
        Assert.Equal(1, h.Fake.Log.Count(l => l == "POST /api/v3/order/cancelReplace"));
    }

    [Fact]
    public async Task Reconciliation_catches_unknown_orders_balance_drift_and_a_testnet_reset()
    {
        var (h, run) = await SetupAsync();
        var bought = await h.Executor.PlaceAsync(run, 0, OrderPurpose.Entry, Decision, Entry(h), NextBar, null, default);
        await h.Executor.PlaceAsync(run, 0, OrderPurpose.Stop, Decision, Stop(h, bought.ExecutedQuantity), NextBar, null, default);
        Assert.True((await h.Executor.ReconcileAsync([run], default)).Ok);

        h.Fake.Orders.Add(new FakeBinance.FakeOrder { Symbol = "BTCUSDT", ClientOrderId = "pi-e-aaaaaaaaaaaaaaaaaaaaaaaaaa", OrderId = 1, Side = "BUY", Type = "LIMIT", Quantity = 1, Price = 1 });
        Assert.Equal("unknown-open-order", (await h.Executor.ReconcileAsync([run], default)).Reason);
        h.Fake.Orders.RemoveAt(h.Fake.Orders.Count - 1);

        h.Fake.Orders.Add(new FakeBinance.FakeOrder { Symbol = "BTCUSDT", ClientOrderId = "web_manual_order", OrderId = 2, Side = "BUY", Type = "LIMIT", Quantity = 1, Price = 1 });
        Assert.True((await h.Executor.ReconcileAsync([run], default)).Ok); // a foreign order alone is only counted
        h.Fake.Orders.RemoveAt(h.Fake.Orders.Count - 1);

        h.Fake.Free["USDT"] += 0.5m; // someone traded on the account
        Assert.Equal("balance", (await h.Executor.ReconcileAsync([run], default)).Reason);
        h.Fake.Free["USDT"] -= 0.5m;

        h.Fake.Reset(usdt: 10_000m); // orders wiped, fresh balances (S-B19)
        Assert.Equal("testnet-reset-suspected", (await h.Executor.ReconcileAsync([run], default)).Reason);
    }
}
