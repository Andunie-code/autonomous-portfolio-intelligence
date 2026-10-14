using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Indicators;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>Golden values computed by hand from the definitions in docs/m2-protocol.md §3, and look-ahead properties.</summary>
public class IndicatorTests
{
    [Fact]
    public void Ema_is_seeded_with_the_simple_mean_and_then_smoothed()
    {
        var ema = new Ema(3);
        Assert.Null(ema.Update(1));
        Assert.Null(ema.Update(2));
        Assert.Equal(2m, ema.Update(3));
        Assert.Equal(3m, ema.Update(4));
        Assert.Equal(4m, ema.Update(5));
    }

    [Fact]
    public void Atr_uses_true_range_and_wilder_smoothing()
    {
        var atr = new Atr(3);
        Assert.Null(atr.Update(10, 8, 9));
        Assert.Null(atr.Update(11, 9, 10));
        Assert.Equal(2m, atr.Update(12, 10, 11));
        Assert.Equal(8m / 3, atr.Update(11, 7, 8));
        Assert.Equal((8m / 3 * 2 + 8) / 3, atr.Update(16, 12, 15));
    }

    [Fact]
    public void Rsi_uses_wilder_averages_and_is_100_without_losses()
    {
        var rsi = new Rsi(3);
        foreach (var close in new[] { 10m, 11m, 12m })
        {
            Assert.Null(rsi.Update(close));
        }

        Assert.Equal(100m - 100m / (1 + (2m / 3) / (1m / 3)), rsi.Update(11));
        Assert.Equal(100m - 100m / (1 + ((2m / 3 * 2 + 2) / 3) / ((1m / 3 * 2 + 0) / 3)), rsi.Update(13));

        var rising = new Rsi(2);
        rising.Update(1);
        rising.Update(2);
        Assert.Equal(100m, rising.Update(3));
    }

    [Fact]
    public void Supertrend_bands_and_direction_follow_the_recursive_rules()
    {
        var st = new Supertrend(3, 1m);
        st.Update(10, 8, 9);
        st.Update(11, 9, 10);
        Assert.Equal(new SupertrendValue(13m, 9m, false), st.Update(12, 10, 11));
        Assert.Equal(new SupertrendValue(9m + 8m / 3, 9m, false), st.Update(11, 7, 8));
        var atr = (8m / 3 * 2 + 8) / 3;
        Assert.Equal(new SupertrendValue(9m + 8m / 3, 14m - atr, true), st.Update(16, 12, 15));
    }

    [Fact]
    public void Reset_returns_indicators_to_their_initial_state()
    {
        var ema = new Ema(2);
        ema.Update(5);
        ema.Update(7);
        ema.Reset();
        Assert.Null(ema.Value);
        Assert.Null(ema.Update(1));
        Assert.Equal(2m, ema.Update(3));
    }

    [Fact]
    public void Indicator_values_up_to_t_do_not_depend_on_later_candles()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var series = Synthetic.RandomWalk("XUSDT", 300, seed);
            var changed = series.Take(150).Concat(Synthetic.RandomWalk("XUSDT", 150, seed + 1000, 50m)).ToList();

            Assert.Equal(Run(series).Take(150), Run(changed).Take(150));
        }

        static List<string> Run(List<PortfolioIntelligence.Kernel.MarketData.Candle> candles)
        {
            var ema = new Ema(50);
            var atr = new Atr(14);
            var rsi = new Rsi(14);
            var st = new Supertrend(10, 3m);
            return candles.Select(c => $"{ema.Update(c.Close)}|{atr.Update(c.High, c.Low, c.Close)}|{rsi.Update(c.Close)}|{st.Update(c.High, c.Low, c.Close)}").ToList();
        }
    }

    [Fact]
    public void Strategy_signals_up_to_t_do_not_depend_on_later_candles()
    {
        foreach (IStrategy strategy in new IStrategy[] { new EmaTrendStrategy(), new SupertrendStrategy() })
        {
            var series = Synthetic.RandomWalk("XUSDT", 600, 7);
            var changed = series.Take(300).Concat(Synthetic.RandomWalk("XUSDT", 300, 99, 10m)).ToList();
            Assert.Equal(Signals(strategy, series).Take(300), Signals(strategy, changed).Take(300));
        }

        static List<(bool, bool)> Signals(IStrategy strategy, List<PortfolioIntelligence.Kernel.MarketData.Candle> candles)
        {
            var instance = strategy.CreateInstance();
            return candles.Select(c =>
            {
                instance.Update(c);
                return (instance.EntrySignal, instance.ExitSignal);
            }).ToList();
        }
    }
}
