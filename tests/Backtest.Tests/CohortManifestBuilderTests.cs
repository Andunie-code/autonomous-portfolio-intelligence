using System.Text.Json.Nodes;
using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Cohorts;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>
/// Cohort manifest builder (ADR-0019, D31) on synthetic research-window data: deterministic, readable by the
/// operational reader, all evaluator fields present, no server-assigned field, the operational flag and exemptions
/// written, and the manifest risk block without the D29 cluster cap.
/// </summary>
public class CohortManifestBuilderTests
{
    private static readonly string[] Members =
    [
        """{"dsl":"1","name":"m-sma-btc","interval":"4h","indicators":[{"id":"trend","type":"sma","params":{"period":50}},{"id":"btc_trend","type":"sma","params":{"period":100},"source":"btc"},{"id":"atr","type":"atr","params":{"period":14}}],"entry":{"op":"and","args":[{"op":"cross_above","args":[{"price":"close"},{"ref":"trend.value"}]},{"op":"gt","args":[{"price":"close","source":"btc"},{"ref":"btc_trend.value"}]}]},"exit":{"op":"cross_below","args":[{"price":"close"},{"ref":"trend.value"}]},"stop":{"type":"atr","ref":"atr.value","multiple":3,"trailing":false}}""",
        """{"dsl":"1","name":"m-macd","interval":"4h","indicators":[{"id":"macd","type":"macd","params":{"fast":12,"slow":26,"signal":9}},{"id":"atr","type":"atr","params":{"period":14}}],"entry":{"op":"cross_above","args":[{"ref":"macd.line"},{"ref":"macd.signal"}]},"exit":{"op":"cross_below","args":[{"ref":"macd.line"},{"ref":"macd.signal"}]},"stop":{"type":"atr","ref":"atr.value","multiple":3,"trailing":false}}""",
    ];

    private static readonly CohortHeader Header = new(
        "Kohort T", "operational", new string('a', 40), "sha256:" + new string('b', 64),
        ["operational-override (U, test): research gates not passed"], ForceUnderpowered: true, "test");

    private static (List<CohortMemberRuns> Runs, CoreUniverse Universe, Dictionary<DateOnly, decimal> BtcDayEnd) Fixture()
    {
        var universe = CoreUniverse.LoadDefault();
        const int bars = 6 * 700;
        var candles = universe.Ranks.Keys.Order(StringComparer.Ordinal).Select((s, i) => (s, Series: (IReadOnlyList<Candle>)Synthetic.RandomWalk(s, bars, 40 + i)))
            .ToDictionary(x => x.s, x => x.Series);
        var input = new BacktestInput(candles, universe.Ranks, Synthetic.At(6 * 60), Synthetic.At(bars), Reference: candles["BTCUSDT"]);
        var runs = Members.Select(json =>
        {
            var strategy = DslStrategy.FromJson(json);
            return new CohortMemberRuns(json, new BacktestEngine(strategy, CostModel.V1, RiskSettings.Policy).Run(input), new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input));
        }).ToList();
        var btcDayEnd = candles["BTCUSDT"].Where(c => c.CloseTime.TimeOfDay == TimeSpan.Zero).ToDictionary(c => DateOnly.FromDateTime(c.OpenTime.UtcDateTime), c => c.Close);
        return (runs, universe, btcDayEnd);
    }

    [Fact]
    public void The_manifest_is_deterministic_complete_and_operational()
    {
        var (runs, universe, btcDayEnd) = Fixture();
        var manifest = CohortManifestBuilder.Build(Header, runs, universe, btcDayEnd);
        var text = CohortManifestBuilder.Text(manifest);

        Assert.Equal(text, CohortManifestBuilder.Text(CohortManifestBuilder.Build(Header, runs, universe, btcDayEnd)));
        var operational = CohortManifestReader.Read(text);
        Assert.Equal(Members.Select(m => DslStrategy.FromJson(m).DefinitionHash), operational.Members.Select(m => m.DefinitionHash));
        Assert.Equal(20, operational.Pairs.Count);

        // Fields the evaluator's parser requires (src/HoldoutEvaluator/Manifest.cs); none of the server-assigned ones.
        foreach (var field in new[] { "manifestVersion", "protocol", "strategies", "pairs", "interval", "dataSource", "checksumPolicy", "costModel",
                     "allocation", "dataQuality", "engineSha", "imageDigest", "risk", "initialCapital", "power" })
        {
            Assert.True(manifest.ContainsKey(field), field);
        }

        foreach (var field in new[] { "registeredAt", "aC", "A_c", "windowEnd" })
        {
            Assert.False(manifest.ContainsKey(field), field);
        }

        Assert.Equal("1", manifest["manifestVersion"]!.GetValue<string>());
        Assert.Equal("policy", manifest["protocol"]!["evaluatedRun"]!.GetValue<string>());
        Assert.InRange(manifest["power"]!["months"]!.GetValue<int>(), 6, 12);
        Assert.True(manifest["power"]!["underpowered"]!.GetValue<bool>());
        Assert.Equal(Header.Exemptions, manifest["exemptions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal(["maxOpenPositions", "maxAssetWeight", "maxTotalExposure", "dailyLossLimit", "maxDrawdownHalt", "maxPositionLoss"],
            manifest["risk"]!.AsObject().Select(kv => kv.Key));
        Assert.All(manifest["strategies"]!.AsArray(), s =>
        {
            Assert.NotNull(s!["researchMetric"]);
            Assert.Equal(64, s["policyResultHash"]!.GetValue<string>().Length);
        });
    }

    [Fact]
    public void A_cohort_has_one_to_three_strategies()
    {
        var (runs, universe, btcDayEnd) = Fixture();
        Assert.Throws<ArgumentException>(() => CohortManifestBuilder.Build(Header, [], universe, btcDayEnd));
        Assert.Throws<ArgumentException>(() => CohortManifestBuilder.Build(Header, [.. runs, .. runs], universe, btcDayEnd));
    }
}
