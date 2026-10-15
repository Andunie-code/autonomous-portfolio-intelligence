using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Indicators;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>Golden values computed by hand from docs/m3-protocol.md §1, warm-up lengths and look-ahead properties.</summary>
public class PoolIndicatorTests
{
    [Fact]
    public void Sma_is_the_mean_of_the_last_n_inputs()
    {
        var sma = new Sma(3);
        Assert.Null(sma.Update(1));
        Assert.Null(sma.Update(2));
        Assert.Equal(2m, sma.Update(3));
        Assert.Equal(3m, sma.Update(4));
        Assert.Equal(5m, sma.Update(8));
    }

    [Fact]
    public void Macd_line_signal_and_histogram_start_at_bar_s_plus_g_minus_1()
    {
        var macd = new Macd(2, 3, 2);
        var values = new[] { 1m, 2m, 3m, 4m, 6m }.Select(c => macd.Update(c)).ToList();

        Assert.All(values.Take(3), v => Assert.Null(v));
        // EMA2: 1.5, 2.5, 3.5, 5.1666…; EMA3: 2, 3, 4.5; line: 0.5, 0.5, 0.6666…; signal seeded with mean(0.5, 0.5).
        Assert.Equal(0.5m, Math.Round(values[3]!.Value.Line, 20));
        Assert.Equal(0.5m, Math.Round(values[3]!.Value.Signal, 20));
        var line = 2m / 3 * 6 + 1m / 3 * (2m / 3 * 4 + 1m / 3 * 2.5m) - 4.5m;
        Assert.Equal(Math.Round(line, 20), Math.Round(values[4]!.Value.Line, 20));
        Assert.Equal(Math.Round(2m / 3 * line + 1m / 3 * 0.5m, 20), Math.Round(values[4]!.Value.Signal, 20));
        Assert.Equal(values[4]!.Value.Line - values[4]!.Value.Signal, values[4]!.Value.Histogram);
    }

    [Fact]
    public void Adx_uses_wilder_sums_and_is_seeded_with_the_mean_of_the_first_n_dx()
    {
        var adx = new Adx(2);
        Assert.Null(adx.Update(10, 8, 9));
        Assert.Null(adx.Update(12, 9, 11));

        // Bar 3 (n + 1): TR sum 7, +DM sum 2, −DM sum 2.
        var third = adx.Update(11, 7, 8)!.Value;
        Assert.Equal(200m / 7, third.PlusDi);
        Assert.Equal(200m / 7, third.MinusDi);
        Assert.Null(third.Adx);

        // Bar 4 (2n): TR 7 − 3.5 + 5 = 8.5, +DM 3, −DM 1, DX = 50; ADX = mean(0, 50).
        var fourth = adx.Update(13, 10, 12)!.Value;
        Assert.Equal(300m / 8.5m, fourth.PlusDi);
        Assert.Equal(25m, Math.Round(fourth.Adx!.Value, 20));

        // Bar 5: TR 6.25, +DM 2.5, −DM 0.5 → +DI 40, −DI 8, DX 66.6…; ADX = (25 + DX) / 2.
        var fifth = adx.Update(14, 12, 13)!.Value;
        Assert.Equal(40m, fifth.PlusDi);
        Assert.Equal(8m, fifth.MinusDi);
        Assert.Equal(Math.Round((25m + 100m * 32 / 48) / 2, 20), Math.Round(fifth.Adx!.Value, 20));
    }

    [Fact]
    public void Bollinger_uses_population_sigma_rounded_to_12_significant_digits()
    {
        var bands = new Bollinger(3, 2m);
        bands.Update(1);
        bands.Update(2);
        var value = bands.Update(3)!.Value;

        Assert.Equal(0.816496580928m, Numerics.Sqrt(2m / 3));
        Assert.Equal(2m, value.Middle);
        Assert.Equal(2m + 2 * 0.816496580928m, value.Upper);
        Assert.Equal(2m - 2 * 0.816496580928m, value.Lower);
        Assert.Equal((value.Upper - value.Lower) / 2, value.Bandwidth);
        Assert.Equal((3 - value.Lower) / (value.Upper - value.Lower), value.PercentB);

        var flat = new Bollinger(2, 2m);
        flat.Update(5);
        var collapsed = flat.Update(5)!.Value;
        Assert.Null(collapsed.PercentB);
        Assert.Equal(0m, collapsed.Bandwidth);
    }

    [Fact]
    public void Donchian_excludes_the_current_candle()
    {
        var channel = new Donchian(2);
        Assert.Null(channel.Update(10, 8));
        Assert.Null(channel.Update(12, 9));
        Assert.Equal(new DonchianValue(12, 8, 10), channel.Update(11, 7));
        Assert.Equal(new DonchianValue(12, 7, 9.5m), channel.Update(30, 1));
    }

    [Fact]
    public void Roc_is_a_fraction_of_the_close_n_candles_earlier()
    {
        var roc = new Roc(2);
        Assert.Null(roc.Update(10));
        Assert.Null(roc.Update(11));
        Assert.Equal(0.2m, roc.Update(12));
        Assert.Equal(9m / 11 - 1, roc.Update(9));
    }

    [Fact]
    public void Reset_clears_every_pool_indicator()
    {
        var macd = new Macd(2, 3, 2);
        var adx = new Adx(2);
        var bands = new Bollinger(2, 1m);
        var channel = new Donchian(2);
        var roc = new Roc(1);
        foreach (var c in Synthetic.RandomWalk("XUSDT", 20, 3))
        {
            macd.Update(c.Close);
            adx.Update(c.High, c.Low, c.Close);
            bands.Update(c.Close);
            channel.Update(c.High, c.Low);
            roc.Update(c.Close);
        }

        macd.Reset();
        adx.Reset();
        bands.Reset();
        channel.Reset();
        roc.Reset();
        Assert.Null(macd.Update(1));
        Assert.Null(adx.Update(2, 1, 1.5m));
        Assert.Null(bands.Update(1));
        Assert.Null(channel.Update(2, 1));
        Assert.Null(roc.Update(1));
    }

    [Fact]
    public void Pool_indicator_values_up_to_t_do_not_depend_on_later_candles()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var series = Synthetic.RandomWalk("XUSDT", 300, seed);
            var changed = series.Take(150).Concat(Synthetic.RandomWalk("XUSDT", 150, seed + 1000, 50m)).ToList();

            Assert.Equal(Run(series).Take(150), Run(changed).Take(150));
        }

        static List<string> Run(List<Candle> candles)
        {
            var sma = new Sma(20);
            var macd = new Macd(12, 26, 9);
            var adx = new Adx(14);
            var bands = new Bollinger(20, 2m);
            var channel = new Donchian(20);
            var roc = new Roc(10);
            var volume = new Sma(20);
            return candles.Select(c =>
                $"{sma.Update(c.Close)}|{macd.Update(c.Close)}|{adx.Update(c.High, c.Low, c.Close)}|{bands.Update(c.Close)}|" +
                $"{channel.Update(c.High, c.Low)}|{roc.Update(c.Close)}|{volume.Update(c.QuoteVolume)}").ToList();
        }
    }
}
