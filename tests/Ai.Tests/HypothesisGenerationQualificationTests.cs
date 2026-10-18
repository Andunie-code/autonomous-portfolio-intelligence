using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai.Tests.Support;
using PortfolioIntelligence.Research.Qualification;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>
/// HypothesisGeneration on synthetic items (docs/m4-hg-qualification-design.md) with the fake provider: prompt and schema,
/// the LLM and baseline arms, evidence and safety invariants, and the harness end to end. Zero cost, no network.
/// </summary>
public class HypothesisGenerationQualificationTests
{
    private const string TrendDefinition = """
        {"dsl":"1","name":"llm-trend","interval":"4h","indicators":[{"id":"fast","type":"ema","params":{"period":20}},{"id":"slow","type":"ema","params":{"period":60}},{"id":"atr","type":"atr","params":{"period":14}}],
         "entry":{"op":"cross_above","args":[{"ref":"fast.value"},{"ref":"slow.value"}]},"exit":{"op":"cross_below","args":[{"ref":"fast.value"},{"ref":"slow.value"}]},
         "stop":{"type":"atr","ref":"atr.value","multiple":3,"trailing":false}}
        """;

    private static readonly IReadOnlyList<QualificationItem> Pilot = HgItem.Range(30_000, 2).Select(i => i.ToQualificationItem()).ToList();

    private static readonly IReadOnlyList<QualificationItem> Set = HgItem.Range(40_000, 2).Select(i => i.ToQualificationItem()).ToList();

    [Fact]
    public void Items_are_self_describing_and_round_trip()
    {
        var item = HgItem.Range(30_000, 1).Single();

        var parsed = HgItem.Parse(item.ToQualificationItem());

        Assert.Equal(item, parsed);
        Assert.Contains("kappa=0.056", item.Content);
        Assert.NotEqual(QualificationItem.SetHash(Pilot), QualificationItem.SetHash(Set));
    }

    [Fact]
    public void Prompt_lists_the_whitelist_and_every_exploration_result_and_holds_no_real_market_data()
    {
        var prompt = HgPrompt.Build([("exp:1", Kernel.Dsl.DslParser.Parse(Kernel.Dsl.DslBaselines.EmaTrendJson), new ExplorationMetrics(0.123456789, 40, 3, 4, 12, 20, 0.2))]);

        Assert.Contains("exp:1: M=0.123457", prompt);
        foreach (var type in Kernel.Dsl.DslCatalog.Indicators.Keys)
        {
            Assert.Contains($"- {type}:", prompt);
        }

        Assert.DoesNotContain("ETHUSDT", prompt);
        Assert.DoesNotContain("2026", prompt);
        Assert.Equal("object", HgPrompt.Schema.Root["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Llm_arm_gates_a_valid_proposal_on_the_evaluation_universe()
    {
        var (rig, session) = Session();
        rig.Provider.Returns(Proposal(TrendDefinition));
        var task = new HypothesisGenerationTask(rig.Time, bootstrapReplicates: 500);

        var outcome = await task.RunLlmAsync(session, Set[0], default);

        var record = Assert.Single(task.Records);
        Assert.True(record.ValidDefinition);
        Assert.NotNull(record.DefinitionHash);
        Assert.Equal(record.GateFailures.Count == 0, outcome.Success);
        Assert.Empty(outcome.SafetyViolations);
        var request = Assert.Single(rig.Provider.Requests);
        Assert.Null(request.Tools); // no tools in this task
        Assert.Contains("exp:6", request.Messages[0].Text);
    }

    [Fact]
    public async Task Fabricated_evidence_is_rejected_and_counts_as_a_failure_not_a_violation()
    {
        var (rig, session) = Session();
        rig.Provider.Returns(Proposal(TrendDefinition, new JsonObject { ["text"] = "exp:99 shows a strong trend", ["critical"] = true, ["evidenceRef"] = "exp:99", ["value"] = 0.5 }));
        var task = new HypothesisGenerationTask(rig.Time, bootstrapReplicates: 500);

        var outcome = await task.RunLlmAsync(session, Set[0], default);

        Assert.False(outcome.Success);
        Assert.Empty(outcome.SafetyViolations);
        Assert.Contains("evidence validator rejected", task.Records.Single().Reason);
    }

    [Fact]
    public async Task Invalid_dsl_is_a_failure_and_is_counted()
    {
        var (rig, session) = Session();
        rig.Provider.Returns(Proposal("""{"dsl":"1","name":"x","interval":"1h"}"""));
        var task = new HypothesisGenerationTask(rig.Time, bootstrapReplicates: 500);

        var outcome = await task.RunLlmAsync(session, Set[0], default);

        Assert.False(outcome.Success);
        Assert.StartsWith("invalid DSL", task.Records.Single().Reason);
    }

    [Fact]
    public async Task A_tool_call_in_this_task_is_a_safety_violation()
    {
        var (rig, session) = Session();
        rig.Provider.Calls(new AiToolCall("SubmitExperiment", new JsonObject()));
        var task = new HypothesisGenerationTask(rig.Time, bootstrapReplicates: 500);

        var outcome = await task.RunLlmAsync(session, Set[0], default);

        Assert.False(outcome.Success);
        Assert.Contains("tool call in a task without tools", Assert.Single(outcome.SafetyViolations));
    }

    [Fact]
    public async Task Baseline_arm_is_deterministic_and_makes_no_call()
    {
        var first = new HypothesisGenerationTask(TimeProvider.System, bootstrapReplicates: 500);
        var second = new HypothesisGenerationTask(TimeProvider.System, bootstrapReplicates: 500);

        var a = await first.RunBaselineAsync(Set[1], default);
        var b = await second.RunBaselineAsync(Set[1], default);

        Assert.Equal(a.Success, b.Success);
        Assert.Equal(first.Records.Single().DefinitionHash, second.Records.Single().DefinitionHash);
        Assert.Equal(first.Records.Single().GateFailures, second.Records.Single().GateFailures);
    }

    [Fact]
    public async Task Harness_runs_the_task_end_to_end_and_records_the_result()
    {
        var rig = new RouterRig();
        const string document = "# HG protocol (test)\n";
        var protocol = new QualificationProtocol(QualificationProtocol.HashDocument(document), "abc1234", TaskClass.HypothesisGeneration, "gemini", TestConfig.Model,
            HgPrompt.PromptVersion, HgPrompt.Schema.Hash, QualificationItem.SetHash(Pilot), QualificationItem.SetHash(Set), 0.05, 0.15, 2, 3m);
        await rig.Qualifications.RegisterProtocolAsync(protocol, default);
        rig.Provider.Returns(Proposal(TrendDefinition)).Returns(Proposal(TrendDefinition));
        var task = new HypothesisGenerationTask(rig.Time, bootstrapReplicates: 500);

        var result = await new QualificationHarness(rig.Qualifications, rig.Router, rig.Ledger, rig.Time).RunAsync(document, Pilot, Set, task, default);

        Assert.Equal(2, result.TrialsLlm);
        Assert.Equal(2, result.TrialsBaseline);
        Assert.Equal(2, rig.Provider.Requests.Count);
        Assert.Single(rig.Qualifications.Results);
        Assert.Equal(4, task.Records.Count);
        Assert.All(rig.Traces.Traces, t => Assert.Equal(RouterMode.Qualification, t.Mode));
    }

    private static (RouterRig Rig, QualificationSession Session) Session()
    {
        var rig = new RouterRig();
        var protocol = new QualificationProtocol(new string('e', 64), "pilot", TaskClass.HypothesisGeneration, "gemini", TestConfig.Model, HgPrompt.PromptVersion,
            HgPrompt.Schema.Hash, QualificationItem.SetHash(Pilot), QualificationItem.SetHash(Set), 0.05, 0.15, 2, 0.5m);
        return (rig, new QualificationSession(protocol, new JobContext("hg-test", JobKind.Qualification), rig.Router));
    }

    private static string Proposal(string definition, params JsonObject[] claims) => new JsonObject
    {
        ["title"] = "trend",
        ["rationale"] = "exploration trend strategies did well",
        ["definition"] = definition,
        ["claims"] = new JsonArray(claims.Length > 0 ? claims : [new JsonObject { ["text"] = "trend persistence", ["critical"] = false }]),
    }.ToJsonString();
}
