using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Families;

/// <summary>
/// Kohort 0 (m3-protocol v1 §10, v2 §5): builds the manifest of the two DSL baselines from the research window
/// (M_R and δ from the policy run; duration and power from the no-halt planning run, v1 §5), records the two M2
/// trials (dev-assisted) and writes docs/cohorts/cohort-0.json. Reads only market.candles (&lt; C).
/// </summary>
internal static class Cohort0
{
    private const string ProtocolVersion = "m3-protocol v2";
    private const string ProtocolCommit = "5668d12";
    private const double Alpha = 0.05;
    private const int BootstrapSeed = 20_261_016;

    public static async Task<int> RunAsync(string connectionString, string repositoryRoot, string engineSha, string output)
    {
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
        var registry = new TrialRegistry(new NpgsqlFamilyStore(connectionString), universe.SnapshotHash);

        var strategies = new JsonArray();
        var months = new List<int>();
        var planned = new List<int>();
        var underpowered = false;
        foreach (var json in new[] { DslBaselines.EmaTrendJson, DslBaselines.SupertrendJson })
        {
            var definition = DslParser.Parse(json);
            var strategy = new DslStrategy(definition);
            var policy = new BacktestEngine(strategy, CostModel.V1, RiskSettings.Policy).Run(input);
            var noHalt = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input);
            var researchMetric = ReturnStatistics.PrimaryMetric(policy);
            var delta = Math.Min(Math.Max(0.05, 0.50 * researchMetric), 0.15);
            var plan = DurationPlanner.Plan(DailyReturns(noHalt), noHalt.Trades.Select(t => DateOnly.FromDateTime(t.EntryTime.UtcDateTime)).ToList(),
                btcDayEnd, delta, Alpha / 2, 0.80, BootstrapSeed);
            var trial = await registry.RecordAsync(new TrialRequest(definition, TrialOrigin.DevAssisted, "m2-protocol v1"), policy, CancellationToken.None);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{strategy.Name}: M_R {researchMetric:P2}, δ {delta:0.00}, worst σ_LR {plan.WorstSigma:0.0000}, f {plan.WorstTradesPerDay:0.000}/day, " +
                $"power months {(plan.PowerMonths?.ToString(CultureInfo.InvariantCulture) ?? "> 24")}, L_c {plan.CohortMonths}, planned trades {plan.PlannedTrades}, underpowered {plan.Underpowered}; " +
                $"trial {trial.TrialId} family {trial.FamilyId} ({trial.AssignedBy}{(trial.NewTrial ? "" : ", existing")}); policy result {policy.Hash()[..12]}"));

            months.Add(plan.CohortMonths);
            planned.Add(plan.PlannedTrades);
            underpowered |= plan.Underpowered;
            strategies.Add(new JsonObject
            {
                ["definition"] = JsonNode.Parse(json),
                ["definitionHash"] = strategy.DefinitionHash,
                ["structureHash"] = strategy.StructureHash,
                ["researchMetric"] = Math.Round(researchMetric, 6),
                ["delta"] = delta,
                ["policyResultHash"] = policy.Hash(),
                ["power"] = new JsonObject
                {
                    ["regimes"] = new JsonArray(plan.Regimes.Select(r => (JsonNode)new JsonObject
                    {
                        ["regime"] = r.Regime, ["days"] = r.Days, ["longRunSigma"] = Math.Round(r.LongRunSigma, 8), ["tradesPerDay"] = Math.Round(r.TradesPerDay, 6),
                    }).ToArray()),
                    ["worstSigma"] = double.IsNaN(plan.WorstSigma) ? null : Math.Round(plan.WorstSigma, 8),
                    ["worstTradesPerDay"] = Math.Round(plan.WorstTradesPerDay, 6),
                    ["powerMonths"] = plan.PowerMonths,
                    ["requiredMonths"] = plan.RequiredMonths,
                    ["cohortMonths"] = plan.CohortMonths,
                    ["plannedTrades"] = plan.PlannedTrades,
                    ["underpowered"] = plan.Underpowered,
                },
            });
        }

        var manifest = new JsonObject
        {
            ["manifestVersion"] = "1",
            ["cohort"] = "Kohort 0",
            ["kind"] = "seed",
            ["protocol"] = new JsonObject
            {
                ["version"] = ProtocolVersion, ["commit"] = ProtocolCommit,
                ["alpha"] = Alpha, ["power"] = 0.80, ["deltaFloor"] = 0.05, ["deltaShrink"] = 0.50, ["deltaCap"] = 0.15, ["deltaMode"] = "a",
                ["maxDrawdown"] = 0.25, ["bootstrapReplicates"] = 10_000, ["bootstrapSeed"] = BootstrapSeed,
                ["primaryMetric"] = "annualised-log-return (mean 4h log return x 2190)", ["benchmark"] = "cash", ["test"] = "one-sided stationary block bootstrap",
                ["correction"] = "Bonferroni within family, Holm within cohort", ["evaluatedRun"] = "policy",
            },
            ["strategies"] = strategies,
            ["pairs"] = new JsonArray(universe.Ranks.OrderBy(r => r.Value).Select(r => (JsonNode)new JsonObject { ["symbol"] = r.Key, ["category"] = "core" }).ToArray()),
            ["universe"] = universe.Version,
            ["datasetSnapshot"] = universe.SnapshotHash,
            ["interval"] = "4h",
            ["dataSource"] = "binance-spot-archive (data.binance.vision), monthly klines",
            ["checksumPolicy"] = "sha256-required",
            ["engineSha"] = engineSha,
            ["imageDigest"] = $"local-build:{engineSha}",
            ["costModel"] = CostModel.V1.Version,
            ["allocation"] = "allocation v1 (inverse volatility, 30 days)",
            ["risk"] = new JsonObject
            {
                ["maxOpenPositions"] = 5, ["maxAssetWeight"] = 0.20, ["maxTotalExposure"] = 0.80,
                ["dailyLossLimit"] = 0.03, ["maxDrawdownHalt"] = 0.15, ["maxPositionLoss"] = 0.10,
            },
            ["initialCapital"] = 10000,
            ["power"] = new JsonObject { ["months"] = months.Max(), ["plannedTrades"] = planned.Max(), ["underpowered"] = underpowered },
            ["dataQuality"] = "m1-data-policy v1.2 §6 applied to the window",
            ["secondaryMetrics"] = new JsonArray("excess vs BTC buy-and-hold", "excess vs equal-weight basket", "total return", "CAGR", "max drawdown", "Sharpe", "trades", "win rate", "exposure", "turnover"),
            ["exemptions"] = new JsonArray("pipeline-validation: exempt from the research gates (m3-protocol v1 §10)"),
            ["notes"] = "registered_at = commit time of this file (architecture §6.3 Kohort 0 exception); A_c is derived by the server. No image digest exists in M3; the evaluator release is recorded as local-build.",
        };

        var text = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text, new UTF8Encoding(false));
        Console.WriteLine($"Manifest: {output}");
        return 0;
    }

    /// <summary>Daily log returns of the equity at UTC day ends (the close of the 20:00 candle).</summary>
    private static Dictionary<DateOnly, double> DailyReturns(BacktestResult result)
    {
        var dayEnds = result.Equity.Where(p => p.Time.TimeOfDay == TimeSpan.Zero).ToList();
        var returns = new Dictionary<DateOnly, double>();
        var previous = (double)result.InitialCapital;
        foreach (var point in dayEnds)
        {
            var day = DateOnly.FromDateTime(point.Time.UtcDateTime).AddDays(-1);
            returns[day] = Math.Log((double)point.Equity / previous);
            previous = (double)point.Equity;
        }

        return returns;
    }
}
