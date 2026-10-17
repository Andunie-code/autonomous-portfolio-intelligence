using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.Research.Evaluation;
using PortfolioIntelligence.Research.Families;
using PortfolioIntelligence.Researcher;

namespace PortfolioIntelligence.Research.Hypotheses;

/// <summary>
/// Settings of one research cycle, fixed by its protocol (m4-protocol v1): the global trial cap (§1), the trial count
/// used in G6 (§4), the gate thresholds (§6) and the reproducibility labels (§7).
/// </summary>
public sealed record ResearchCycleSettings(
    string ProtocolVersion,
    string ProtocolCommit,
    string EngineSha,
    string SnapshotHash,
    int GlobalTrialCap,
    int DeflatedSharpeTrials,
    GateThresholds Thresholds,
    int BootstrapSeed = 20260101);

/// <summary>
/// The Research module's implementation of the Researcher's tool port and of the deterministic hypothesis pipeline.
/// Every experiment: request guard on [U, C) (H8) → trial cap → one no-halt research-window backtest (v2 §1) →
/// trial record (family, counter; ADR-0015) → experiment record. Data comes only from the preloaded research-window
/// input (market.candles, &lt; C).
/// </summary>
public sealed class ResearchToolBackend(
    BacktestInput input,
    ResearchCycleSettings settings,
    TrialRegistry trials,
    ExperimentStore experiments,
    ExperimentRequestGuard guard,
    DatasetCatalog catalog,
    HypothesisStore hypotheses) : IResearchToolBackend, IHypothesisPipeline
{
    public const string Actor = "researcher";

    private readonly Dictionary<string, long> _hypothesisIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string DefinitionHash, DslDefinition Definition, BacktestResult Run)>> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<long, JsonObject> _results = [];

    public async Task<JsonNode> SearchExperimentsAsync(string query, int limit, CancellationToken ct) =>
        new JsonArray((await experiments.SearchAsync(query, limit, ct)).Select(Summary).ToArray());

    public async Task<JsonNode> GetDatasetCatalogAsync(CancellationToken ct) =>
        new JsonArray((await catalog.GetAsync(ct)).Select(e => (JsonNode)new JsonObject
        {
            ["symbol"] = e.Symbol, ["interval"] = e.Interval, ["start"] = e.Start.ToString("O", CultureInfo.InvariantCulture),
            ["end"] = e.End.ToString("O", CultureInfo.InvariantCulture), ["candles"] = e.Candles,
        }).ToArray());

    public async Task<JsonNode> SubmitExperimentAsync(ExperimentSubmission submission, CancellationToken ct)
    {
        try
        {
            await guard.EnsureAllowedAsync(Actor, "SubmitExperiment", input.TradingStart, input.End, ct);
        }
        catch (ResearchRequestRejectedException e)
        {
            return Error(e.Message);
        }

        if (!_hypothesisIds.TryGetValue(submission.HypothesisKey, out var hypothesisId))
        {
            return Error($"unknown hypothesis '{submission.HypothesisKey}'");
        }

        var definition = DslParser.Parse(submission.DefinitionJson);
        if (!await trials.IsTrialAsync(definition, ct) && await trials.CountAsync(ct) + 1 > settings.GlobalTrialCap)
        {
            return Error($"trial budget exhausted: the global trial counter may not exceed {settings.GlobalTrialCap} (m4-protocol §1)");
        }

        var strategy = new DslStrategy(definition);
        var run = new BacktestEngine(strategy, CostModel.V1, RiskSettings.NoHalt).Run(input);
        var outcome = await trials.RecordAsync(new TrialRequest(definition, Origin(submission.Origin), settings.ProtocolVersion, HypothesisId: hypothesisId), run, ct);
        var years = (input.End - input.TradingStart).TotalDays / 365.25;
        var metrics = Metrics.Compute(run, years);
        var experimentId = await experiments.AddAsync(new ExperimentRecord(strategy, RiskSettings.NoHalt.Variant, settings.ProtocolVersion,
            settings.ProtocolCommit, settings.SnapshotHash, settings.EngineSha, run.Hash(), metrics), ct);

        var hash = DslCanonical.DefinitionHash(definition);
        var points = _runs[submission.HypothesisKey];
        if (points.All(p => p.DefinitionHash != hash))
        {
            points.Add((hash, definition, run));
        }

        _results[experimentId] = new JsonObject
        {
            ["experimentId"] = experimentId,
            ["strategy"] = strategy.Name,
            ["definitionHash"] = hash,
            ["variant"] = RiskSettings.NoHalt.Variant,
            ["primaryMetric"] = Math.Round(ReturnStatistics.PrimaryMetric(run), 6),
            ["trades"] = run.Trades.Count,
            ["totalReturn"] = metrics.TotalReturn,
            ["maxDrawdown"] = metrics.MaxDrawdown,
            ["sharpe"] = Math.Round(metrics.Sharpe, 6),
            ["resultHash"] = run.Hash(),
        };
        return new JsonObject
        {
            ["accepted"] = true,
            ["experimentId"] = experimentId,
            ["trialId"] = outcome.TrialId,
            ["familyId"] = outcome.FamilyId,
            ["newTrial"] = outcome.NewTrial,
            ["assignedBy"] = outcome.AssignedBy,
        };
    }

    public async Task<JsonNode> GetExperimentResultAsync(long experimentId, CancellationToken ct) =>
        _results.TryGetValue(experimentId, out var cached) ? cached.DeepClone()
        : await experiments.GetAsync(experimentId, ct) is { } summary ? Summary(summary)
        : Error($"no experiment {experimentId}");

    public async Task RegisterHypothesisAsync(Hypothesis hypothesis, HypothesisProvenance provenance, CancellationToken ct)
    {
        _hypothesisIds[hypothesis.Key] = await hypotheses.EnsureAsync(hypothesis, provenance, settings.ProtocolVersion, ct);
        _runs.TryAdd(hypothesis.Key, []);
    }

    /// <summary>m4-protocol §5–§6: select the grid candidate, anchored G3, then G1–G6 on the no-halt run with N fixed.</summary>
    public Task<HypothesisVerdict> EvaluateHypothesisAsync(Hypothesis hypothesis, CancellationToken ct)
    {
        var runs = _runs.GetValueOrDefault(hypothesis.Key) ?? [];
        var byHash = runs.ToDictionary(r => r.DefinitionHash);
        var missing = hypothesis.Points.Where(p => !byHash.ContainsKey(p.DefinitionHash)).Select(p => p.Label).ToList();
        if (missing.Count > 0)
        {
            return Task.FromResult(new HypothesisVerdict(hypothesis.Key, hypothesis.Title, HypothesisCategory.Error, null,
                [$"points without a run: {string.Join(", ", missing)}"], new JsonObject()));
        }

        var points = hypothesis.Points.Select(p => new GridPointRun(p.Label, byHash[p.DefinitionHash].Run)).ToList();
        var selected = GridSelection.SelectCandidate(points);
        var anchored = GridSelection.Anchored(points);
        var definition = byHash[hypothesis.Points[selected].DefinitionHash].Definition;
        var strategy = new DslStrategy(definition);
        var evaluator = new ResearchGateEvaluator(CostModel.V1, RiskSettings.NoHalt, bootstrapSeed: settings.BootstrapSeed);
        var statistics = evaluator.Evaluate(strategy, input, new TrialContext(settings.DeflatedSharpeTrials, 0, EstimatorVariance: true), alwaysEvaluateNeighbourhood: true);
        var calendarShare = statistics.WalkForwardShare;
        statistics = statistics with { WalkForwardShare = anchored.Share };
        var failures = statistics.Failures(settings.Thresholds);
        var policy = new BacktestEngine(strategy, CostModel.V1, RiskSettings.Policy).Run(input);

        var details = new JsonObject
        {
            ["selectedPoint"] = points[selected].Label,
            ["definitionHash"] = strategy.DefinitionHash,
            ["structureHash"] = strategy.StructureHash,
            ["grid"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject
            {
                ["point"] = p.Label,
                ["primaryMetric"] = Round(ReturnStatistics.PrimaryMetric(p.Run)),
                ["trades"] = p.Run.Trades.Count,
                ["resultHash"] = p.Run.Hash(),
            }).ToArray()),
            ["noHalt"] = new JsonObject
            {
                ["primaryMetric"] = Round(statistics.PrimaryMetric),
                ["trades"] = statistics.Trades,
                ["lower95"] = Round(statistics.Bounds.Lower),
                ["anchoredWalkForwardShare"] = Round(anchored.Share),
                ["folds"] = new JsonArray(anchored.Folds.Select(f => (JsonNode)new JsonObject
                {
                    ["testYear"] = f.TestYear, ["selected"] = f.SelectedLabel, ["selectionMetric"] = Round(f.SelectionMetric), ["testMetric"] = Round(f.TestMetric),
                }).ToArray()),
                ["calendarYearShare"] = Round(calendarShare),
                ["neighbourhoodShare"] = statistics.NeighbourhoodShare is { } nb ? Round(nb) : null,
                ["multiAsset"] = statistics.MultiAsset,
                ["periodSharpe"] = Round(statistics.PeriodSharpe),
                ["tStatistic"] = Round(statistics.TStatistic),
                ["deflatedSharpeProbability"] = Round(statistics.DeflatedSharpeProbability),
                ["maxDrawdown"] = Round(ReturnStatistics.MaxDrawdown(points[selected].Run.InitialCapital, points[selected].Run.Equity)),
            },
            ["policy"] = new JsonObject
            {
                ["primaryMetric"] = Round(ReturnStatistics.PrimaryMetric(policy)),
                ["trades"] = policy.Trades.Count,
                ["maxDrawdown"] = Round(ReturnStatistics.MaxDrawdown(policy.InitialCapital, policy.Equity)),
                ["haltedAt"] = policy.HaltedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["resultHash"] = policy.Hash(),
            },
            ["gates"] = new JsonObject
            {
                ["G1"] = statistics.G1, ["G2"] = statistics.G2(settings.Thresholds), ["G3"] = statistics.G3(settings.Thresholds),
                ["G4"] = statistics.G4(settings.Thresholds), ["G5"] = statistics.G5, ["G6"] = statistics.G6(settings.Thresholds),
            },
        };
        var category = failures.Count == 0 ? HypothesisCategory.Candidate : HypothesisCategory.Rejected;
        return Task.FromResult(new HypothesisVerdict(hypothesis.Key, hypothesis.Title, category, points[selected].Label, failures, details));
    }

    private static double? Round(double value) => double.IsFinite(value) ? Math.Round(value, 6) : null;

    private static JsonNode Summary(ExperimentSummary e) => new JsonObject
    {
        ["experimentId"] = e.Id,
        ["strategy"] = e.StrategyName,
        ["strategyHash"] = e.StrategyHash,
        ["variant"] = e.Variant,
        ["protocol"] = e.ProtocolVersion,
        ["resultHash"] = e.ResultHash,
        ["metrics"] = JsonNode.Parse(e.MetricsJson),
    };

    private static TrialOrigin Origin(string origin) => origin switch
    {
        "llm" => TrialOrigin.Llm,
        "grid" => TrialOrigin.Grid,
        "human" => TrialOrigin.Human,
        "dev-assisted" => TrialOrigin.DevAssisted,
        _ => throw new JsonException($"unknown origin '{origin}'"),
    };

    private static JsonObject Error(string message) => new() { ["error"] = message };
}
