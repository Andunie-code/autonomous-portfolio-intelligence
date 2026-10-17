using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai.Tests.Support;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>testing-strategy §6.3: routing, caps, degraded results, refusals and invalid output (fake provider, zero cost).</summary>
public class ModelRouterTests
{
    private const string QualifiedModelJson = $$"""{ "provider": "gemini", "model": "{{TestConfig.Model}}", "promptVersion": "hg v1", "qualificationId": 1, "acceptedModelVersions": [ "{{TestConfig.Model}}" ] }""";

    [Fact]
    public async Task Decision_affecting_task_without_a_qualified_model_makes_no_call()
    {
        var rig = new RouterRig();

        var result = await rig.Router.ExecuteAsync(TaskClass.HypothesisGeneration, TestConfig.Input(), TestConfig.Research, default);

        Assert.Equal(TaskOutcome.DeterministicFallback, result.Outcome);
        Assert.False(result.MayAffectDecisions);
        Assert.Empty(rig.Provider.Requests);
        Assert.Equal("no qualified model", Assert.Single(rig.Traces.Traces).Reason);
        Assert.Empty(rig.Ledger.Entries);
    }

    [Theory]
    [InlineData(QualificationVerdict.Failed, "hg v1", false)]
    [InlineData(QualificationVerdict.Inconclusive, "hg v1", false)]
    [InlineData(QualificationVerdict.Passed, "hg v2", false)] // prompt changed after qualification: qualification drops
    [InlineData(QualificationVerdict.Passed, "hg v1", true)]
    public async Task Only_a_valid_passed_qualification_routes_a_decision_affecting_task(QualificationVerdict verdict, string protocolPrompt, bool routed)
    {
        var rig = new RouterRig(TestConfig.Configuration(qualifiedModels: QualifiedModelJson));
        await RegisterQualificationAsync(rig, verdict, protocolPrompt, TestConfig.AnswerSchema.Hash);
        rig.Provider.Returns(RouterRig.Answer());

        var result = await rig.Router.ExecuteAsync(TaskClass.HypothesisGeneration, TestConfig.Input(), TestConfig.Research, default);

        Assert.Equal(routed ? 1 : 0, rig.Provider.Requests.Count);
        Assert.Equal(routed, result.MayAffectDecisions);
        Assert.Equal(routed ? TaskOutcome.Completed : TaskOutcome.DeterministicFallback, result.Outcome);
    }

    [Fact]
    public async Task A_schema_change_drops_the_qualification()
    {
        var rig = new RouterRig(TestConfig.Configuration(qualifiedModels: QualifiedModelJson));
        await RegisterQualificationAsync(rig, QualificationVerdict.Passed, "hg v1", new string('0', 64));

        var result = await rig.Router.ExecuteAsync(TaskClass.HypothesisGeneration, TestConfig.Input(), TestConfig.Research, default);

        Assert.Empty(rig.Provider.Requests);
        Assert.Equal(TaskOutcome.DeterministicFallback, result.Outcome);
    }

    [Fact]
    public async Task Monthly_cap_refuses_the_reservation_makes_no_call_and_trips_the_kill_switch()
    {
        var rig = new RouterRig();
        await rig.SpendAsync(9.999m);

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Empty(rig.Provider.Requests);
        Assert.Equal(TaskOutcome.Abstained, result.Outcome);
        Assert.Contains("MonthlyCapExceeded", result.Reason);
        Assert.True(await rig.KillSwitch.IsTrippedAsync(default));
        Assert.Equal(KillSwitchTrigger.MonthlyCap, rig.KillSwitchStore.Events.Single().Trigger);
    }

    [Fact]
    public async Task Job_cap_refuses_the_reservation_and_makes_no_call()
    {
        var rig = new RouterRig();
        await rig.SpendAsync(0.499m, TestConfig.Research.JobId); // research cycle cap = 5 % of 10 USD

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Empty(rig.Provider.Requests);
        Assert.Contains("JobCapExceeded", result.Reason);
        Assert.False(await rig.KillSwitch.IsTrippedAsync(default));
    }

    [Fact]
    public async Task Call_cap_refuses_an_estimate_above_the_per_call_cap()
    {
        var rig = new RouterRig(TestConfig.Configuration(callCap: 0.001m));

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Empty(rig.Provider.Requests);
        Assert.Contains("CallCapExceeded", result.Reason);
    }

    [Fact]
    public async Task Reservation_is_settled_with_the_actual_list_price_cost()
    {
        var rig = new RouterRig();
        rig.Provider.Returns(RouterRig.Answer(), input: 1000, output: 400);

        await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        var settlement = rig.Ledger.Entries.Single(e => e.Kind == LedgerEntryKind.Settlement);
        Assert.Equal((1000 * 0.30m + 400 * 2.50m) / 1_000_000m, settlement.Amount.Value);
        var reservation = rig.Ledger.Entries.Single(e => e.Kind == LedgerEntryKind.Reservation);
        Assert.Equal(rig.Provider.EstimateMaxCost(rig.Provider.Requests[0]), reservation.Amount);
    }

    [Fact]
    public async Task Different_reported_model_is_degraded_and_cannot_affect_decisions()
    {
        var rig = new RouterRig();
        rig.Provider.Returns(RouterRig.Answer(), model: TestConfig.OtherModel);

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Equal(TaskOutcome.Completed, result.Outcome);
        Assert.True(result.Degraded);
        Assert.False(result.MayAffectDecisions);
        var trace = Assert.Single(rig.Traces.Traces);
        Assert.True(trace.Degraded);
        Assert.Equal(TestConfig.Model, trace.RequestedModel);
        Assert.Equal(TestConfig.OtherModel, trace.ActualModel);
    }

    [Fact]
    public async Task Refusal_ends_in_no_decision()
    {
        var rig = new RouterRig();
        rig.Provider.Returns(null, finish: AiFinish.Refusal);

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Equal(TaskOutcome.Abstained, result.Outcome);
        Assert.Contains("no decision: refusal", result.Reason);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task Invalid_output_gets_one_repair_request_then_no_decision()
    {
        var rig = new RouterRig();
        rig.Provider.Returns("""{"answer": 5}""").Returns("not json");

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Equal(2, rig.Provider.Requests.Count);
        Assert.Contains("after one repair request", result.Reason);
        Assert.Null(result.Output);
        Assert.Equal(AiRole.User, rig.Provider.Requests[1].Messages[^1].Role);
    }

    [Fact]
    public async Task Repaired_output_completes()
    {
        var rig = new RouterRig();
        rig.Provider.Returns("""{"answer": "x", "extra": 1}""").Returns(RouterRig.Answer("fixed"));

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Equal(TaskOutcome.Completed, result.Outcome);
        Assert.Equal("fixed", result.Output!["answer"]!.GetValue<string>());
        Assert.Equal(2, Assert.Single(rig.Traces.Traces).Calls);
    }

    [Fact]
    public async Task Disallowed_tool_call_is_a_safety_violation_and_is_never_executed()
    {
        var rig = new RouterRig();
        var executor = new RecordingExecutor();
        var tools = new NeutralToolSet([new NeutralTool("allowed", "test", NeutralSchema.Parse("""{"type":"object","properties":{"q":{"type":"string"}},"additionalProperties":false}"""))]);
        rig.Provider.Calls(new AiToolCall("run_sql", new JsonObject { ["sql"] = "SELECT 1" }));

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(tools: tools, executor: executor), TestConfig.Research, default);

        Assert.Empty(executor.Calls);
        Assert.False(result.MayAffectDecisions);
        Assert.Contains("disallowed tool call 'run_sql'", Assert.Single(result.SafetyViolations));
        Assert.Contains("disallowed tool call 'run_sql'", Assert.Single(rig.Traces.Traces).SafetyViolations);
    }

    [Fact]
    public async Task Allowed_tool_calls_are_executed_and_appended_to_the_history()
    {
        var rig = new RouterRig();
        var executor = new RecordingExecutor();
        var tools = new NeutralToolSet([new NeutralTool("allowed", "test", NeutralSchema.Parse("""{"type":"object","properties":{"q":{"type":"string"}},"additionalProperties":false}"""))]);
        rig.Provider.Calls(new AiToolCall("allowed", new JsonObject { ["q"] = "x" })).Returns(RouterRig.Answer());

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(tools: tools, executor: executor), TestConfig.Research, default);

        Assert.Equal(TaskOutcome.Completed, result.Outcome);
        Assert.Single(executor.Calls);
        var history = rig.Provider.Requests[1].Messages;
        Assert.Equal([AiRole.User, AiRole.Model, AiRole.Tool], history.Select(m => m.Role));
    }

    [Fact]
    public async Task Another_providers_model_is_unavailable_not_a_fallback()
    {
        var configuration = TestConfig.Configuration();
        var policies = configuration.Tasks.ToDictionary();
        policies[TaskClass.Summary] = policies[TaskClass.Summary] with { Model = new ModelChoice("anthropic", "x", "v", ["x"]) };
        var rig = new RouterRig(configuration with { Tasks = policies });

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.Empty(rig.Provider.Requests);
        Assert.Equal(TaskOutcome.Abstained, result.Outcome);
        Assert.Contains("not configured", result.Reason);
    }

    internal static async Task RegisterQualificationAsync(RouterRig rig, QualificationVerdict verdict, string promptVersion, string schemaHash)
    {
        var protocol = new QualificationProtocol(new string('a', 64), "abc1234", TaskClass.HypothesisGeneration, "gemini", TestConfig.Model, promptVersion,
            schemaHash, new string('b', 64), new string('c', 64), 0.05, 0.05, 10, 3m);
        await rig.Qualifications.RegisterProtocolAsync(protocol, default);
        await rig.Qualifications.AddResultAsync(new TaskQualification(0, protocol.ProtocolHash, rig.Time.GetUtcNow(), verdict, 10, 5, 10, 1, 0.01, 0, UsdAmount.Zero, ""), default);
    }

    private sealed class RecordingExecutor : IToolExecutor
    {
        public List<AiToolCall> Calls { get; } = [];

        public Task<string> ExecuteAsync(AiToolCall call, CancellationToken ct)
        {
            Calls.Add(call);
            return Task.FromResult("""{"ok":true}""");
        }
    }
}
