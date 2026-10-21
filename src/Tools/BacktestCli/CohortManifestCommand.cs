using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Cohorts;
using PortfolioIntelligence.Research.Families;
using PortfolioIntelligence.Researcher;

/// <summary>
/// cohort-manifest (ADR-0019; docs/cohort-1-preregistration.md): builds a cohort manifest from a spec file whose
/// members are points of the pre-registered M4 hypothesis list. Every member must already be a trial (no new trial is
/// recorded) and its no-halt run must reproduce the recorded M4 result hash. Reads market.candles (&lt; C) and the
/// research registries as research_app; writes only the manifest file. The release (image digest and engine SHA) is
/// the evaluator release registered in the holdout database, given by the user (scripts/m5-cohort1-release.ps1).
/// </summary>
internal static partial class CohortManifestCommand
{
    public static async Task<int> RunAsync(string connectionString, string repositoryRoot, string specPath, string imageDigest, string engineSha, string output)
    {
        if (!Digest().IsMatch(imageDigest) || !Sha().IsMatch(engineSha))
        {
            Console.Error.WriteLine("--image-digest must be sha256:<64 hex> or local-build:<40 hex>, --engine-sha a 40-hex git SHA.");
            return 1;
        }

        var spec = JsonNode.Parse(await File.ReadAllTextAsync(specPath))!.AsObject();
        var list = HypothesisList.Load(await File.ReadAllTextAsync(Path.Combine(repositoryRoot, spec["hypothesisList"]!.GetValue<string>())), ResearchM4.ListHash);
        var definitions = new List<string>();
        foreach (var member in spec["members"]!.AsArray())
        {
            var key = member!["hypothesis"]!.GetValue<string>();
            var label = member["point"]!.GetValue<string>();
            var point = list.Hypotheses.Single(h => h.Key == key).Points.Single(p => p.Label == label);
            if (point.DefinitionHash != member["definitionHash"]!.GetValue<string>())
            {
                Console.Error.WriteLine($"{key} {label}: the spec's definition hash does not match the hypothesis list ({point.DefinitionHash}).");
                return 2;
            }

            definitions.Add(point.DefinitionJson);
        }

        var universe = CoreUniverse.LoadDefault();
        var store = new CandleStore(connectionString);
        var range = new TimeRange(ResearchCutoff.RankingWindow.Start, ResearchCutoff.C);
        var candles = new Dictionary<string, IReadOnlyList<Candle>>();
        foreach (var symbol in universe.Ranks.Keys.Order(StringComparer.Ordinal))
        {
            candles[symbol] = await store.ReadAsync(symbol, CandleInterval.FourHours, range, CancellationToken.None);
        }

        var input = new BacktestInput(candles, universe.Ranks, ResearchCutoff.U, ResearchCutoff.C, Reference: candles["BTCUSDT"]);
        var btcDayEnd = candles["BTCUSDT"].Where(c => c.CloseTime.TimeOfDay == TimeSpan.Zero)
            .ToDictionary(c => DateOnly.FromDateTime(c.OpenTime.UtcDateTime), c => c.Close);
        var trials = new TrialRegistry(new NpgsqlFamilyStore(connectionString), universe.SnapshotHash);
        var experiments = new ExperimentStore(connectionString);
        var trialsBefore = await trials.CountAsync(CancellationToken.None);

        var runs = new List<CohortMemberRuns>();
        foreach (var json in definitions)
        {
            var definition = DslParser.Parse(json);
            var strategy = new DslStrategy(definition);
            if (!await trials.IsTrialAsync(definition, CancellationToken.None))
            {
                Console.Error.WriteLine($"{strategy.Name}: not a recorded trial; a cohort member must come from the research record.");
                return 2;
            }

            var noHalt = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input);
            var recorded = await experiments.ByStrategyAsync(strategy.DefinitionHash, RiskSettings.NoHalt.Variant, CancellationToken.None);
            if (recorded.Count == 0 || recorded.All(e => e.ResultHash != noHalt.Hash()))
            {
                Console.Error.WriteLine($"{strategy.Name}: the no-halt run {noHalt.Hash()[..12]} does not reproduce the recorded M4 result " +
                                        $"({string.Join(", ", recorded.Select(e => e.ResultHash[..12]))}).");
                return 2;
            }

            var policy = new BacktestEngine(strategy, CostModel.V1, RiskSettings.Policy).Run(input);
            runs.Add(new CohortMemberRuns(json, policy, noHalt));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{strategy.Name}: trial ok, no-halt {noHalt.Hash()[..12]} = M4, policy M_R {ReturnStatistics.PrimaryMetric(policy):P2} ({policy.Hash()[..12]})"));
        }

        var header = new CohortHeader(
            spec["cohort"]!.GetValue<string>(), spec["kind"]!.GetValue<string>(), engineSha, imageDigest,
            spec["exemptions"]!.AsArray().Select(e => e!.GetValue<string>()).ToList(), spec["forceUnderpowered"]?.GetValue<bool>() ?? false,
            spec["notes"]!.GetValue<string>());
        var manifest = CohortManifestBuilder.Build(header, runs, universe, btcDayEnd);
        var operational = CohortManifestReader.Read(manifest.ToJsonString());
        if (await trials.CountAsync(CancellationToken.None) != trialsBefore)
        {
            Console.Error.WriteLine("The global trial counter changed; nothing written.");
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, CohortManifestBuilder.Text(manifest), new UTF8Encoding(false));
        Console.WriteLine($"Manifest: {output}");
        Console.WriteLine($"Manifest hash {operational.Hash}; members {operational.Members.Count}; underpowered {manifest["power"]!["underpowered"]}; global trial counter {trialsBefore} (unchanged).");
        return 0;
    }

    [GeneratedRegex("^(sha256:[0-9a-f]{64}|local-build:[0-9a-f]{40})$")]
    private static partial Regex Digest();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha();
}
