using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.Research.Evaluation;
using PortfolioIntelligence.Research.Families;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>Research gates, calibration selection and family identity (m3-protocol §6–§8; testing-strategy §4).</summary>
public class EvaluationPipelineTests
{
    [Fact]
    public void Walk_forward_share_counts_calendar_years_with_positive_returns()
    {
        var points = new List<EquityPoint>();
        var equity = 10_000m;
        for (var t = ResearchCutoff.U.AddHours(4); t <= ResearchCutoff.C; t = t.AddHours(4))
        {
            // Close times: the candle opening at 2022-12-31T20:00 closes at 2023-01-01T00:00 and belongs to 2022.
            equity += t.AddTicks(-1).Year == 2023 ? -1m : 1m;
            points.Add(new EquityPoint(t, equity, 0));
        }

        var result = new BacktestResult("s", "policy", 10_000m, [], points, null);

        Assert.Equal(0.75, ResearchGateEvaluator.WalkForwardShare(result));
    }

    [Fact]
    public void Multi_asset_gate_needs_breadth_and_no_dominant_pair()
    {
        static Trade T(string symbol, decimal profit) =>
            new(symbol, Synthetic.At(0), 100m, 1m, Synthetic.At(1), 100m + profit, ExitReason.Signal, 0m, 0m);
        BacktestResult R(params Trade[] trades) => new("s", "policy", 10_000m, trades, [], null);

        Assert.True(ResearchGateEvaluator.MultiAsset(R(T("A", 10), T("B", 10), T("C", -5))));
        Assert.False(ResearchGateEvaluator.MultiAsset(R(T("A", 90), T("B", 10), T("C", -5))));
        Assert.False(ResearchGateEvaluator.MultiAsset(R(T("A", 10), T("B", -1), T("C", -1), T("D", -1))));
        Assert.False(ResearchGateEvaluator.MultiAsset(R()));
    }

    [Fact]
    public void Parameter_neighbourhood_moves_each_numeric_parameter_by_ten_percent()
    {
        var definition = DslParser.Parse(DslBaselines.EmaTrendJson);

        var neighbours = ParameterNeighbourhood.Neighbours(definition);

        // EMA 50 ± 5, EMA 100 ± 10, ATR 14 ± 1, multiple 3 × 0.9 / 1.1.
        Assert.Equal(8, neighbours.Count);
        Assert.Contains(neighbours, n => n.Indicators.Any(i => i.Params["period"] == 45m));
        Assert.Contains(neighbours, n => n.Indicators.Any(i => i.Params["period"] == 110m));
        Assert.Contains(neighbours, n => n.Indicators.Any(i => i.Type == "atr" && i.Params["period"] == 13m));
        Assert.Contains(neighbours, n => n.Stop!.Multiple == 3.3m);
        Assert.All(neighbours, n => Assert.Equal(DslCanonical.StructureHash(definition), DslCanonical.StructureHash(n)));
    }

    [Fact]
    public void Random_null_strategies_are_valid_and_reproducible()
    {
        var a = new RandomStrategies(5);
        var b = new RandomStrategies(5);
        var first = Enumerable.Range(0, 50).Select(_ => DslCanonical.DefinitionHash(a.Next())).ToList();
        var second = Enumerable.Range(0, 50).Select(_ => DslCanonical.DefinitionHash(b.Next())).ToList();

        Assert.Equal(first, second);
        Assert.True(first.Distinct().Count() > 45);
    }

    [Fact]
    public void Calibration_selection_prefers_the_highest_true_positive_rate_then_the_stricter_thresholds()
    {
        GridOutcome O(int n, double wf, double nb, double dsr, double fp, double tp) => new(new GateThresholds(n, wf, nb, dsr), fp, tp, fp <= 0.05 && tp >= 0.8);
        var grid = new[]
        {
            O(20, 0.5, 0.5, 0.90, 0.10, 0.99), // not admissible: FP too high
            O(20, 0.5, 0.5, 0.95, 0.04, 0.90),
            O(30, 0.5, 0.5, 0.95, 0.03, 0.90),
            O(20, 0.75, 0.5, 0.95, 0.02, 0.85),
        };

        Assert.Equal(new GateThresholds(30, 0.5, 0.5, 0.95), CalibrationRunner.Select(grid));
        Assert.Null(CalibrationRunner.Select([O(20, 0.5, 0.5, 0.9, 0.2, 0.9)]));
    }

    [Fact]
    public void Gate_statistics_detect_a_planted_edge_and_stay_quiet_on_a_null_universe()
    {
        var evaluator = new ResearchGateEvaluator(CostModel.V1, RiskSettings.NoHalt, bootstrapReplicates: 500);
        var strategy = DslStrategy.FromJson(CalibrationRunner.TruePositiveJson);

        var edge = evaluator.Measure(strategy, SyntheticMarkets.Trending(20_001, 0.12, 300, 0.5));
        var none = evaluator.Measure(strategy, SyntheticMarkets.Null(10_001));

        Assert.True(edge.G1 && edge.Bounds.Lower > 0 && edge.Trades > 50);
        Assert.True(none.PrimaryMetric < edge.PrimaryMetric);
    }

    [Fact]
    public async Task Parameter_change_keeps_the_family_and_a_copy_is_caught_by_similarity()
    {
        var store = new InMemoryFamilyStore();
        var registry = new TrialRegistry(store, new string('0', 64));
        var input = Universe();
        var baseline = DslParser.Parse(DslBaselines.EmaTrendJson);

        var first = await registry.RecordAsync(new TrialRequest(baseline, TrialOrigin.DevAssisted, "m3-protocol v1"), Run(baseline, input), default);
        var again = await registry.RecordAsync(new TrialRequest(baseline, TrialOrigin.DevAssisted, "m3-protocol v1"), Run(baseline, input), default);
        var tuned = DslParser.Parse(DslBaselines.EmaTrendJson.Replace("\"period\": 50", "\"period\": 45"));
        var tunedOutcome = await registry.RecordAsync(new TrialRequest(tuned, TrialOrigin.Grid, "m3-protocol v1", ClaimedFamilyId: 999), Run(tuned, input), default);
        // A copy disguised by an always-true filter: a new structure hash with identical trades (positive control).
        var copy = DslParser.Parse(DslBaselines.EmaTrendJson.Replace(
            "\"entry\": { \"op\": \"cross_above\", \"args\": [ { \"ref\": \"fast.value\" }, { \"ref\": \"slow.value\" } ] },",
            "\"entry\": { \"op\": \"and\", \"args\": [ { \"op\": \"cross_above\", \"args\": [ { \"ref\": \"fast.value\" }, { \"ref\": \"slow.value\" } ] }, { \"op\": \"gt\", \"args\": [ { \"price\": \"close\" }, { \"const\": 0 } ] } ] },"));
        Assert.NotEqual(DslCanonical.StructureHash(baseline), DslCanonical.StructureHash(copy));
        var copyOutcome = await registry.RecordAsync(new TrialRequest(copy, TrialOrigin.Llm, "m3-protocol v1"), Run(copy, input), default);
        var different = DslParser.Parse(DslBaselines.SupertrendJson);
        var differentOutcome = await registry.RecordAsync(new TrialRequest(different, TrialOrigin.Human, "m3-protocol v1"), Run(different, input), default);

        Assert.True(first.NewFamily);
        Assert.False(again.NewTrial);
        Assert.Equal(first.FamilyId, tunedOutcome.FamilyId);
        Assert.Equal("structure", tunedOutcome.AssignedBy);
        Assert.Equal(first.FamilyId, copyOutcome.FamilyId);
        Assert.Equal("similarity", copyOutcome.AssignedBy);
        Assert.True(differentOutcome.NewFamily);
        Assert.Equal(4, (await store.TrialSharpesAsync(default)).Count);
    }

    [Fact]
    public async Task Similarity_compares_only_with_representatives_and_memory_grows_with_families()
    {
        var store = new InMemoryFamilyStore();
        var random = new Random(3);
        for (var i = 0; i < 1000; i++)
        {
            await store.CreateFamilyAsync(Hash(i), Hash(i), new string('0', 64), new FamilyArtifacts(new Dictionary<string, (byte[], sbyte[])>
            {
                ["S0USDT"] = (Bits(random, 40), Exposure(random)),
            }), default);
        }

        var registry = new TrialRegistry(store, new string('0', 64));
        var candidate = DslParser.Parse(DslBaselines.SupertrendJson);
        await registry.RecordAsync(new TrialRequest(candidate, TrialOrigin.Grid, "m3-protocol v1"), Run(candidate, Universe()), default);

        Assert.Equal(1000, store.RepresentativeReads);
        Assert.Equal(1001, store.Families);
        var perFamily = new FamilyArtifacts(new Dictionary<string, (byte[], sbyte[])> { ["S0USDT"] = (Bits(random, 1), Exposure(random)) }).Bytes;
        Assert.InRange(perFamily, 5_000, 6_000); // ≈ 4.4 KB bitset + 1.5 KB exposure per pair, independent of trials
    }

    [Fact]
    public void Jaccard_and_correlation_recognise_identical_and_disjoint_artefacts()
    {
        var random = new Random(1);
        var exposure = Exposure(random);
        var a = new FamilyArtifacts(new Dictionary<string, (byte[], sbyte[])> { ["X"] = (Bits(random, 30), exposure) });
        var b = new FamilyArtifacts(new Dictionary<string, (byte[], sbyte[])> { ["Y"] = (Bits(random, 30), exposure) });

        Assert.Equal(1, FamilyArtifacts.Jaccard(a, a));
        Assert.Equal(1, FamilyArtifacts.Correlation(a, a), 12);
        Assert.Equal(0, FamilyArtifacts.Jaccard(a, b));
        Assert.True(FamilyArtifacts.Correlation(a, b) < 0.9);
    }

    [Fact]
    public void Strategy_hash_guard_refuses_a_definition_that_differs_from_the_manifest()
    {
        var hash = DslBaselines.EmaTrend().DefinitionHash;

        Assert.Equal(hash, StrategyHashGuard.LoadVerified(DslBaselines.EmaTrendJson, hash).DefinitionHash);
        Assert.Throws<StrategyHashMismatchException>(() => StrategyHashGuard.LoadVerified(DslBaselines.EmaTrendJson.Replace("\"period\": 50", "\"period\": 51"), hash));
    }

    private static BacktestInput Universe()
    {
        var symbols = Enumerable.Range(0, 4).Select(i => Synthetic.RandomWalk($"S{i}USDT", 8700, 70 + i)).ToList();
        return new BacktestInput(
            symbols.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s),
            symbols.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2),
            Synthetic.At(300),
            symbols[0][^1].OpenTime.AddHours(4));
    }

    private static BacktestResult Run(DslDefinition definition, BacktestInput input) =>
        new BacktestEngine(new DslStrategy(definition), CostModel.V1, RiskSettings.NoHalt).Run(input);

    private static string Hash(int i) => DslCanonical.Sha256(i.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static byte[] Bits(Random random, int count)
    {
        var bits = new byte[(FamilyArtifacts.Hours + 7) / 8];
        for (var i = 0; i < count; i++)
        {
            var hour = random.Next(FamilyArtifacts.Hours);
            bits[hour / 8] |= (byte)(1 << (hour % 8));
        }

        return bits;
    }

    private static sbyte[] Exposure(Random random) => Enumerable.Range(0, FamilyArtifacts.Days).Select(_ => (sbyte)random.Next(0, 128)).ToArray();
}
