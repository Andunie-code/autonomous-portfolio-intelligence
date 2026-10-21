using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>
/// The read-only inspection of a DSL instance (ADR-0020 §5, signal radar) agrees with the signals of the same
/// instance on every bar and does not change them; atom values follow the comparison and cross rules.
/// </summary>
public class DslInspectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Inspection_agrees_with_the_signals_on_every_bar_and_does_not_change_them(int seed)
    {
        var strategy = DslStrategy.FromJson(DslTests.Filtered);
        var series = Synthetic.RandomWalk("ETHUSDT", 900, seed);
        var btc = Synthetic.RandomWalk("BTCUSDT", 900, seed + 100);
        // Two identical instances; only one is inspected, so any side effect of the inspection would show up.
        var tracker = strategy.CreateReferenceTracker();
        var plain = strategy.CreateInstance(tracker);
        var inspectedTracker = strategy.CreateReferenceTracker();
        var inspected = strategy.CreateInstance(inspectedTracker);
        bool sawEntry = false, sawUnknown = false;
        for (var i = 0; i < series.Count; i++)
        {
            // Every 97th BTC bar is missing: the BTC atom is Unknown and the tracker resets (m3-protocol §2).
            var reference = i % 97 == 50 ? null : btc[i];
            tracker.Update(reference);
            inspectedTracker.Update(reference);
            plain.Update(series[i]);
            inspected.Update(series[i]);
            var view = DslStrategy.Inspect(inspected);

            Assert.Equal(plain.EntrySignal, inspected.EntrySignal);
            Assert.Equal(plain.ExitSignal, inspected.ExitSignal);
            Assert.Equal(inspected.EntrySignal, view.Entry == Tri.True);
            Assert.Equal(inspected.ExitSignal, view.Exit == Tri.True);
            Assert.Equal(["entry", "entry", "entry", "exit"], view.Atoms.Select(a => a.Side));
            Assert.Equal(["fast.value", "trend.adx", "btc.close", "close"], view.Atoms.Select(a => a.Left));
            Assert.Equal(["slow.value", "20", "regime.value", "slow.value×0.98"], view.Atoms.Select(a => a.Right));
            foreach (var atom in view.Atoms)
            {
                var expected = atom.Op switch
                {
                    "gt" => atom.Left0 is { } l && atom.Right0 is { } r ? (l > r ? Tri.True : Tri.False) : Tri.Unknown,
                    "lt" => atom.Left0 is { } l2 && atom.Right0 is { } r2 ? (l2 < r2 ? Tri.True : Tri.False) : Tri.Unknown,
                    "cross_above" => atom is { Left0: { } a0, Right0: { } b0, Left1: { } a1, Right1: { } b1 } ? (a0 > b0 && a1 <= b1 ? Tri.True : Tri.False) : Tri.Unknown,
                    _ => throw new InvalidOperationException(atom.Op),
                };
                Assert.Equal(expected, atom.Value);
            }

            if (reference is null)
            {
                Assert.Equal(Tri.Unknown, view.Atoms[2].Value);
                sawUnknown = true;
            }

            sawEntry |= view.Entry == Tri.True;
        }

        Assert.True(sawUnknown);
        Assert.True(sawEntry);
    }

    [Fact]
    public void Inspection_requires_a_dsl_instance()
    {
        Assert.Throws<ArgumentException>(() => DslStrategy.Inspect(new EmaTrendStrategy().CreateInstance()));
    }
}
