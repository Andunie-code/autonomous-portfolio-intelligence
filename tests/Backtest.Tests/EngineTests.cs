using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using static PortfolioIntelligence.Backtest.Tests.Support.Synthetic;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>Engine mechanics against docs/m2-protocol.md §2 and §6–§9, with scripted signals on synthetic data.</summary>
public class EngineTests
{
    private const int History = -240; // 40 days before the trading start, so 30-day volatility is defined.

    /// <summary>Price alternates by UTC day (constant within a day) so day-end volatility is non-zero.</summary>
    private static List<Candle> Series(string symbol, int bars, Func<int, decimal>? price = null) =>
        Enumerable.Range(History, bars - History).Select(i =>
        {
            var p = price?.Invoke(i) ?? 100m * (Math.Abs(Math.Floor(i / 6.0)) % 2 == 0 ? 1m : 1.02m);
            return Bar(symbol, At(i), p, p * 1.01m, p * 0.99m, p);
        }).ToList();

    private static BacktestInput Input(params List<Candle>[] series) => new(
        series.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s),
        series.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2),
        Start,
        series.Max(s => s[^1].OpenTime) + TimeSpan.FromHours(4));

    private static ScriptedStrategy Scripted(int[] entries, int[] exits, decimal stop = 0.5m, int warmup = 1) =>
        new(entries.Select(At).ToHashSet(), exits.Select(At).ToHashSet(), stop, warmup);

    [Fact]
    public void Signals_fill_at_the_next_open_with_slippage_and_fees_and_the_books_balance()
    {
        var candles = Series("AUSDT", 30);
        var result = new BacktestEngine(Scripted([0], [10]), CostModel.V1, RiskSettings.Policy).Run(Input(candles));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(At(1), trade.EntryTime);
        Assert.Equal(candles.Single(c => c.OpenTime == At(1)).Open * 1.0005m, trade.EntryPrice);
        Assert.Equal(At(11), trade.ExitTime);
        Assert.Equal(candles.Single(c => c.OpenTime == At(11)).Open * 0.9995m, trade.ExitPrice);
        Assert.Equal(ExitReason.Signal, trade.Reason);
        Assert.Equal(2000m, trade.Quantity * trade.EntryPrice);
        Assert.Equal(Math.Round(result.InitialCapital + trade.NetProfit, 12), Math.Round(result.FinalEquity, 12));
        Assert.True(trade.Fees > 0 && trade.SlippageCost > 0);
    }

    [Fact]
    public void Protective_stop_exits_inside_the_candle_with_stop_slippage()
    {
        var candles = Series("AUSDT", 30, _ => 100m);
        var withDip = candles.Select(c => c.OpenTime == At(5) ? c with { Low = 90m } : c).ToList();
        // σ would be zero on constant prices; give the history a day-to-day pattern.
        withDip = withDip.Select(c => c.OpenTime < Start ? c with { Open = Day(c), High = Day(c) * 1.01m, Low = Day(c) * 0.99m, Close = Day(c) } : c).ToList();

        var result = new BacktestEngine(Scripted([0], [], stop: 0.05m), CostModel.V1, RiskSettings.Policy).Run(Input(withDip));

        var trade = Assert.Single(result.Trades);
        var stop = trade.EntryPrice * 0.95m;
        Assert.Equal(ExitReason.Stop, trade.Reason);
        Assert.Equal(At(5), trade.ExitTime);
        Assert.Equal(stop * 0.998m, trade.ExitPrice);

        static decimal Day(Candle c) => (c.OpenTime - Start).Days % 2 == 0 ? 100m : 102m;
    }

    [Fact]
    public void A_missing_candle_resets_the_strategy_until_the_warm_up_is_filled_again()
    {
        var candles = Series("AUSDT", 40).Where(c => c.OpenTime != At(3)).ToList();

        var blocked = new BacktestEngine(Scripted([5], [], warmup: 5), CostModel.V1, RiskSettings.Policy).Run(Input(candles));
        var allowed = new BacktestEngine(Scripted([10], [], warmup: 5), CostModel.V1, RiskSettings.Policy).Run(Input(candles));

        Assert.Empty(blocked.Trades);
        Assert.Equal(At(11), Assert.Single(allowed.Trades).EntryTime);
    }

    [Fact]
    public void At_most_five_positions_are_opened_in_core_rank_order()
    {
        var series = Enumerable.Range(1, 7).Select(i => Series($"S{i}USDT", 20)).ToArray();
        var result = new BacktestEngine(Scripted([0], []), CostModel.V1, RiskSettings.Policy).Run(Input(series));

        Assert.Equal(new[] { "S1USDT", "S2USDT", "S3USDT", "S4USDT", "S5USDT" }, result.Trades.Select(t => t.Symbol).Order());
        Assert.All(result.Trades, t => Assert.Equal(ExitReason.EndOfTest, t.Reason));
        Assert.True(result.Equity.All(p => p.Exposure <= 0.80m + 0.01m));
    }

    [Fact]
    public void Drawdown_halt_blocks_new_entries_only_in_the_policy_run()
    {
        // Full-size position, wide stops; price falls 20 % after entry and then recovers.
        var aggressive = new RiskSettings(5, 1m, 1m, 0.99m, 0.15m, true, 0.99m);
        var candles = Series("AUSDT", 60, i => i is > 2 and < 20 ? 80m : DayPrice(i));
        var strategy = Scripted([0, 30], [25], stop: 0.99m);

        var policy = new BacktestEngine(strategy, CostModel.V1, aggressive).Run(Input(candles));
        var noHalt = new BacktestEngine(strategy, CostModel.V1, aggressive with { DrawdownHaltEnabled = false }).Run(Input(candles));

        Assert.NotNull(policy.HaltedAt);
        Assert.Single(policy.Trades);
        Assert.Null(noHalt.HaltedAt);
        Assert.Equal(2, noHalt.Trades.Count);
        Assert.Equal("policy", RiskSettings.Policy.Variant);
        Assert.Equal("no-halt", RiskSettings.NoHalt.Variant);

        static decimal DayPrice(int i) => 100m * (Math.Abs(Math.Floor(i / 6.0)) % 2 == 0 ? 1m : 1.02m);
    }

    [Fact]
    public void Daily_loss_limit_blocks_entries_for_24_hours()
    {
        var settings = new RiskSettings(5, 1m, 1m, 0.03m, 0.99m, false, 0.99m);
        var candles = Series("AUSDT", 40, i => i is >= 2 and < 4 ? 90m : 100m * (Math.Abs(Math.Floor(i / 6.0)) % 2 == 0 ? 1m : 1.02m));
        var second = Series("BUSDT", 40);

        var result = new BacktestEngine(Scripted([0, 5, 12], [4]), CostModel.V1, settings).Run(Input(candles, second));

        // A drops 10 % on bar 2 (≥ 3 % daily loss at that close): the entry signal at bar 5 is blocked, the one at 12 is not.
        Assert.DoesNotContain(result.Trades, t => t.EntryTime == At(6));
        Assert.Contains(result.Trades, t => t.EntryTime == At(13));
    }

    [Fact]
    public void Same_inputs_give_the_same_result_hash_and_later_data_does_not_change_the_past()
    {
        var symbols = Enumerable.Range(1, 5).Select(i => (IReadOnlyList<Candle>)RandomWalk($"R{i}USDT", 1500, i).Select(c => c with { OpenTime = c.OpenTime.AddHours(4 * History) }).ToList()).ToList();
        var input = new BacktestInput(symbols.ToDictionary(s => s[0].Symbol), symbols.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2), Start, symbols[0][^1].OpenTime.AddHours(4));

        foreach (IStrategy strategy in new IStrategy[] { new EmaTrendStrategy(), new SupertrendStrategy() })
        {
            var first = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input);
            var second = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input);
            Assert.Equal(first.Hash(), second.Hash());
            Assert.NotEmpty(first.Trades);

            var cut = At(700);
            var altered = new BacktestInput(
                input.Candles.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Candle>)kv.Value.Select(c => c.OpenTime >= cut ? c with { Close = c.Close * 0.5m, Low = Math.Min(c.Low, c.Close * 0.5m), Open = c.Open * 0.5m, High = Math.Max(c.Open * 0.5m, c.Close * 0.5m) * 1.01m } : c).ToList()),
                input.CoreRank, input.TradingStart, input.End);
            var changed = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(altered);

            Assert.Equal(first.Equity.TakeWhile(p => p.Time <= cut), changed.Equity.TakeWhile(p => p.Time <= cut));
            Assert.Equal(first.Trades.Where(t => t.ExitTime < cut), changed.Trades.Where(t => t.ExitTime < cut));
        }
    }

    [Fact]
    public void Accounting_invariant_holds_on_random_data()
    {
        var symbols = Enumerable.Range(1, 6).Select(i => (IReadOnlyList<Candle>)RandomWalk($"R{i}USDT", 1200, 100 + i).Select(c => c with { OpenTime = c.OpenTime.AddHours(4 * History) }).ToList()).ToList();
        var input = new BacktestInput(symbols.ToDictionary(s => s[0].Symbol), symbols.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2), Start, symbols[0][^1].OpenTime.AddHours(4));

        foreach (IStrategy strategy in new IStrategy[] { new EmaTrendStrategy(), new SupertrendStrategy() })
        {
            var result = new BacktestEngine(strategy, CostModel.V1, RiskSettings.Policy).Run(input);

            // Exact up to decimal rounding (28 significant digits).
            Assert.Equal(Math.Round(result.InitialCapital + result.Trades.Sum(t => t.NetProfit), 12), Math.Round(result.FinalEquity, 12));
            Assert.All(result.Trades, t => Assert.True(t.Fees >= 0 && t.SlippageCost >= 0 && t.ExitTime >= t.EntryTime));
            Assert.All(result.Equity, p => Assert.InRange(p.Exposure, 0m, 0.80m + 0.05m));
        }
    }
}
