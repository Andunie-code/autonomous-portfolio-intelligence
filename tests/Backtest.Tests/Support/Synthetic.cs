using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Backtest.Tests.Support;

public static class Synthetic
{
    public static readonly DateTimeOffset Start = new(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(int bar) => Start.AddHours(4 * bar);

    public static Candle Bar(string symbol, DateTimeOffset open, decimal o, decimal h, decimal l, decimal c) =>
        new(symbol, CandleInterval.FourHours, open, o, h, l, c, 1, c, 1);

    /// <summary>Flat candles at <paramref name="price"/> (open = close = price, ±1 % range) for each bar index.</summary>
    public static List<Candle> Flat(string symbol, int bars, decimal price, Func<int, decimal>? priceAt = null) =>
        Enumerable.Range(0, bars).Select(i =>
        {
            var p = priceAt?.Invoke(i) ?? price;
            return Bar(symbol, At(i), p, p * 1.01m, p * 0.99m, p);
        }).ToList();

    /// <summary>Deterministic random walk with valid OHLC.</summary>
    public static List<Candle> RandomWalk(string symbol, int bars, int seed, decimal start = 100m)
    {
        var random = new Random(seed);
        var candles = new List<Candle>(bars);
        var close = start;
        for (var i = 0; i < bars; i++)
        {
            var open = close;
            close = Math.Max(1m, open * (1 + (decimal)((random.NextDouble() - 0.5) * 0.06)));
            var high = Math.Max(open, close) * (1 + (decimal)(random.NextDouble() * 0.02));
            var low = Math.Min(open, close) * (1 - (decimal)(random.NextDouble() * 0.02));
            candles.Add(Bar(symbol, At(i), Math.Round(open, 6), Math.Round(high, 6) + 0.000001m, Math.Round(low, 6) - 0.000001m, Math.Round(close, 6)));
        }

        return candles;
    }
}

/// <summary>Scripted strategy: enters and exits at given candle open times; stop at a fixed fraction below the fill.</summary>
public sealed class ScriptedStrategy(ISet<DateTimeOffset> entries, ISet<DateTimeOffset> exits, decimal stopFraction = 0.5m, int warmup = 1) : IStrategy
{
    public string Name => "scripted";

    public string Version => "test";

    public string Definition => "scripted test strategy";

    public int WarmupBars => warmup;

    public IStrategyInstance CreateInstance() => new Instance(entries, exits, stopFraction);

    private sealed class Instance(ISet<DateTimeOffset> entries, ISet<DateTimeOffset> exits, decimal stopFraction) : IStrategyInstance
    {
        public bool EntrySignal { get; private set; }

        public bool ExitSignal { get; private set; }

        public decimal? TrailingStop => null;

        public int Resets { get; private set; }

        public void Update(Candle candle)
        {
            EntrySignal = entries.Contains(candle.OpenTime);
            ExitSignal = exits.Contains(candle.OpenTime);
        }

        public decimal InitialStop(decimal entryFill) => entryFill * (1 - stopFraction);

        public void Reset()
        {
            Resets++;
            EntrySignal = false;
            ExitSignal = false;
        }
    }
}
