using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>Protective stop (ADR-0014, D25), cost model v1 and allocation v1 (docs/m2-protocol.md §6, §8, §9).</summary>
public class RulesTests
{
    [Theory]
    [InlineData(80, 100, 90)]   // strategy stop wider than 10 %: the hard stop wins
    [InlineData(95, 100, 95)]   // strategy stop tighter: it wins
    public void Effective_stop_is_the_level_closest_to_the_entry(decimal strategyStop, decimal entry, decimal expected)
    {
        Assert.Equal(expected, ProtectiveStop.Effective(strategyStop, entry, 0.10m));
    }

    [Fact]
    public void Stop_triggers_on_gap_open_or_low_and_only_low_on_the_entry_candle()
    {
        var at = Synthetic.At(0);
        Assert.Equal(85m, ProtectiveStop.Trigger(Synthetic.Bar("X", at, 85, 92, 84, 88), 90m, isEntryCandle: false));
        Assert.Equal(90m, ProtectiveStop.Trigger(Synthetic.Bar("X", at, 95, 96, 89, 94), 90m, isEntryCandle: false));
        Assert.Null(ProtectiveStop.Trigger(Synthetic.Bar("X", at, 95, 96, 91, 94), 90m, isEntryCandle: false));
        Assert.Equal(90m, ProtectiveStop.Trigger(Synthetic.Bar("X", at, 85, 92, 84, 88), 90m, isEntryCandle: true));
    }

    [Fact]
    public void Cost_model_v1_values_and_signs()
    {
        var costs = CostModel.V1;
        Assert.Equal(100.05m, costs.BuyPrice(100, costs.OpenSlippage));
        Assert.Equal(99.80m, costs.SellPrice(100, costs.StopSlippage));
        Assert.Equal(1m, costs.Fee(1000));
        Assert.Equal(1m, costs.Fee(-1000));
        Assert.True(costs.FeeRate >= 0 && costs.OpenSlippage >= 0 && costs.StopSlippage >= 0);
    }

    [Fact]
    public void Allocation_weights_never_exceed_the_caps_for_random_inputs()
    {
        var random = new Random(42);
        for (var i = 0; i < 5_000; i++)
        {
            var count = random.Next(1, 6);
            var volatilities = Enumerable.Range(0, count).ToDictionary(j => $"S{j}", _ => (decimal)(random.NextDouble() * 0.2 + 1e-6));
            var weights = InverseVolatilityAllocation.Weights(volatilities, RiskSettings.Policy);

            Assert.All(weights.Values, w => Assert.InRange(w, 0m, 0.20m));
            Assert.True(weights.Values.Sum() <= 0.80m + 0.0000000001m);
        }
    }

    [Fact]
    public void Lower_volatility_gets_more_weight_until_the_cap()
    {
        var weights = InverseVolatilityAllocation.Weights(new Dictionary<string, decimal> { ["A"] = 0.01m, ["B"] = 0.02m, ["C"] = 0.04m, ["D"] = 0.04m, ["E"] = 0.04m }, RiskSettings.Policy);

        Assert.Equal(0.20m, weights["A"]);
        Assert.True(weights["B"] > weights["C"]);
        Assert.Equal(weights["C"], weights["D"]);
    }

    [Fact]
    public void Volatility_needs_31_consecutive_day_end_closes()
    {
        var closes = Enumerable.Range(0, 31).ToDictionary(i => new DateOnly(2022, 1, 1).AddDays(i), i => 100m * (i % 2 == 0 ? 1m : 1.01m));
        var asOf = new DateOnly(2022, 1, 31);

        Assert.NotNull(InverseVolatilityAllocation.Volatility(closes, asOf));
        closes.Remove(new DateOnly(2022, 1, 15));
        Assert.Null(InverseVolatilityAllocation.Volatility(closes, asOf));

        var flat = Enumerable.Range(0, 31).ToDictionary(i => new DateOnly(2022, 1, 1).AddDays(i), _ => 100m);
        Assert.Null(InverseVolatilityAllocation.Volatility(flat, asOf));
    }
}
