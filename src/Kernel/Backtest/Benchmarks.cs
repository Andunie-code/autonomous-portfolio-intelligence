using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Kernel.Backtest;

/// <summary>
/// Benchmarks of docs/m2-protocol.md §10: buy-and-hold of one symbol, a monthly rebalanced equal-weight basket and
/// cash. Same cost model as the strategies; not subject to the portfolio risk rules.
/// </summary>
public static class Benchmarks
{
    public static BacktestResult Cash(DateTimeOffset start, DateTimeOffset end, TimeSpan length, decimal capital)
    {
        var equity = new List<EquityPoint>();
        for (var t = start; t < end; t += length)
        {
            equity.Add(new EquityPoint(t + length, capital, 0));
        }

        return new BacktestResult("cash", "benchmark", capital, [], equity, null);
    }

    public static BacktestResult BuyAndHold(string symbol, IReadOnlyList<Candle> candles, DateTimeOffset start, DateTimeOffset end, CostModel costs, decimal capital) =>
        Basket("buy-and-hold " + symbol, new Dictionary<string, IReadOnlyList<Candle>> { [symbol] = candles }, start, end, costs, capital, rebalanceMonthly: false);

    /// <summary>
    /// Equal weights at the first open of <paramref name="start"/>; if <paramref name="rebalanceMonthly"/>, back to
    /// equal weights at the first open of each month (costs only on the traded difference); sold at the final close.
    /// A symbol without a candle at a rebalance open is rebalanced at its next available open.
    /// </summary>
    public static BacktestResult Basket(string name, IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles,
        DateTimeOffset start, DateTimeOffset end, CostModel costs, decimal capital, bool rebalanceMonthly = true)
    {
        var interval = candles.Values.SelectMany(c => c).Select(c => c.Interval).Distinct().Single();
        var length = interval.FixedLength!.Value;
        var byTime = candles.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(c => c.OpenTime));
        var symbols = candles.Keys.Order(StringComparer.Ordinal).ToList();
        var quantity = symbols.ToDictionary(s => s, _ => 0m);
        var lastClose = new Dictionary<string, decimal>();
        var due = new HashSet<string>(symbols);
        var cash = capital;
        var trades = new List<Trade>();
        var equity = new List<EquityPoint>();
        var targetWeight = 1m / symbols.Count;
        var month = -1;

        for (var t = start; t < end; t += length)
        {
            if (rebalanceMonthly && t.Month != month && t > start)
            {
                due.UnionWith(symbols);
            }

            month = t.Month;
            var open = symbols.Where(s => byTime[s].ContainsKey(t)).ToDictionary(s => s, s => byTime[s][t].Open);
            if (due.Count > 0)
            {
                var value = cash + quantity.Sum(q => q.Value * (open.TryGetValue(q.Key, out var o) ? o : lastClose.GetValueOrDefault(q.Key)));

                // Sells before buys so that cash is available.
                foreach (var symbol in due.Where(open.ContainsKey).Order(StringComparer.Ordinal).ToList())
                {
                    var target = targetWeight * value / open[symbol];
                    if (quantity[symbol] > target)
                    {
                        var sell = quantity[symbol] - target;
                        var fill = costs.SellPrice(open[symbol], costs.OpenSlippage);
                        cash += sell * fill - costs.Fee(sell * fill);
                        quantity[symbol] = target;
                        trades.Add(new Trade(symbol, t, fill, -sell, t, fill, ExitReason.Signal, costs.Fee(sell * fill), sell * (open[symbol] - fill)));
                    }
                }

                foreach (var symbol in due.Where(open.ContainsKey).Order(StringComparer.Ordinal).ToList())
                {
                    var target = targetWeight * value / open[symbol];
                    if (quantity[symbol] < target)
                    {
                        var fill = costs.BuyPrice(open[symbol], costs.OpenSlippage);
                        var notional = Math.Min((target - quantity[symbol]) * open[symbol], cash / (1 + costs.FeeRate));
                        var buy = notional / fill;
                        cash -= notional + costs.Fee(notional);
                        quantity[symbol] += buy;
                        trades.Add(new Trade(symbol, t, fill, buy, t, fill, ExitReason.Signal, costs.Fee(notional), buy * (fill - open[symbol])));
                    }

                    due.Remove(symbol);
                }

                foreach (var symbol in due.Where(open.ContainsKey).ToList())
                {
                    due.Remove(symbol);
                }
            }

            foreach (var symbol in symbols.Where(s => byTime[s].ContainsKey(t)))
            {
                lastClose[symbol] = byTime[symbol][t].Close;
            }

            var isLast = t + length >= end;
            if (isLast)
            {
                foreach (var symbol in symbols.Where(s => quantity[s] > 0))
                {
                    var fill = costs.SellPrice(lastClose[symbol], costs.OpenSlippage);
                    cash += quantity[symbol] * fill - costs.Fee(quantity[symbol] * fill);
                    trades.Add(new Trade(symbol, t, fill, -quantity[symbol], t + length, fill, ExitReason.EndOfTest, costs.Fee(quantity[symbol] * fill), quantity[symbol] * (lastClose[symbol] - fill)));
                    quantity[symbol] = 0;
                }
            }

            var market = quantity.Sum(q => q.Value * lastClose.GetValueOrDefault(q.Key));
            var total = cash + market;
            equity.Add(new EquityPoint(t + length, total, total == 0 ? 0 : market / total));
        }

        return new BacktestResult(name, "benchmark", capital, trades, equity, null);
    }
}
