using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai.Tests.Support;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Researcher;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>
/// Researcher contract (roadmap M4): exactly the six tools of architecture §4, no holdout, evaluator, SQL, shell or web
/// access; disallowed calls are refused and recorded; the pre-registered hypothesis list cannot drift.
/// </summary>
public class ResearcherContractTests
{
    private const string ListHash = "89ab3965a8cbaf068788a95bc354f0340c29ae1b8d3834ef6c840bad1d4c2ec7";

    [Fact]
    public void Toolbox_exposes_exactly_the_six_researcher_tools()
    {
        var toolbox = new ResearcherToolbox(new FakeBackend(), new InMemorySafetyLog());

        Assert.Equal(
            ["GetDatasetCatalog", "GetExperimentResult", "ListIndicators", "SearchExperiments", "SubmitExperiment", "ValidateStrategyDefinition"],
            toolbox.Tools.Tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ResearcherToolNames.All.Order(StringComparer.Ordinal), toolbox.Tools.Tools.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("RunSql")]
    [InlineData("Shell")]
    [InlineData("FetchUrl")]
    [InlineData("SubmitToEvaluator")]
    [InlineData("ReadHoldout")]
    [InlineData("PlaceOrder")]
    [InlineData("SetRiskConfig")]
    public async Task Any_other_tool_is_refused_and_recorded_as_a_safety_violation(string name)
    {
        var backend = new FakeBackend();
        var safety = new InMemorySafetyLog();
        var toolbox = new ResearcherToolbox(backend, safety);

        var result = await toolbox.InvokeAsync(name, [], default);

        Assert.NotNull(result["error"]);
        Assert.Equal($"disallowed tool call '{name}'", Assert.Single(safety.Violations));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Model_calling_a_forbidden_tool_through_the_router_never_reaches_the_backend()
    {
        var rig = new RouterRig();
        var backend = new FakeBackend();
        var toolbox = new ResearcherToolbox(backend, new InMemorySafetyLog());
        rig.Provider.Calls(new AiToolCall("SubmitExperiment", new JsonObject { ["hypothesis"] = "h", ["definition"] = DslBaselines.EmaTrendJson, ["origin"] = "llm" }),
            new AiToolCall("RunSql", new JsonObject { ["sql"] = "SELECT * FROM holdout" }));

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(tools: toolbox.Tools, executor: toolbox), TestConfig.Research, default);

        Assert.False(result.MayAffectDecisions);
        Assert.Contains("disallowed tool call 'RunSql'", result.SafetyViolations);
        Assert.Equal(0, backend.Calls); // the whole turn is refused; not even the allowed call in it runs
    }

    [Fact]
    public async Task Invalid_arguments_and_invalid_definitions_never_reach_the_backend()
    {
        var backend = new FakeBackend();
        var toolbox = new ResearcherToolbox(backend, new InMemorySafetyLog());

        var badArguments = await toolbox.InvokeAsync(ResearcherToolNames.SubmitExperiment, new JsonObject { ["definition"] = "{}" }, default);
        var badDefinition = await toolbox.InvokeAsync(ResearcherToolNames.SubmitExperiment,
            new JsonObject { ["hypothesis"] = "h", ["definition"] = """{"dsl":"1","name":"x"}""", ["origin"] = "grid" }, default);
        var badOrigin = await toolbox.InvokeAsync(ResearcherToolNames.SubmitExperiment,
            new JsonObject { ["hypothesis"] = "h", ["definition"] = DslBaselines.EmaTrendJson, ["origin"] = "holdout" }, default);

        Assert.NotNull(badArguments["error"]);
        Assert.False(badDefinition["valid"]!.GetValue<bool>());
        Assert.NotNull(badOrigin["error"]);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Validate_and_list_indicators_work_without_the_backend()
    {
        var backend = new FakeBackend();
        var toolbox = new ResearcherToolbox(backend, new InMemorySafetyLog());

        var valid = await toolbox.InvokeAsync(ResearcherToolNames.ValidateStrategyDefinition, new JsonObject { ["definition"] = DslBaselines.EmaTrendJson }, default);
        var indicators = await toolbox.InvokeAsync(ResearcherToolNames.ListIndicators, new JsonObject { ["filter"] = "ma" }, default);

        Assert.True(valid["valid"]!.GetValue<bool>());
        Assert.Equal(DslCanonical.DefinitionHash(DslParser.Parse(DslBaselines.EmaTrendJson)), valid["definitionHash"]!.GetValue<string>());
        Assert.Equal(["macd"], indicators.AsArray().Select(i => i!["type"]!.GetValue<string>()));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Dataset_catalog_tool_passes_through_only_the_backend_catalog()
    {
        var toolbox = new ResearcherToolbox(new FakeBackend(), new InMemorySafetyLog());

        var catalog = await toolbox.InvokeAsync(ResearcherToolNames.GetDatasetCatalog, new JsonObject { ["interval"] = "4h" }, default);

        var row = Assert.Single(catalog.AsArray());
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", row!["end"]!.GetValue<string>());
    }

    [Fact]
    public void Researcher_sources_use_no_database_process_http_or_holdout_code()
    {
        string[] forbidden = ["Npgsql", "System.Diagnostics.Process", "Process.Start", "System.Net.Http", "HttpClient", "HoldoutEvaluator", "System.Data"];
        var directory = Path.Combine(RepositoryRoot.Path, "src", "Modules", "Researcher");
        var offenders = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("<!--", StringComparison.Ordinal)
                        && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(l => forbidden.Any(token => l.Text.Contains(token, StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Pre_registered_hypothesis_list_loads_and_matches_its_hash()
    {
        var list = HypothesisList.Load(File.ReadAllText(HypothesisFile), ListHash);

        Assert.Equal(12, list.Hypotheses.Count);
        Assert.Equal(96, list.TrialCount);
        Assert.Equal(12, list.Hypotheses.Select(h => h.StructureHash).Distinct().Count());
        Assert.True(list.TrialCount + 2 <= 100); // m4-protocol §1: the global counter stays ≤ 100 (2 baselines before)
    }

    [Fact]
    public void Tampered_hypothesis_list_is_refused()
    {
        var json = File.ReadAllText(HypothesisFile);
        var tampered = json.Replace("\"period\": 55", "\"period\": 56", StringComparison.Ordinal);
        Assert.NotEqual(json, tampered);

        Assert.Throws<InvalidDataException>(() => HypothesisList.Load(tampered, ListHash));
        Assert.Throws<InvalidDataException>(() => HypothesisList.Load(json, new string('0', 64)));
    }

    [Fact]
    public async Task Llm_hypothesis_source_without_a_qualified_model_yields_nothing_and_makes_no_call()
    {
        var rig = new RouterRig();
        var source = new LlmHypothesisSource(rig.Router, TestConfig.Research, () => TestConfig.Input());

        var hypotheses = await source.HypothesesAsync(default);

        Assert.Empty(hypotheses);
        Assert.Empty(rig.Provider.Requests);
        Assert.Equal(TaskOutcome.DeterministicFallback, source.LastResult!.Outcome);
    }

    [Fact]
    public async Task Research_cycle_takes_every_point_through_the_tools_in_order()
    {
        var list = HypothesisList.Load(File.ReadAllText(HypothesisFile), ListHash);
        var small = list with { Hypotheses = list.Hypotheses.Take(2).ToList() };
        var backend = new FakeBackend();
        var safety = new InMemorySafetyLog();
        var cycle = new ResearchCycle(new ResearcherToolbox(backend, safety), backend, safety);
        var provenance = new HypothesisProvenance("dev-assisted", "grid", "test", new DateOnly(2026, 6, 30), true, "user");

        var report = await cycle.RunAsync(new ApprovedHypothesisSource(small, provenance), _ => { }, default);

        Assert.Equal(["h01", "h02"], report.Verdicts.Select(v => v.Key));
        Assert.Equal(small.TrialCount, report.Points.Count);
        Assert.Equal(small.Hypotheses.SelectMany(h => h.Points.Select(p => p.DefinitionHash)), backend.Submitted.Select(s => DslCanonical.DefinitionHash(DslParser.Parse(s.DefinitionJson))));
        Assert.All(backend.Submitted, s => Assert.Equal("grid", s.Origin));
        Assert.Empty(report.SafetyViolations);
    }

    private static string HypothesisFile => Path.Combine(RepositoryRoot.Path, "docs", "m4-hypotheses.v1.json");

    private sealed class FakeBackend : IResearchToolBackend, IHypothesisPipeline
    {
        public int Calls { get; private set; }

        public List<ExperimentSubmission> Submitted { get; } = [];

        public Task<JsonNode> SearchExperimentsAsync(string query, int limit, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<JsonNode>(new JsonArray());
        }

        public Task<JsonNode> GetDatasetCatalogAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<JsonNode>(new JsonArray(
                new JsonObject { ["symbol"] = "BTCUSDT", ["interval"] = "4h", ["end"] = "2026-01-01T00:00:00.0000000+00:00" },
                new JsonObject { ["symbol"] = "BTCUSDT", ["interval"] = "1h", ["end"] = "2026-01-01T00:00:00.0000000+00:00" }));
        }

        public Task<JsonNode> SubmitExperimentAsync(ExperimentSubmission submission, CancellationToken ct)
        {
            Calls++;
            Submitted.Add(submission);
            return Task.FromResult<JsonNode>(new JsonObject { ["accepted"] = true, ["experimentId"] = Submitted.Count, ["trialId"] = Submitted.Count, ["newTrial"] = true });
        }

        public Task<JsonNode> GetExperimentResultAsync(long experimentId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<JsonNode>(new JsonObject { ["experimentId"] = experimentId, ["primaryMetric"] = 0.01 });
        }

        public Task RegisterHypothesisAsync(Hypothesis hypothesis, HypothesisProvenance provenance, CancellationToken ct) => Task.CompletedTask;

        public Task<HypothesisVerdict> EvaluateHypothesisAsync(Hypothesis hypothesis, CancellationToken ct) =>
            Task.FromResult(new HypothesisVerdict(hypothesis.Key, hypothesis.Title, HypothesisCategory.Rejected, hypothesis.Points[0].Label, ["G1 cost-adjusted return"], new JsonObject()));
    }
}
