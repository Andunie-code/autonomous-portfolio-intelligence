using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Research.Cohorts;

/// <summary>
/// The research-window runs of one member: the policy run (M_R and δ, evaluatedRun = policy) and the no-halt
/// planning run (duration and power, m3-protocol v1 §5).
/// </summary>
public sealed record CohortMemberRuns(string DefinitionJson, BacktestResult Policy, BacktestResult NoHalt);

/// <summary>Cohort-level fields that are not computed: name, kind, release and the user's exemptions (ADR-0019 §1).</summary>
public sealed record CohortHeader(
    string Cohort,
    string Kind,
    string EngineSha,
    string ImageDigest,
    IReadOnlyList<string> Exemptions,
    bool ForceUnderpowered,
    string Notes);

/// <summary>
/// Builds a cohort manifest (architecture §6.3) with the protocol of Kohort 0 (m3-protocol v2; D26). It contains
/// no registered_at or A_c: the evaluator assigns them. <see cref="CohortHeader.ForceUnderpowered"/> marks an
/// operational cohort (D31): the power artefacts are still computed and written, but the cohort is flagged
/// underpowered so that it can at best be Inconclusive.
/// </summary>
public static class CohortManifestBuilder
{
    public const string ProtocolVersion = "m3-protocol v2";
    public const string ProtocolCommit = "5668d12";
    public const double Alpha = 0.05;
    public const double Power = 0.80;
    public const int BootstrapSeed = 20_261_016;

    public static JsonObject Build(CohortHeader header, IReadOnlyList<CohortMemberRuns> members, CoreUniverse universe, IReadOnlyDictionary<DateOnly, decimal> btcDayEnd)
    {
        if (members.Count is < 1 or > 3)
        {
            throw new ArgumentException("A cohort has 1 to 3 strategies (architecture §6.3).", nameof(members));
        }

        var strategies = new JsonArray();
        var months = new List<int>();
        var planned = new List<int>();
        var underpowered = header.ForceUnderpowered;
        foreach (var member in members)
        {
            var strategy = DslStrategy.FromJson(member.DefinitionJson);
            if (strategy.Source.Interval != "4h")
            {
                throw new ArgumentException($"{strategy.Name}: the interval must be 4h.");
            }

            var researchMetric = ReturnStatistics.PrimaryMetric(member.Policy);
            var delta = Math.Min(Math.Max(0.05, 0.50 * researchMetric), 0.15);
            // Worst case of the Holm correction within the cohort (Kohort 0: α/2 with two members).
            var plan = DurationPlanner.Plan(DailyReturns(member.NoHalt), member.NoHalt.Trades.Select(t => DateOnly.FromDateTime(t.EntryTime.UtcDateTime)).ToList(),
                btcDayEnd, delta, Alpha / members.Count, Power, BootstrapSeed);
            months.Add(plan.CohortMonths);
            planned.Add(plan.PlannedTrades);
            underpowered |= plan.Underpowered;
            strategies.Add(new JsonObject
            {
                ["definition"] = JsonNode.Parse(member.DefinitionJson),
                ["definitionHash"] = strategy.DefinitionHash,
                ["structureHash"] = strategy.StructureHash,
                ["researchMetric"] = Math.Round(researchMetric, 6),
                ["delta"] = delta,
                ["policyResultHash"] = member.Policy.Hash(),
                ["noHaltResultHash"] = member.NoHalt.Hash(),
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

        return new JsonObject
        {
            ["manifestVersion"] = "1",
            ["cohort"] = header.Cohort,
            ["kind"] = header.Kind,
            ["protocol"] = new JsonObject
            {
                ["version"] = ProtocolVersion, ["commit"] = ProtocolCommit,
                ["alpha"] = Alpha, ["power"] = Power, ["deltaFloor"] = 0.05, ["deltaShrink"] = 0.50, ["deltaCap"] = 0.15, ["deltaMode"] = "a",
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
            ["engineSha"] = header.EngineSha,
            ["imageDigest"] = header.ImageDigest,
            ["costModel"] = CostModel.V1.Version,
            ["allocation"] = "allocation v1 (inverse volatility, 30 days)",
            // The manifest's own risk block (D7, D25); the D29 cluster cap is operational only and not written here.
            ["risk"] = new JsonObject
            {
                ["maxOpenPositions"] = 5, ["maxAssetWeight"] = 0.20, ["maxTotalExposure"] = 0.80,
                ["dailyLossLimit"] = 0.03, ["maxDrawdownHalt"] = 0.15, ["maxPositionLoss"] = 0.10,
            },
            ["initialCapital"] = 10000,
            ["power"] = new JsonObject { ["months"] = months.Max(), ["plannedTrades"] = planned.Max(), ["underpowered"] = underpowered },
            ["dataQuality"] = "m1-data-policy v1.2 §6 applied to the window",
            ["secondaryMetrics"] = new JsonArray("excess vs BTC buy-and-hold", "excess vs equal-weight basket", "total return", "CAGR", "max drawdown", "Sharpe", "trades", "win rate", "exposure", "turnover"),
            ["exemptions"] = new JsonArray(header.Exemptions.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
            ["notes"] = header.Notes,
        };
    }

    public static string Text(JsonObject manifest) => manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>Daily log returns of the equity at UTC day ends (the close of the 20:00 candle).</summary>
    public static Dictionary<DateOnly, double> DailyReturns(BacktestResult result)
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
