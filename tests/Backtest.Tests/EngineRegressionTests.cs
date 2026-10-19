using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>
/// Regression lock for the M5 step-engine refactor (ADR-0018 §1): result hashes captured with the M2–M4 batch engine
/// on synthetic data (gaps, BTC reference, both risk variants, a tight-risk variant that trips the daily-loss and
/// drawdown rules). The real-data lock is the four M2 hashes of scripts/m3-parity.ps1.
/// </summary>
public class EngineRegressionTests
{
    private static BacktestInput Universe(int seed, bool gaps)
    {
        var symbols = Enumerable.Range(0, 6).Select(i => Synthetic.RandomWalk($"S{i}USDT", 1600, seed * 100 + i)).ToList();
        if (gaps)
        {
            // Remove a few candles in two symbols to exercise the gap rule (indicator reset, order cancellation).
            symbols[1].RemoveAll(c => c.OpenTime >= Synthetic.At(700) && c.OpenTime < Synthetic.At(703));
            symbols[3].RemoveAll(c => c.OpenTime == Synthetic.At(1100));
        }

        var btc = Synthetic.RandomWalk("BTCUSDT", 1600, seed * 100 + 99);
        return new BacktestInput(
            symbols.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s),
            symbols.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2),
            Synthetic.At(300),
            Synthetic.At(1600),
            Reference: btc);
    }

    internal static IEnumerable<(string Key, Func<string> Hash)> All()
    {
        var tight = new RiskSettings(3, 0.5m, 0.9m, 0.01m, 0.05m, true, 0.05m);
        var strategies = new (string Name, Func<IStrategy> Create)[]
        {
            ("ema-trend", () => new EmaTrendStrategy()),
            ("supertrend", () => new SupertrendStrategy()),
            ("filtered", () => DslStrategy.FromJson(DslTests.Filtered)),
        };
        foreach (var seed in new[] { 1, 2 })
        {
            foreach (var gaps in new[] { false, true })
            {
                var input = Universe(seed, gaps);
                foreach (var (name, create) in strategies)
                {
                    foreach (var (variant, risk) in new[] { ("policy", RiskSettings.Policy), ("no-halt", RiskSettings.NoHalt), ("tight", tight) })
                    {
                        yield return ($"{name}|{variant}|{seed}|{(gaps ? "gaps" : "full")}", () => new BacktestEngine(create(), CostModel.V1, risk).Run(input).Hash());
                    }
                }
            }
        }
    }

    [Fact]
    public void Batch_results_are_unchanged_by_the_step_engine()
    {
        var actual = All().Select(c => $"{c.Key}={c.Hash()}").ToList();
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "engine-regression.v1.txt")).Where(l => l.Length > 0).ToList();

        Assert.Equal(expected, actual);
    }
}
