using System.Net;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.MarketData.Live;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>The hourly cycle (m5-protocol §2.1, risk-model §3.1): schedule, cycle metric, health, HTTP 418 halt. No network.</summary>
public class TradingCycleTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]") });
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Cycles_run_at_quarter_past_the_hour()
    {
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 10, 0, 15, TimeSpan.Zero), TradingCycle.NextSchedule(new DateTimeOffset(2026, 3, 1, 9, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 11, 0, 15, TimeSpan.Zero), TradingCycle.NextSchedule(new DateTimeOffset(2026, 3, 1, 10, 0, 15, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero), PaperTrader.NextBoundary(new DateTimeOffset(2026, 3, 1, 4, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Http_418_engages_the_kill_switch_and_every_cycle_is_recorded()
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async();
        var store = new PaperStore(db.As("trading_app"));
        var stub = new StubHandler((HttpStatusCode)418);
        var symbols = manifest.Pairs.Select(p => p.Symbol).ToHashSet();
        var feed = new LiveCandleFeed(new LiveKlineClient(new HttpClient(new LiveKlineGuardHandler(symbols, stub))), new LiveCandleStore(db.As("trading_app")));
        var now = new DateTimeOffset(2026, 3, 1, 1, 0, 15, TimeSpan.Zero);
        var health = new TradingHealth();
        var trader = new PaperTrader(store, new LiveCandleSource(new LiveCandleStore(db.As("trading_app"))), CostModel.V1);

        var outcome = await new TradingCycle(store, trader, feed, health, new FixedTime(now)).RunAsync(now, default);

        Assert.Equal(1, stub.Requests);
        Assert.True(await store.KillSwitchEngagedAsync(default));
        Assert.Contains("kill-switch", outcome.Degraded);
        Assert.Equal("ip-banned", health.Current.Outcome);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.cycle_metrics WHERE outcome = 'ip-banned'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'live_data_ip_banned'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM market.live_candles"));
    }
}
