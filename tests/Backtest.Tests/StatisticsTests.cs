using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>Statistical building blocks of m3-protocol §4–§6.</summary>
public class StatisticsTests
{
    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.6448536269514722, 0.95)]
    [InlineData(-1.959963984540054, 0.025)]
    [InlineData(3.090232306167813, 0.999)]
    public void Normal_cdf_and_inverse_match_reference_values(double x, double p)
    {
        Assert.Equal(p, Normal.Cdf(x), 12);
        Assert.Equal(x, Normal.InverseCdf(p), 9);
    }

    [Fact]
    public void Primary_metric_is_the_annualised_mean_log_return_from_the_initial_capital()
    {
        var t = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var equity = new List<EquityPoint> { new(t, 11_000m, 0), new(t.AddHours(4), 9_900m, 0) };

        var returns = ReturnStatistics.LogReturns(10_000m, equity);

        Assert.Equal(Math.Log(1.1), returns[0], 14);
        Assert.Equal(Math.Log(0.9), returns[1], 14);
        Assert.Equal((Math.Log(1.1) + Math.Log(0.9)) / 2 * 2190, ReturnStatistics.AnnualisedMean(returns), 10);
        Assert.Equal(0.1, ReturnStatistics.MaxDrawdown(10_000m, equity), 12);
    }

    [Fact]
    public void Stationary_bootstrap_is_seeded_and_its_bounds_bracket_the_estimate()
    {
        var random = new Random(5);
        var x = Enumerable.Range(0, 2000).Select(_ => 0.0004 + (random.NextDouble() - 0.5) * 0.02).ToArray();
        var first = StationaryBootstrap.Distribution(x, 2000, 42, 2190);
        var second = StationaryBootstrap.Distribution(x, 2000, 42, 2190);
        var other = StationaryBootstrap.Distribution(x, 2000, 43, 2190);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(13, StationaryBootstrap.MeanBlockLength(2000));

        var estimate = ReturnStatistics.AnnualisedMean(x);
        var bounds = StationaryBootstrap.Bounds(estimate, first, 0.05);
        Assert.True(bounds.Lower < estimate && estimate < bounds.Upper);
        Assert.Equal(bounds.Lower > 0, bounds.PValue < 0.05);
    }

    [Fact]
    public void Holm_tests_in_p_order_and_stops_at_the_first_non_rejection()
    {
        var levels = Holm.Levels([0.04, 0.01, 0.03], 0.05);

        Assert.Equal((0.05 / 3, true), levels[1]);
        Assert.Equal((0.05 / 2, true), levels[2]);
        Assert.Equal((0.05 / 1, false), levels[0]); // 0.03 ≥ 0.025, so the third is not eligible
        Assert.All(Holm.Levels([0.001, 0.002], 0.025), l => Assert.True(l.Eligible));
    }

    [Fact]
    public void Deflated_sharpe_benchmark_grows_with_the_number_of_trials()
    {
        Assert.Equal(0, DeflatedSharpe.Benchmark(1, 0.01));
        Assert.True(DeflatedSharpe.Benchmark(100, 0.01) > DeflatedSharpe.Benchmark(10, 0.01));

        var single = DeflatedSharpe.Probability(0.05, 1000, 0, 3, 1, 0.0004);
        var many = DeflatedSharpe.Probability(0.05, 1000, 0, 3, 1000, 0.0004);
        Assert.Equal(Normal.Cdf(0.05 * Math.Sqrt(999) / Math.Sqrt(1 + 0.5 * 0.0025)), single, 12);
        Assert.True(many < single);
    }

    [Fact]
    public void Duration_planner_finds_the_shortest_powered_window_and_flags_underpowered_cohorts()
    {
        var start = new DateOnly(2021, 6, 1);
        var btc = new Dictionary<DateOnly, decimal>();
        var random = new Random(9);
        var price = 30_000.0;
        for (var d = start; d < new DateOnly(2026, 1, 1); d = d.AddDays(1))
        {
            price *= Math.Exp((random.NextDouble() - 0.5) * 0.06);
            btc[d] = (decimal)Math.Round(price, 2);
        }

        var daily = new Dictionary<DateOnly, double>();
        var entries = new List<DateOnly>();
        for (var d = new DateOnly(2022, 1, 1); d < new DateOnly(2026, 1, 1); d = d.AddDays(1))
        {
            daily[d] = (random.NextDouble() - 0.5) * 0.004;
            if (d.Day % 5 == 0)
            {
                entries.Add(d);
            }
        }

        var strong = DurationPlanner.Plan(daily, entries, btc, delta: 0.50, planningAlpha: 0.025, targetPower: 0.8, seed: 1, replicates: 2000);
        Assert.False(strong.Underpowered);
        Assert.Equal(6, strong.CohortMonths);
        Assert.All(strong.Regimes, r => Assert.True(r.Days >= DurationPlanner.MinRegimeDays));
        Assert.Equal(strong.PowerCurve.First(p => p.Power >= 0.8).Months, strong.PowerMonths);

        var weak = DurationPlanner.Plan(daily, entries, btc, delta: 0.01, planningAlpha: 0.025, targetPower: 0.8, seed: 1, replicates: 2000);
        Assert.True(weak.Underpowered);
        Assert.Equal(DurationPlanner.MaxMonths, weak.CohortMonths);

        var silent = DurationPlanner.Plan(daily, [], btc, delta: 0.50, planningAlpha: 0.025, targetPower: 0.8, seed: 1, replicates: 2000);
        Assert.True(silent.Underpowered);
    }
}
