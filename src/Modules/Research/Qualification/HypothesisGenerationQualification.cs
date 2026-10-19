using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.Research.Evaluation;

namespace PortfolioIntelligence.Research.Qualification;

/// <summary>
/// One HypothesisGeneration qualification item (docs/m4-hg-qualification-design.md §2): a pair of seeded synthetic
/// planted-edge universes — an exploration universe the model sees results from and an evaluation universe of the same
/// structure with another seed, where the proposal is gated. Synthetic only: no research-window data, no trial counter.
/// </summary>
/// <remarks>
/// Variant 2 (docs/m4-hg-qualification-design-v2.md §2) differs only in the exploration set: no well-configured strategy of
/// the planted (trend) family is shown. Variant 1 content is unchanged, so v1 set hashes stay reproducible.
/// </remarks>
public sealed record HgItem(string Id, int Seed, double Kappa, int MeanRegimeBars, double UpShare, int EvaluationSeedOffset, int Variant = 1)
{
    public const string Version = "hg-synthetic v1";
    public const string VersionV2 = "hg-synthetic v2";

    public string Content => Variant == 1
        ? string.Create(CultureInfo.InvariantCulture,
            $"{Version}; seed={Seed}; kappa={Kappa:R}; meanBars={MeanRegimeBars}; upShare={UpShare:R}; evalSeedOffset={EvaluationSeedOffset}")
        : string.Create(CultureInfo.InvariantCulture,
            $"{VersionV2}; seed={Seed}; kappa={Kappa:R}; meanBars={MeanRegimeBars}; upShare={UpShare:R}; evalSeedOffset={EvaluationSeedOffset}; exploration=misfit-v2");

    public QualificationItem ToQualificationItem() => new(Id, Content);

    public static HgItem Parse(QualificationItem item)
    {
        var fields = item.Content.Split(';', StringSplitOptions.TrimEntries).Skip(1)
            .Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1], StringComparer.Ordinal);
        var variant = item.Content.StartsWith(Version + ";", StringComparison.Ordinal) ? 1
            : item.Content.StartsWith(VersionV2 + ";", StringComparison.Ordinal) && fields.GetValueOrDefault("exploration") == "misfit-v2" ? 2
            : throw new FormatException($"Item {item.Id} is not a {Version} or {VersionV2} item.");

        return new HgItem(item.Id, int.Parse(fields["seed"], CultureInfo.InvariantCulture), double.Parse(fields["kappa"], CultureInfo.InvariantCulture),
            int.Parse(fields["meanBars"], CultureInfo.InvariantCulture), double.Parse(fields["upShare"], CultureInfo.InvariantCulture),
            int.Parse(fields["evalSeedOffset"], CultureInfo.InvariantCulture), variant);
    }

    /// <summary>Items with consecutive seeds and the calibrated planted-edge parameters (κ = 0.056, m3-calibration-results-v2).</summary>
    public static IReadOnlyList<HgItem> Range(int seedBase, int count, int variant = 1) =>
        Enumerable.Range(seedBase, count).Select(s => new HgItem(variant == 1 ? $"hg-{s}" : $"hg2-{s}", s, 0.056, 300, 0.5, 500_000, variant)).ToList();
}

/// <summary>One arm's outcome on one item, for the report (metrics only).</summary>
public sealed record HgArmRecord(
    string ItemId,
    string Arm,
    bool Success,
    bool ValidDefinition,
    string? DefinitionHash,
    bool DuplicatesExploration,
    IReadOnlyList<string> GateFailures,
    double? EvaluationMetric,
    string? Reason,
    IReadOnlyList<string> SafetyViolations,
    IReadOnlyList<string>? PrunedIndicators = null);

/// <summary>
/// Prompt, output schema and evidence for HypothesisGeneration on synthetic items (prompt version
/// <see cref="PromptVersion"/>). The model gets the DSL v1 whitelist and six exploration results and proposes one strategy;
/// no tools. Claims must cite exploration ids; a cited number must match the shown value.
/// </summary>
public static class HgPrompt
{
    public const string PromptVersion = "hg-synthetic-prompt v1";

    public static NeutralSchema Schema { get; } = NeutralSchema.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string","description":"short name of the hypothesis"},
          "rationale":{"type":"string","description":"why this strategy should work, based only on the evidence"},
          "definition":{"type":"string","description":"the DSL v1 strategy definition as JSON text"},
          "claims":{"type":"array","maxItems":6,"items":{"type":"object","properties":{
            "text":{"type":"string"},
            "critical":{"type":"boolean"},
            "evidenceRef":{"type":"string","description":"an exploration id such as exp:1"},
            "value":{"type":"number","description":"the cited primary metric value, exactly as shown"}},
            "required":["text","critical"],"additionalProperties":false}}},
         "required":["title","rationale","definition","claims"],"additionalProperties":false}
        """);

    public const string System = """
        You are a quantitative strategy researcher. You propose exactly one long-only spot strategy for a synthetic market,
        written in a restricted JSON strategy language (DSL v1). All data is synthetic; use only the evidence given.
        Reply only with JSON matching the response schema. Every claim that relies on an experiment must cite its id
        in evidenceRef; a cited number must equal the value shown.
        """;

    public static string Build(IReadOnlyList<(string Id, DslDefinition Definition, ExplorationMetrics Metrics)> exploration, string? rules = null)
    {
        var s = new StringBuilder();
        s.AppendLine("Market: 20 synthetic spot pairs (one is BTCUSDT and can be used as a reference series with \"source\":\"btc\"), 4h candles,");
        s.AppendLine("4 years of research data. Costs, position limits and a 10 % hard stop per position are applied by the backtest.");
        s.AppendLine("A proposal is judged on an unseen synthetic market of the same kind: cost-adjusted return > 0, at least 100 trades,");
        s.AppendLine("a positive lower confidence bound, consistency across years, robustness to ±10 % parameter changes, several");
        s.AppendLine("profitable pairs, and a deflated Sharpe ratio. Overfitting the exploration results does not help.");
        s.AppendLine();
        s.AppendLine("DSL v1 (strict; unknown fields are rejected):");
        s.AppendLine("""{"dsl":"1","name":"<a-z0-9- up to 64>","interval":"4h","indicators":[{"id":"<a-z0-9_ up to 16>","type":"<type>","params":{...},"source":"self|btc"}],""");
        s.AppendLine(""" "entry":<condition>,"exit":<condition>,"stop":<stop, optional>}""");
        s.AppendLine("Indicators (type: params [range] -> outputs):");
        foreach (var spec in DslCatalog.Indicators.Values.OrderBy(i => i.Type, StringComparer.Ordinal))
        {
            var parameters = string.Join(", ", spec.Parameters.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Name} [{p.Min}-{p.Max}{(p.Integer ? ", integer" : "")}]")));
            s.AppendLine(CultureInfo.InvariantCulture, $"- {spec.Type}: {parameters} -> {string.Join(", ", spec.Outputs)}");
        }

        s.AppendLine("Conditions: {\"op\":\"gt|gte|lt|lte|cross_above|cross_below\",\"args\":[operand, operand]}, {\"op\":\"and|or\",\"args\":[2-4 conditions]}, {\"op\":\"not\",\"args\":[condition]}; depth at most 3; at most 8 indicators.");
        s.AppendLine("Operands: {\"ref\":\"<id>.<output>\",\"lag\":0|1,\"mult\":x}, {\"price\":\"open|high|low|close|volume\",\"source\":\"self|btc\",\"lag\":0|1,\"mult\":x}, {\"const\":x}; mult in (0, 100].");
        s.AppendLine("Stop: {\"type\":\"percent\",\"pct\":0.01-0.10} | {\"type\":\"atr\",\"ref\":\"<atr id>.value\",\"multiple\":0.5-10,\"trailing\":false} | {\"type\":\"indicator\",\"ref\":\"<id>.<output>\",\"trailing\":true|false}.");
        s.AppendLine();
        s.AppendLine("Exploration results on the research data (M = annualised mean 4h log return after costs):");
        foreach (var (id, definition, m) in exploration)
        {
            s.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- {id}: M={m.PrimaryMetric:0.000000}, trades={m.Trades}, positiveYears={m.PositiveYears}/{m.Years}, profitablePairs={m.ProfitablePairs}/{m.TradedPairs}, maxDrawdown={m.MaxDrawdown:0.000}; definition: {DslJson.Serialize(definition)}"));
        }

        s.AppendLine();
        if (rules is not null)
        {
            s.AppendLine(rules);
            s.AppendLine();
        }

        s.AppendLine("Propose one new strategy (not a copy of an exploration definition).");
        return s.ToString();
    }
}

/// <summary>
/// Prompt v2 (design v2 §3): claims name the metric they cite (<c>metric</c> enum) so the validator checks the right number;
/// the prompt states that every declared indicator must be used. Exploration lines and the DSL description are those of v1.
/// </summary>
public static class HgPromptV2
{
    public const string PromptVersion = "hg-synthetic-prompt v2";

    public static NeutralSchema Schema { get; } = NeutralSchema.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string","description":"short name of the hypothesis"},
          "rationale":{"type":"string","description":"why this strategy should work, based only on the evidence"},
          "definition":{"type":"string","description":"the DSL v1 strategy definition as JSON text; every declared indicator is used"},
          "claims":{"type":"array","maxItems":6,"items":{"type":"object","properties":{
            "text":{"type":"string"},
            "critical":{"type":"boolean"},
            "evidenceRef":{"type":"string","description":"an exploration id such as exp:1"},
            "metric":{"type":"string","enum":["primaryMetric","trades","positiveYears","profitablePairs","maxDrawdown"],"description":"which shown number the value cites"},
            "value":{"type":"number","description":"the cited number exactly as shown for that metric"}},
            "required":["text","critical"],"additionalProperties":false}}},
         "required":["title","rationale","definition","claims"],"additionalProperties":false}
        """);

    public const string System = """
        You are a quantitative strategy researcher. You propose exactly one long-only spot strategy for a synthetic market,
        written in a restricted JSON strategy language (DSL v1). All data is synthetic; use only the evidence given.
        Reply only with JSON matching the response schema. A claim that relies on an experiment cites its id in evidenceRef;
        if it cites a number, it names the metric in "metric" and gives the number exactly as shown in "value".
        """;

    public const string Rules = """
        Rules:
        - Every indicator you declare must be referenced in entry, exit or stop; unused indicators are rejected.
        - Number citations: metric "primaryMetric" is M; "trades" is trades; "positiveYears" and "profitablePairs" are the
          number before the slash; "maxDrawdown" is maxDrawdown. Copy the value exactly as shown.
        """;

    public static string Build(IReadOnlyList<(string Id, DslDefinition Definition, ExplorationMetrics Metrics)> exploration) => HgPrompt.Build(exploration, Rules);
}

public sealed record ExplorationMetrics(double PrimaryMetric, int Trades, int PositiveYears, int Years, int ProfitablePairs, int TradedPairs, double MaxDrawdown)
{
    /// <summary>The value shown in the prompt (6 decimals); a cited claim is checked against it.</summary>
    public decimal ShownPrimaryMetric => Math.Round((decimal)PrimaryMetric, 6);

    /// <summary>Every number shown in the prompt, as shown (v2 claims name one of these).</summary>
    public IReadOnlyDictionary<string, decimal> ShownMetrics => new Dictionary<string, decimal>(StringComparer.Ordinal)
    {
        ["primaryMetric"] = ShownPrimaryMetric,
        ["trades"] = Trades,
        ["positiveYears"] = PositiveYears,
        ["profitablePairs"] = ProfitablePairs,
        ["maxDrawdown"] = Math.Round((decimal)MaxDrawdown, 3),
    };

    public static ExplorationMetrics Of(BacktestResult run)
    {
        var returns = ReturnStatistics.LogReturns(run.InitialCapital, run.Equity);
        var years = run.Equity.Select((p, i) => (Year: p.Time.AddTicks(-1).Year, Return: returns[i])).GroupBy(x => x.Year)
            .Select(g => ReturnStatistics.AnnualisedMean(g.Select(x => x.Return).ToList())).ToList();
        var pairs = run.Trades.GroupBy(t => t.Symbol).Select(g => g.Sum(t => t.NetProfit)).ToList();
        return new ExplorationMetrics(ReturnStatistics.AnnualisedMean(returns), run.Trades.Count, years.Count(y => y > 0), years.Count,
            pairs.Count(p => p > 0), pairs.Count, ReturnStatistics.MaxDrawdown(run.InitialCapital, run.Equity));
    }
}

/// <summary>
/// HypothesisGeneration on synthetic items (design §2–§4). Success of an arm on an item = its proposal passes all research
/// gates G1–G6 (v2 thresholds, no-halt run, DSR with N = 100) on the item's evaluation universe.
/// <list type="bullet">
/// <item>LLM arm: router in qualification mode, structured output, no tools, EvidenceValidator on the claims; anything but a
/// completed, non-degraded result with a valid DSL definition is a failure.</item>
/// <item>Baseline arm (deterministic grid): the best exploration definition by M, moved by its first parameter neighbour.</item>
/// </list>
/// Exploration: the two M2 baselines and four seeded random DSL strategies on the exploration universe.
/// </summary>
/// <remarks>
/// Variant 2 (design v2): prompt and schema v2 with named-metric claims, unused indicators pruned from the model's definition
/// before the strict parser (recorded), and an exploration set without a well-configured planted-family strategy.
/// </remarks>
public sealed class HypothesisGenerationTask(TimeProvider time, int bootstrapReplicates = 10_000, int variant = 1) : IQualificationTask
{
    /// <summary>Prompt version and output schema of a variant (a protocol must match them).</summary>
    public static (string PromptVersion, NeutralSchema Schema) Prompt(int variant) =>
        variant == 2 ? (HgPromptV2.PromptVersion, HgPromptV2.Schema) : (HgPrompt.PromptVersion, HgPrompt.Schema);

    public const int DeflatedSharpeTrials = 100;

    public static GateThresholds Thresholds { get; } = new(100, 0.75, 0.80, 0.90);

    private readonly Dictionary<string, Exploration> _explorations = new(StringComparer.Ordinal);
    private readonly List<HgArmRecord> _records = [];

    public TaskClass Task => TaskClass.HypothesisGeneration;

    public IReadOnlyList<HgArmRecord> Records => _records.ToList();

    public async Task<ArmOutcome> RunLlmAsync(QualificationSession session, QualificationItem item, CancellationToken ct)
    {
        var exploration = Explore(item);
        var evidence = exploration.Results.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        var resolver = new ExplorationEvidence(evidence, time.GetUtcNow().AddMinutes(-1));
        var ids = exploration.Results.Select(r => r.Id).ToList();
        var input = variant == 2
            ? new TaskInput(HgPromptV2.System, HgPromptV2.Build(exploration.Results), HgPromptV2.Schema, ids, [new EvidenceValidator(resolver)])
            : new TaskInput(HgPrompt.System, HgPrompt.Build(exploration.Results), HgPrompt.Schema, ids, [new EvidenceValidator(resolver)]);
        var result = await session.Router.ExecuteForQualificationAsync(session, input, ct);
        var violations = result.SafetyViolations.ToList();
        if (result.Outcome != TaskOutcome.Completed || result.Degraded || result.Output is null)
        {
            return Record(item, "llm", null, exploration, violations, result.Degraded ? "degraded" : result.Reason ?? result.Outcome.ToString());
        }

        // Independent re-check of the evidence invariant: an accepted claim may only cite an exploration id.
        foreach (var claim in result.Output["claims"]?.AsArray() ?? [])
        {
            if (claim?["evidenceRef"] is JsonValue reference && reference.TryGetValue<string>(out var id) && !evidence.ContainsKey(id))
            {
                violations.Add($"fabricated evidenceRef '{id}' passed the validator");
            }
        }

        DslDefinition? definition;
        var text = result.Output["definition"]!.GetValue<string>();
        IReadOnlyList<string> pruned = [];
        if (variant == 2)
        {
            (text, pruned) = DslNormalizer.PruneUnusedIndicators(text);
        }

        try
        {
            definition = DslParser.Parse(text);
        }
        catch (DslException e)
        {
            return Record(item, "llm", null, exploration, violations, "invalid DSL: " + string.Join("; ", e.Errors.Take(3)), validDefinition: false, pruned: pruned);
        }

        return Record(item, "llm", definition, exploration, violations, null, pruned: pruned);
    }

    public Task<ArmOutcome> RunBaselineAsync(QualificationItem item, CancellationToken ct)
    {
        var exploration = Explore(item);
        var best = exploration.Results.OrderByDescending(r => r.Metrics.PrimaryMetric).ThenBy(r => r.Id, StringComparer.Ordinal).First().Definition;
        var neighbour = ParameterNeighbourhood.Neighbours(best).FirstOrDefault() ?? best;
        return System.Threading.Tasks.Task.FromResult(Record(item, "baseline", neighbour with { Name = "grid-" + DslCanonical.DefinitionHash(neighbour)[..12] }, exploration, [], null));
    }

    private ArmOutcome Record(QualificationItem item, string arm, DslDefinition? definition, Exploration exploration, List<string> violations, string? reason,
        bool validDefinition = true, IReadOnlyList<string>? pruned = null)
    {
        if (definition is null)
        {
            _records.Add(new HgArmRecord(item.Id, arm, false, validDefinition && reason is null, null, false, [], null, reason, violations, pruned));
            return new ArmOutcome(false, violations);
        }

        var hash = DslCanonical.DefinitionHash(definition);
        var evaluator = new ResearchGateEvaluator(CostModel.V1, RiskSettings.NoHalt, bootstrapReplicates, 20_261_019);
        var statistics = evaluator.Evaluate(new DslStrategy(definition), exploration.Evaluation, new TrialContext(DeflatedSharpeTrials, 0, EstimatorVariance: true));
        var failures = statistics.Failures(Thresholds);
        var duplicate = exploration.Results.Any(r => DslCanonical.DefinitionHash(r.Definition) == hash);
        _records.Add(new HgArmRecord(item.Id, arm, failures.Count == 0, true, hash, duplicate, failures, statistics.PrimaryMetric, reason, violations, pruned));
        return new ArmOutcome(failures.Count == 0, violations);
    }

    private Exploration Explore(QualificationItem item)
    {
        if (_explorations.TryGetValue(item.Id, out var cached))
        {
            return cached;
        }

        var hg = HgItem.Parse(item);
        var universe = SyntheticMarkets.Trending(hg.Seed, hg.Kappa, hg.MeanRegimeBars, hg.UpShare);
        var evaluation = SyntheticMarkets.Trending(hg.Seed + hg.EvaluationSeedOffset, hg.Kappa, hg.MeanRegimeBars, hg.UpShare);
        var random = new RandomStrategies(hg.Seed);
        var definitions = hg.Variant == 2
            ? MisfitExploration.Select(DslParser.Parse).Concat(Enumerable.Range(0, 2).Select(_ => random.Next())).ToList()
            : new[] { DslParser.Parse(DslBaselines.EmaTrendJson), DslParser.Parse(DslBaselines.SupertrendJson) }
                .Concat(Enumerable.Range(0, 4).Select(_ => random.Next())).ToList();
        var results = definitions.Select((d, i) => ($"exp:{i + 1}", d,
            ExplorationMetrics.Of(new BacktestEngine(new DslStrategy(d), CostModel.V1, RiskSettings.NoHalt).Run(universe)))).ToList();
        var exploration = new Exploration(results, evaluation);
        _explorations[item.Id] = exploration;
        return exploration;
    }

    /// <summary>
    /// Variant 2 exploration (design v2 §2), fixed before any v2 run: two mean-reversion strategies (the wrong family for a
    /// trend market) and two trend and breakout strategies configured so they cannot hold a trend (very short periods and a
    /// 1 % stop), followed by two seeded random strategies. A ±10 % neighbour of any of them keeps its flaw.
    /// </summary>
    public static IReadOnlyList<string> MisfitExploration { get; } =
    [
        """{"dsl":"1","name":"rsi-reversion","interval":"4h","indicators":[{"id":"rsi","type":"rsi","params":{"period":14}}],"entry":{"op":"lt","args":[{"ref":"rsi.value"},{"const":30}]},"exit":{"op":"gt","args":[{"ref":"rsi.value"},{"const":55}]},"stop":{"type":"percent","pct":0.05}}""",
        """{"dsl":"1","name":"bollinger-reversion","interval":"4h","indicators":[{"id":"bb","type":"bollinger","params":{"period":20,"k":2}}],"entry":{"op":"lt","args":[{"price":"close"},{"ref":"bb.lower"}]},"exit":{"op":"gt","args":[{"price":"close"},{"ref":"bb.middle"}]},"stop":{"type":"percent","pct":0.05}}""",
        """{"dsl":"1","name":"fast-ema-tight-stop","interval":"4h","indicators":[{"id":"fast","type":"ema","params":{"period":3}},{"id":"slow","type":"ema","params":{"period":5}}],"entry":{"op":"cross_above","args":[{"ref":"fast.value"},{"ref":"slow.value"}]},"exit":{"op":"cross_below","args":[{"ref":"fast.value"},{"ref":"slow.value"}]},"stop":{"type":"percent","pct":0.01}}""",
        """{"dsl":"1","name":"short-breakout-tight-stop","interval":"4h","indicators":[{"id":"dc","type":"donchian","params":{"period":5}},{"id":"dc_exit","type":"donchian","params":{"period":3}}],"entry":{"op":"gt","args":[{"price":"close"},{"ref":"dc.upper"}]},"exit":{"op":"lt","args":[{"price":"close"},{"ref":"dc_exit.lower"}]},"stop":{"type":"percent","pct":0.01}}""",
    ];

    private sealed record Exploration(IReadOnlyList<(string Id, DslDefinition Definition, ExplorationMetrics Metrics)> Results, BacktestInput Evaluation);

    private sealed class ExplorationEvidence(Dictionary<string, (string Id, DslDefinition Definition, ExplorationMetrics Metrics)> results, DateTimeOffset availableAt) : IEvidenceResolver
    {
        public Task<EvidenceItem?> ResolveAsync(string evidenceRef, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(results.TryGetValue(evidenceRef, out var r)
                ? new EvidenceItem(evidenceRef, availableAt, r.Metrics.ShownPrimaryMetric, r.Metrics.ShownMetrics)
                : null);
    }
}
