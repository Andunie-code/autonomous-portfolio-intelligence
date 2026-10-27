using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Execution.Exchange;
using PortfolioIntelligence.Execution.Exchange.Binance;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Live;

namespace PortfolioIntelligence.Trading.Tests.Support;

/// <summary>
/// Kohort 0 in paper on synthetic candles with a fake Binance Testnet (m6-protocol §9): the trader has a Testnet
/// executor whose adapter goes through the real signing handler and guard into <see cref="FakeBinance"/>. The fake's
/// reference price is the close of the latest closed bar at the harness clock.
/// </summary>
public sealed class TestnetHarness
{
    public static readonly DateTimeOffset Now0 = new(2026, 3, 1, 1, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset TradingStart = PaperTrader.NextBoundary(Now0);
    public static readonly DateTimeOffset WarmupStart = TradingStart - PaperTrader.Warmup;
    public static readonly TimeSpan Bar = TimeSpan.FromHours(4);

    private TestnetHarness(OpsDatabase.OpsDb db, OperationalManifest manifest, Dictionary<string, IReadOnlyList<Candle>> candles, ManualTime time, FakeBinance fake, ExchangeClock clock)
    {
        Db = db;
        Manifest = manifest;
        Candles = candles;
        Time = time;
        Fake = fake;
        Clock = clock;
        Adapter = new BinanceTestnetAdapter(fake.Client(manifest.Pairs.Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal), clock), clock);
        Store = new ExchangeStore(db.As("trading_app"));
        var paper = new PaperStore(db.As("trading_app"));
        Executor = new TestnetExecutor(Adapter, Store, clock, (e, payload, ct) => paper.AuditAsync("trading", e, payload, ct));
    }

    public OpsDatabase.OpsDb Db { get; }
    public OperationalManifest Manifest { get; }
    public Dictionary<string, IReadOnlyList<Candle>> Candles { get; }
    public ManualTime Time { get; }
    public FakeBinance Fake { get; }
    public ExchangeClock Clock { get; }
    public BinanceTestnetAdapter Adapter { get; }
    public ExchangeStore Store { get; }
    public TestnetExecutor Executor { get; }

    public static async Task<TestnetHarness> CreateAsync(OpsDatabase fixture, int days = 70, int seed = 500)
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async();
        var bars = (int)((TradingStart - WarmupStart + TimeSpan.FromDays(days)) / Bar);
        var candles = manifest.Pairs.Select((p, i) => (p.Symbol, Series: (IReadOnlyList<Candle>)LiveSynthetic.Walk(p.Symbol, WarmupStart, bars, seed + i)))
            .ToDictionary(x => x.Symbol, x => x.Series);
        await db.AddCandlesAsync(candles.Values.SelectMany(c => c));
        var time = new ManualTime(Now0);
        var fake = new FakeBinance(time);
        var harness = new TestnetHarness(db, manifest, candles, time, fake, new ExchangeClock(time));
        fake.Price = harness.ReferencePrice;
        return harness;
    }

    /// <summary>Close of the latest bar closed at the harness time (the exchange trades near the last close).</summary>
    public decimal ReferencePrice(string symbol) =>
        Candles.TryGetValue(symbol, out var series) ? series.Last(c => c.OpenTime + Bar <= Time.Now).Close : 100m;

    public PaperTrader Trader(bool testnet = true) =>
        new(new PaperStore(Db.As("trading_app")), new LiveCandleSource(new LiveCandleStore(Db.As("trading_app"))), CostModel.V1, testnet: testnet ? Executor : null);

    /// <summary>One cycle at <paramref name="now"/>; the bars closed since the previous cycle first trigger the exchange's stops (intrabar).</summary>
    public async Task<CycleOutcome> CycleAsync(DateTimeOffset now, DateTimeOffset? previous = null, bool trigger = true)
    {
        if (trigger && previous is { } from)
        {
            foreach (var (symbol, series) in Candles)
            {
                foreach (var bar in series.Where(c => c.OpenTime + Bar > from && c.OpenTime + Bar <= now))
                {
                    Time.Now = bar.OpenTime + TimeSpan.FromMinutes(90);
                    Fake.TriggerStops(symbol, bar.Low, bar.Open, Time.Now);
                }
            }
        }

        Time.Now = now;
        return await Trader().ProcessAsync(now, default);
    }

    /// <summary>The user's PaperTrading → TestnetActive transition for every Kohort 0 member (as ops_api, with the D30 evidence).</summary>
    public async Task ActivateAsync(params int[] members)
    {
        foreach (var member in members)
        {
            var id = await Db.ScalarAsync<long>($"SELECT id FROM research.frozen_strategies WHERE cohort_id = 1 AND member_index = {member}");
            await Db.ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'PaperTrading', 'TestnetActive', 'user', '{\"d30\": {}, \"confirm\": true}')",
                "ops_api", id);
        }
    }
}
