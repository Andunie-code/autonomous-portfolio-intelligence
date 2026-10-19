using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Kernel.Backtest;

public sealed record Trade(
    string Symbol,
    DateTimeOffset EntryTime,
    decimal EntryPrice,
    decimal Quantity,
    DateTimeOffset ExitTime,
    decimal ExitPrice,
    ExitReason Reason,
    decimal Fees,
    decimal SlippageCost)
{
    /// <summary>Net profit after both fees (slippage is already in the fill prices).</summary>
    public decimal NetProfit => Quantity * (ExitPrice - EntryPrice) - Fees;
}

public sealed record EquityPoint(DateTimeOffset Time, decimal Equity, decimal Exposure);

public sealed record BacktestResult(
    string Strategy,
    string Variant,
    decimal InitialCapital,
    IReadOnlyList<Trade> Trades,
    IReadOnlyList<EquityPoint> Equity,
    DateTimeOffset? HaltedAt)
{
    public decimal FinalEquity => Equity.Count == 0 ? InitialCapital : Equity[^1].Equity;

    /// <summary>SHA-256 over trades and the equity series in a canonical invariant-culture form.</summary>
    public string Hash()
    {
        var s = new StringBuilder();
        s.Append(Strategy).Append('|').Append(Variant).Append('\n');
        foreach (var t in Trades)
        {
            s.Append(string.Create(CultureInfo.InvariantCulture,
                $"{t.Symbol},{t.EntryTime:O},{t.EntryPrice},{t.Quantity},{t.ExitTime:O},{t.ExitPrice},{t.Reason},{t.Fees}\n"));
        }

        foreach (var p in Equity)
        {
            s.Append(string.Create(CultureInfo.InvariantCulture, $"{p.Time:O},{p.Equity}\n"));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToString()))).ToLowerInvariant();
    }
}

public sealed record BacktestInput(
    IReadOnlyDictionary<string, IReadOnlyList<Candle>> Candles,
    IReadOnlyDictionary<string, int> CoreRank,
    DateTimeOffset TradingStart,
    DateTimeOffset End,
    decimal InitialCapital = 10_000m,
    IReadOnlyList<Candle>? Reference = null);

/// <summary>
/// Multi-asset, long-only spot backtest on one interval, following docs/m2-protocol.md §2 and §6–§9 exactly:
/// open fills (exits, then entries), intrabar protective stop, then close: equity, risk rules, indicator
/// updates, signals and orders for the next open. Gaps reset indicators (warm-up rule). Deterministic.
/// Since M5 a single-sleeve loop over <see cref="PortfolioEngine.Step"/> (ADR-0018 §1); results are unchanged.
/// </summary>
public sealed class BacktestEngine(IStrategy strategy, CostModel costs, RiskSettings risk)
{
    public BacktestResult Run(BacktestInput input)
    {
        var engine = Engine(input, out var steps, recordEvents: false);
        steps(engine);
        return engine.Result(0);
    }

    /// <summary>Runs the backtest and returns the engine with its L1/L2 event stream (m5-protocol §5.1).</summary>
    public PortfolioEngine RunWithEvents(BacktestInput input)
    {
        var engine = Engine(input, out var steps, recordEvents: true);
        steps(engine);
        return engine;
    }

    private PortfolioEngine Engine(BacktestInput input, out Action<PortfolioEngine> steps, bool recordEvents)
    {
        var interval = input.Candles.Values.SelectMany(c => c).Select(c => c.Interval).Distinct().Single();
        var length = interval.FixedLength ?? throw new ArgumentException("Fixed-length interval required.");
        if (input.Reference?.Any(c => c.Interval != interval) == true)
        {
            throw new ArgumentException("Reference series must use the same interval.");
        }

        var symbols = input.Candles.Keys.OrderBy(s => input.CoreRank.GetValueOrDefault(s, int.MaxValue)).ThenBy(s => s, StringComparer.Ordinal).ToList();
        var engine = new PortfolioEngine(new PortfolioSetup(
            [new SleeveSpec(strategy.Name, strategy, input.InitialCapital)], costs, new RiskConfig(risk), symbols, interval, input.TradingStart, RecordEvents: recordEvents));
        steps = e => PortfolioReplay.Run(e, symbols, input.Candles, input.Reference, length, input.End);
        return engine;
    }
}

/// <summary>Feeds a <see cref="PortfolioEngine"/> bar by bar from in-memory series (backtest and replay).</summary>
public static class PortfolioReplay
{
    public static void Run(
        PortfolioEngine engine,
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles,
        IReadOnlyList<Candle>? reference,
        TimeSpan length,
        DateTimeOffset end,
        Func<DateTimeOffset, StepContext>? context = null,
        bool final = true)
    {
        var byTime = candles.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(c => c.OpenTime));
        var referenceByTime = reference?.ToDictionary(c => c.OpenTime) ?? [];
        var start = candles.Values.SelectMany(c => c).Min(c => c.OpenTime);
        for (var t = start; t < end; t += length)
        {
            var bars = new Dictionary<string, Candle>();
            foreach (var symbol in symbols)
            {
                if (byTime[symbol].TryGetValue(t, out var bar))
                {
                    bars[symbol] = bar;
                }
            }

            var isLast = final && t + length >= end;
            var step = context?.Invoke(t) ?? StepContext.Normal;
            engine.Step(t, bars, referenceByTime.GetValueOrDefault(t), isLast ? step with { IsFinal = true } : step);
        }
    }
}
