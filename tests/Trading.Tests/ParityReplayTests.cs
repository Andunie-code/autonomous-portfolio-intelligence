using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>Parity replay (m5-protocol §5.1) on synthetic candles: paper and kernel L1/L2 identical across restarts.</summary>
public class ParityReplayTests
{
    [Fact]
    public async Task Paper_cycles_with_restarts_reproduce_the_kernel_streams_and_l3_compares_normalised_values()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cohort-0.json"));
        var manifest = CohortManifestReader.Read(json);
        var start = new DateTimeOffset(2021, 7, 5, 0, 0, 0, TimeSpan.Zero);
        var tradingStart = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2022, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var bars = (int)((end - start).TotalHours / 4);
        var candles = manifest.Pairs.Select((p, i) => (p.Symbol, Series: (IReadOnlyList<Candle>)LiveSynthetic.Walk(p.Symbol, start, bars, 700 + i)))
            .ToDictionary(x => x.Symbol, x => x.Series);

        var report = await ParityReplay.RunAsync(json, 1, candles, tradingStart, end, TimeSpan.FromDays(60), default);

        Assert.True(report.Passed, report.FirstDifference);
        Assert.True(report.Restarts >= 3);
        Assert.True(report.Fills > 20);
        Assert.Equal(report.KernelL1Hash, report.PaperL1Hash);
        Assert.Equal(report.KernelL2Hash, report.PaperL2Hash);
        Assert.Equal(2, report.L3.Count);
        Assert.All(report.L3, s => Assert.InRange(s.TimingMatchRatio, 0, 1));
        Assert.Equal(report.Hash(), (await ParityReplay.RunAsync(json, 1, candles, tradingStart, end, TimeSpan.FromDays(60), default)).Hash());
    }
}
