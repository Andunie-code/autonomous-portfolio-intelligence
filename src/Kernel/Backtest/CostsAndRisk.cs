using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Kernel.Backtest;

/// <summary>Cost model v1 (docs/m2-protocol.md §8). All rates are non-negative fractions.</summary>
public sealed record CostModel(decimal FeeRate, decimal OpenSlippage, decimal StopSlippage, decimal MinimumNotional)
{
    public static CostModel V1 { get; } = new(0.0010m, 0.0005m, 0.0020m, 10m);

    public string Version => "cost-model v1";

    public decimal BuyPrice(decimal reference, decimal slippage) => reference * (1 + slippage);

    public decimal SellPrice(decimal reference, decimal slippage) => reference * (1 - slippage);

    public decimal Fee(decimal notional) => Math.Abs(notional) * FeeRate;
}

/// <summary>Portfolio risk settings (D7, D25; protocol §6 and §9).</summary>
public sealed record RiskSettings(
    int MaxOpenPositions,
    decimal MaxAssetWeight,
    decimal MaxTotalExposure,
    decimal DailyLossLimit,
    decimal MaxDrawdown,
    bool DrawdownHaltEnabled,
    decimal MaxPositionLoss)
{
    public static RiskSettings Policy { get; } = new(5, 0.20m, 0.80m, 0.03m, 0.15m, true, 0.10m);

    public static RiskSettings NoHalt { get; } = Policy with { DrawdownHaltEnabled = false };

    public string Variant => DrawdownHaltEnabled ? "policy" : "no-halt";
}

/// <summary>
/// RiskConfig v2 (ADR-0018 §2): the D7/D25 settings plus the experimental-pair cap (D22), the data-freshness rule
/// (D7) and an optional correlation-cluster cap that is off by default (P1; binding only by user decision).
/// </summary>
public sealed record RiskConfig(
    RiskSettings Settings,
    decimal ExperimentalPairsMaxExposure = 0.10m,
    int MaxDataLagBars = 2,
    ClusterCapSettings? ClusterCap = null)
{
    public static RiskConfig Policy { get; } = new(RiskSettings.Policy);

    public string Version => "risk-config v2";
}

/// <summary>Correlation-cluster exposure cap: clusters at average correlation ≥ <see cref="Threshold"/>, each ≤ <see cref="MaxExposure"/>.</summary>
public sealed record ClusterCapSettings(double Threshold, decimal MaxExposure);

public enum ExitReason
{
    Stop,
    Signal,
    EndOfTest,
    Manual,
}

/// <summary>Protective stop rules of ADR-0014 and D25.</summary>
public static class ProtectiveStop
{
    /// <summary>The level closest to the entry of the strategy stop and the hard stop.</summary>
    public static decimal Effective(decimal strategyStop, decimal entryFill, decimal maxPositionLoss) =>
        Math.Max(strategyStop, entryFill * (1 - maxPositionLoss));

    /// <summary>
    /// Stop reference price for <paramref name="candle"/>, or null if not triggered:
    /// Open ≤ S gives Open (gap), otherwise Low ≤ S gives S. On the entry candle only the Low rule applies.
    /// </summary>
    public static decimal? Trigger(Candle candle, decimal stop, bool isEntryCandle)
    {
        if (!isEntryCandle && candle.Open <= stop)
        {
            return candle.Open;
        }

        return candle.Low <= stop ? stop : null;
    }
}

/// <summary>Allocation v1 (ADR-0011, protocol §9): inverse volatility within the approved caps.</summary>
public static class InverseVolatilityAllocation
{
    public const int VolatilityDays = 30;

    /// <summary>
    /// Weights for <paramref name="volatilities"/> (open positions and new candidates together, σ &gt; 0):
    /// w = total × (1/σ) / Σ(1/σ), each capped at the asset cap; the remainder stays in cash.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> Weights(IReadOnlyDictionary<string, decimal> volatilities, RiskSettings risk)
    {
        var inverse = volatilities.Where(v => v.Value > 0).ToDictionary(v => v.Key, v => 1m / v.Value);
        var total = inverse.Values.Sum();
        return inverse.ToDictionary(
            v => v.Key,
            v => total == 0 ? 0m : Math.Min(risk.MaxAssetWeight, risk.MaxTotalExposure * v.Value / total));
    }

    /// <summary>
    /// σ of the last 30 daily log returns from 31 consecutive UTC day-end closes ending at or before
    /// <paramref name="asOfDate"/>; null when any day is missing or σ is zero. Rounded to 12 significant digits.
    /// </summary>
    public static decimal? Volatility(IReadOnlyDictionary<DateOnly, decimal> dayEndCloses, DateOnly asOfDate)
    {
        var closes = new List<double>(VolatilityDays + 1);
        for (var day = asOfDate.AddDays(-VolatilityDays); day <= asOfDate; day = day.AddDays(1))
        {
            if (!dayEndCloses.TryGetValue(day, out var close) || close <= 0)
            {
                return null;
            }

            closes.Add((double)close);
        }

        var returns = closes.Zip(closes.Skip(1), (a, b) => Math.Log(b / a)).ToList();
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var sigma = Math.Sqrt(variance);
        if (sigma <= 0 || double.IsNaN(sigma))
        {
            return null;
        }

        var rounded = Math.Round(sigma, Math.Clamp(11 - (int)Math.Floor(Math.Log10(sigma)), 0, 15));
        return (decimal)rounded;
    }
}
