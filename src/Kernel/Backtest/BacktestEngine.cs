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
/// </summary>
public sealed class BacktestEngine(IStrategy strategy, CostModel costs, RiskSettings risk)
{
    private sealed class SymbolState(IStrategyInstance instance)
    {
        public IStrategyInstance Instance { get; } = instance;
        public int ValidBars { get; set; }
        public bool Seen { get; set; }
        public decimal? LastClose { get; set; }
        public Dictionary<DateOnly, decimal> DayEndCloses { get; } = [];
    }

    private sealed class Position
    {
        public required DateTimeOffset EntryTime { get; init; }
        public required decimal EntryPrice { get; init; }
        public required decimal Quantity { get; init; }
        public required decimal EntryFee { get; init; }
        public required decimal EntrySlippage { get; init; }
        public required decimal StrategyStop { get; set; }
        public required decimal Stop { get; set; }
    }

    private sealed record PendingEntry(string Symbol, decimal Notional);

    public BacktestResult Run(BacktestInput input)
    {
        var interval = input.Candles.Values.SelectMany(c => c).Select(c => c.Interval).Distinct().Single();
        var length = interval.FixedLength ?? throw new ArgumentException("Fixed-length interval required.");
        var byTime = input.Candles.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(c => c.OpenTime));
        var symbols = input.Candles.Keys.OrderBy(s => input.CoreRank.GetValueOrDefault(s, int.MaxValue)).ThenBy(s => s, StringComparer.Ordinal).ToList();
        // Reference series (m3-protocol §2): one tracker per run, fed the candle with the same open time.
        var consumer = strategy as IReferenceSeriesConsumer;
        var tracker = consumer?.CreateReferenceTracker();
        if (input.Reference?.Any(c => c.Interval != interval) == true)
        {
            throw new ArgumentException("Reference series must use the same interval.");
        }

        var referenceByTime = input.Reference?.ToDictionary(c => c.OpenTime) ?? [];
        var states = symbols.ToDictionary(s => s, _ => new SymbolState(tracker is null ? strategy.CreateInstance() : consumer!.CreateInstance(tracker)));
        var start = input.Candles.Values.SelectMany(c => c).Min(c => c.OpenTime);

        var cash = input.InitialCapital;
        var positions = new Dictionary<string, Position>();
        var pendingExits = new HashSet<string>();
        var pendingEntries = new List<PendingEntry>();
        var trades = new List<Trade>();
        var equity = new List<EquityPoint>();
        decimal peak = input.InitialCapital, dayStartEquity = input.InitialCapital;
        DateTimeOffset noEntriesUntil = DateTimeOffset.MinValue;
        DateTimeOffset? haltedAt = null;

        for (var t = start; t < input.End; t += length)
        {
            var isLast = t + length >= input.End;
            var bars = symbols.Where(s => byTime[s].ContainsKey(t)).ToDictionary(s => s, s => byTime[s][t]);

            // Gap rule: a missing expected candle resets the symbol's indicators and cancels its orders.
            foreach (var symbol in symbols.Where(s => states[s].Seen && !bars.ContainsKey(s)))
            {
                states[symbol].Instance.Reset();
                states[symbol].ValidBars = 0;
                pendingExits.Remove(symbol);
                pendingEntries.RemoveAll(e => e.Symbol == symbol);
            }

            // 1. Open: exits first, then entries.
            foreach (var symbol in pendingExits.Where(bars.ContainsKey).OrderBy(symbols.IndexOf).ToList())
            {
                cash += Close(symbol, bars[symbol].Open, costs.OpenSlippage, t, ExitReason.Signal);
            }

            pendingExits.Clear();
            var enteredNow = new HashSet<string>();
            foreach (var entry in pendingEntries.Where(e => bars.ContainsKey(e.Symbol)))
            {
                var fill = costs.BuyPrice(bars[entry.Symbol].Open, costs.OpenSlippage);
                var notional = Math.Min(entry.Notional, cash / (1 + costs.FeeRate));
                if (notional < costs.MinimumNotional)
                {
                    continue;
                }

                var quantity = notional / fill;
                var fee = costs.Fee(notional);
                cash -= notional + fee;
                var strategyStop = states[entry.Symbol].Instance.InitialStop(fill);
                positions[entry.Symbol] = new Position
                {
                    EntryTime = t,
                    EntryPrice = fill,
                    Quantity = quantity,
                    EntryFee = fee,
                    EntrySlippage = quantity * (fill - bars[entry.Symbol].Open),
                    StrategyStop = strategyStop,
                    Stop = ProtectiveStop.Effective(strategyStop, fill, risk.MaxPositionLoss),
                };
                enteredNow.Add(entry.Symbol);
            }

            pendingEntries.Clear();

            // 2. Intrabar protective stop.
            foreach (var symbol in positions.Keys.Where(bars.ContainsKey).OrderBy(symbols.IndexOf).ToList())
            {
                if (ProtectiveStop.Trigger(bars[symbol], positions[symbol].Stop, enteredNow.Contains(symbol)) is { } reference)
                {
                    cash += Close(symbol, reference, costs.StopSlippage, t, ExitReason.Stop);
                }
            }

            // 3. Close: indicators, equity, risk, signals.
            var closeTime = t + length;
            tracker?.Update(referenceByTime.GetValueOrDefault(t));
            foreach (var (symbol, bar) in bars)
            {
                var state = states[symbol];
                state.Seen = true;
                state.LastClose = bar.Close;
                state.Instance.Update(bar);
                state.ValidBars++;
                if (closeTime.TimeOfDay == TimeSpan.Zero)
                {
                    state.DayEndCloses[DateOnly.FromDateTime(t.UtcDateTime)] = bar.Close;
                }
            }

            if (isLast)
            {
                foreach (var symbol in positions.Keys.OrderBy(symbols.IndexOf).ToList())
                {
                    cash += Close(symbol, states[symbol].LastClose!.Value, costs.OpenSlippage, closeTime, ExitReason.EndOfTest);
                }
            }

            var marketValue = positions.Sum(p => p.Value.Quantity * states[p.Key].LastClose!.Value);
            var currentEquity = cash + marketValue;
            if (t < input.TradingStart)
            {
                continue;
            }

            equity.Add(new EquityPoint(closeTime, currentEquity, currentEquity == 0 ? 0 : marketValue / currentEquity));
            if (isLast)
            {
                break;
            }

            peak = Math.Max(peak, currentEquity);
            if (risk.DrawdownHaltEnabled && haltedAt is null && currentEquity <= peak * (1 - risk.MaxDrawdown))
            {
                haltedAt = closeTime;
            }

            if (currentEquity <= dayStartEquity * (1 - risk.DailyLossLimit))
            {
                noEntriesUntil = closeTime.AddHours(24);
            }

            if (closeTime.TimeOfDay == TimeSpan.Zero)
            {
                dayStartEquity = currentEquity;
            }

            var ready = bars.Keys.Where(s => states[s].ValidBars >= strategy.WarmupBars).ToList();
            foreach (var symbol in ready.Where(positions.ContainsKey))
            {
                var position = positions[symbol];
                if (states[symbol].Instance.ExitSignal)
                {
                    pendingExits.Add(symbol);
                }
                else if (states[symbol].Instance.TrailingStop is { } trailing && trailing > position.StrategyStop)
                {
                    position.StrategyStop = trailing;
                    position.Stop = ProtectiveStop.Effective(trailing, position.EntryPrice, risk.MaxPositionLoss);
                }
            }

            if (haltedAt is not null || closeTime < noEntriesUntil)
            {
                continue;
            }

            var remaining = positions.Keys.Where(s => !pendingExits.Contains(s)).ToList();
            var slots = risk.MaxOpenPositions - remaining.Count;
            // Latest completed UTC day: its day-end close is the close of its 20:00 candle (≤ closeTime).
            var asOf = DateOnly.FromDateTime(closeTime.UtcDateTime).AddDays(-1);
            var candidates = ready
                .Where(s => !positions.ContainsKey(s) && states[s].Instance.EntrySignal)
                .Select(s => (Symbol: s, Sigma: InverseVolatilityAllocation.Volatility(states[s].DayEndCloses, asOf)))
                .Where(c => c.Sigma is not null)
                .OrderBy(c => symbols.IndexOf(c.Symbol))
                .Take(Math.Max(slots, 0))
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var volatilities = candidates.ToDictionary(c => c.Symbol, c => c.Sigma!.Value);
            foreach (var symbol in remaining)
            {
                var sigma = InverseVolatilityAllocation.Volatility(states[symbol].DayEndCloses, asOf);
                if (sigma is { } value)
                {
                    volatilities[symbol] = value;
                }
            }

            var weights = InverseVolatilityAllocation.Weights(volatilities, risk);
            var openValue = remaining.Sum(s => positions[s].Quantity * states[s].LastClose!.Value);
            var targets = candidates.ToDictionary(c => c.Symbol, c => weights[c.Symbol] * currentEquity);
            var room = Math.Max(0, risk.MaxTotalExposure * currentEquity - openValue);
            var requested = targets.Values.Sum();
            var scale = requested > room && requested > 0 ? room / requested : 1m;
            pendingEntries.AddRange(candidates.Select(c => new PendingEntry(c.Symbol, targets[c.Symbol] * scale)));
        }

        return new BacktestResult(strategy.Name, risk.Variant, input.InitialCapital, trades, equity, haltedAt);

        decimal Close(string symbol, decimal reference, decimal slippage, DateTimeOffset time, ExitReason reason)
        {
            var position = positions[symbol];
            var fill = costs.SellPrice(reference, slippage);
            var proceeds = position.Quantity * fill;
            var fee = costs.Fee(proceeds);
            trades.Add(new Trade(symbol, position.EntryTime, position.EntryPrice, position.Quantity, time, fill, reason,
                position.EntryFee + fee, position.EntrySlippage + position.Quantity * (reference - fill)));
            positions.Remove(symbol);
            return proceeds - fee;
        }
    }
}
