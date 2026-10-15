using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Research.Evaluation;

/// <summary>
/// Seeded synthetic 4h universes for calibration (m3-protocol §7). Dates mirror the research window shape
/// (warm-up from 2021-07-05, trading 2022-01-01 to 2026-01-01) but every price is generated.
/// </summary>
public static class SyntheticMarkets
{
    public static readonly DateTimeOffset WarmupStart = new(2021, 7, 5, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset TradingStart = new(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset End = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public const int Pairs = 20;
    public const string Btc = "BTCUSDT";

    /// <summary>Null universe: zero-drift log random walks, 4h σ drawn uniformly from [0.008, 0.030] per pair.</summary>
    public static BacktestInput Null(int seed) => Build(seed, trend: null);

    /// <summary>
    /// Planted-edge universe: persistent drift regimes (mean length <paramref name="meanRegimeBars"/> bars, drift ±κσ
    /// per bar with an up-regime share above one half), so a trend-following strategy has a positive edge.
    /// </summary>
    public static BacktestInput Trending(int seed, double kappa, int meanRegimeBars, double upShare) =>
        Build(seed, (kappa, meanRegimeBars, upShare));

    private static BacktestInput Build(int seed, (double Kappa, int MeanBars, double UpShare)? trend)
    {
        var bars = (int)((End - WarmupStart).TotalHours / 4);
        var candles = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Pairs; i++)
        {
            var symbol = i == 0 ? Btc : $"SYN{i:D2}USDT";
            var random = new Random(unchecked(seed * 7919 + i * 104_729));
            var sigma = 0.008 + 0.022 * random.NextDouble();
            candles[symbol] = Series(symbol, bars, sigma, random, trend);
            ranks[symbol] = i + 1;
        }

        return new BacktestInput(candles, ranks, TradingStart, End, Reference: candles[Btc]);
    }

    private static List<Candle> Series(string symbol, int bars, double sigma, Random random, (double Kappa, int MeanBars, double UpShare)? trend)
    {
        var list = new List<Candle>(bars);
        var close = 100.0;
        var up = random.NextDouble() < (trend?.UpShare ?? 0.5);
        for (var b = 0; b < bars; b++)
        {
            var drift = 0.0;
            if (trend is { } t)
            {
                if (random.NextDouble() < 1.0 / t.MeanBars)
                {
                    up = random.NextDouble() < t.UpShare;
                }

                drift = (up ? 1 : -1) * t.Kappa * sigma;
            }

            var open = close;
            close = open * Math.Exp(drift + sigma * Gaussian(random));
            var high = Math.Max(open, close) * Math.Exp(Math.Abs(Gaussian(random)) * sigma * 0.5);
            var low = Math.Min(open, close) * Math.Exp(-Math.Abs(Gaussian(random)) * sigma * 0.5);
            var volume = Math.Exp(13 + 0.5 * Gaussian(random));
            list.Add(new Candle(symbol, CandleInterval.FourHours, WarmupStart.AddHours(4 * b),
                Price(open), Price(high), Price(low), Price(close), Math.Round((decimal)(volume / close), 4), Math.Round((decimal)volume, 2), 1000));
        }

        return list;
    }

    private static decimal Price(double value) => Math.Max(0.00000001m, Math.Round((decimal)value, 8));

    private static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
