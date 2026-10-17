using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Ai;

/// <summary>Executes the tool calls a model makes; returns the result as JSON. Only tools of the task's set reach it.</summary>
public interface IToolExecutor
{
    Task<string> ExecuteAsync(AiToolCall call, CancellationToken ct);
}

public sealed record TaskInput(
    string SystemInstruction,
    string Prompt,
    NeutralSchema OutputSchema,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<IOutputValidator>? Validators = null,
    NeutralToolSet? Tools = null,
    IToolExecutor? ToolExecutor = null)
{
    public string InputHash => DslCanonical.Sha256(string.Join("\n",
        SystemInstruction, Prompt, OutputSchema.Hash, string.Join(",", Tools?.Tools.Select(t => t.Name + ":" + t.Parameters.Hash) ?? [])));
}

/// <summary>
/// Result of one task. Only a completed, non-degraded production result without safety violations may affect a
/// decision; anything else means the caller applies <see cref="OnUnavailable"/> (D20).
/// </summary>
public sealed record TaskResult(
    Guid TaskId,
    TaskOutcome Outcome,
    JsonNode? Output,
    bool Degraded,
    RouterMode Mode,
    UnavailableBehaviour OnUnavailable,
    IReadOnlyList<string> SafetyViolations,
    string? Reason)
{
    public bool MayAffectDecisions => Outcome == TaskOutcome.Completed && !Degraded && Mode == RouterMode.Production && SafetyViolations.Count == 0;
}

/// <summary>
/// Provider-neutral router (architecture §3.1, ADR-0007, ADR-0017 §3). In production a decision-affecting task goes
/// only to a configured model whose qualification is still valid; otherwise no call is made and the task falls to
/// <c>onUnavailable</c>. There is no cross-provider fallback. Every call goes through <see cref="AiCallGuard"/>;
/// refusals, off-schema output (after one repair request), failed validators and disallowed tool calls end in
/// "no decision". Every task writes a <see cref="DecisionTrace"/>.
/// </summary>
public sealed class ModelRouter(AiConfiguration configuration, AiCallGuard guard, IDecisionTraceStore traces, QualificationRegistry qualifications, TimeProvider time)
{
    public AiKillSwitch KillSwitch => guard.KillSwitch;

    public async Task<TaskResult> ExecuteAsync(TaskClass task, TaskInput input, JobContext job, CancellationToken ct)
    {
        var policy = configuration.Policy(task);
        ModelChoice? choice = null;
        long? qualificationId = null;
        if (policy.Kind == TaskKind.DecisionAffecting)
        {
            foreach (var qualified in policy.QualifiedModels ?? [])
            {
                if (await qualifications.IsValidAsync(task, qualified, input.OutputSchema.Hash, ct))
                {
                    choice = qualified.Choice;
                    qualificationId = qualified.QualificationId;
                    break;
                }
            }
        }
        else
        {
            choice = policy.Model;
        }

        if (choice is null)
        {
            return await NoCallAsync(task, input, job, RouterMode.Production, policy, "no qualified model", ct);
        }

        return await RunAsync(task, input, job, RouterMode.Production, policy, choice, qualificationId, null, ct);
    }

    /// <summary>Qualification mode: only through the harness, with a registered protocol; the output never affects a decision.</summary>
    public async Task<TaskResult> ExecuteForQualificationAsync(QualificationSession session, TaskInput input, CancellationToken ct)
    {
        var protocol = session.Protocol;
        if (session.Job.Kind != JobKind.Qualification)
        {
            throw new InvalidOperationException("Qualification mode needs a qualification job.");
        }

        if (input.OutputSchema.Hash != protocol.SchemaHash)
        {
            throw new InvalidOperationException("Output schema differs from the registered protocol.");
        }

        var policy = configuration.Policy(protocol.Task);
        var choice = new ModelChoice(protocol.Provider, protocol.Model, protocol.PromptVersion, [protocol.Model]);
        return await RunAsync(protocol.Task, input, session.Job, RouterMode.Qualification, policy, choice, null, protocol.ProtocolHash, ct);
    }

    private async Task<TaskResult> RunAsync(TaskClass task, TaskInput input, JobContext job, RouterMode mode, TaskPolicy policy, ModelChoice choice,
        long? qualificationId, string? protocolHash, CancellationToken ct)
    {
        var trace = new TraceBuilder(task, input, job, mode, choice, qualificationId, protocolHash, time.GetUtcNow());
        if (!string.Equals(choice.Provider, guard.Provider.Name, StringComparison.Ordinal))
        {
            // No cross-provider fallback (T14): a model of another provider is simply unavailable.
            return await FinishAsync(trace, policy, Fallback(policy), null, $"provider '{choice.Provider}' is not configured", ct);
        }

        var messages = new List<AiMessage> { new(AiRole.User, input.Prompt) };
        var repaired = false;
        for (var round = 0; round <= configuration.Resilience.MaxToolRounds; round++)
        {
            var request = new AiRequest(choice.Model, input.SystemInstruction, messages.ToList(), input.OutputSchema, input.Tools, policy.MaxOutputTokens, policy.Timeout);
            var call = await guard.CallAsync(request, task, job, ct);
            trace.Add(call);
            if (!call.Succeeded)
            {
                var outcome = call.Status == CallStatus.Stopped ? TaskOutcome.Stopped : Fallback(policy);
                return await FinishAsync(trace, policy, outcome, null, $"{call.Status}: {call.Reason}", ct);
            }

            var response = call.Response!;
            trace.ActualModel = response.ActualModel;
            trace.Degraded |= !choice.Accepts(response.ActualModel);
            if (response.Finish == AiFinish.Refusal)
            {
                return await NoDecisionAsync(trace, policy, "refusal", ct);
            }

            if (response.ToolCalls.Count > 0)
            {
                if (input.Tools is null || input.ToolExecutor is null)
                {
                    trace.Violations.Add("tool call in a task without tools");
                    return await NoDecisionAsync(trace, policy, "disallowed tool call", ct);
                }

                var disallowed = response.ToolCalls.Where(c => !input.Tools.Contains(c.Name)).ToList();
                if (disallowed.Count > 0)
                {
                    trace.Violations.AddRange(disallowed.Select(c => $"disallowed tool call '{c.Name}'"));
                    return await NoDecisionAsync(trace, policy, "disallowed tool call", ct);
                }

                messages.Add(new AiMessage(AiRole.Model, response.Text, response.ToolCalls));
                foreach (var toolCall in response.ToolCalls)
                {
                    var tool = input.Tools.Tools.Single(t => t.Name == toolCall.Name);
                    var argumentErrors = tool.Parameters.Validate(toolCall.Arguments);
                    var result = argumentErrors.Count > 0
                        ? new JsonObject { ["error"] = "arguments do not match the tool schema: " + string.Join("; ", argumentErrors) }.ToJsonString()
                        : await input.ToolExecutor.ExecuteAsync(toolCall, ct);
                    messages.Add(new AiMessage(AiRole.Tool, null, ToolName: toolCall.Name, ToolResultJson: result));
                }

                continue;
            }

            var (value, errors) = input.OutputSchema.ParseAndValidate(response.Text);
            if (errors.Count > 0)
            {
                trace.Validators.Add("schema: " + string.Join("; ", errors));
                if (repaired)
                {
                    return await NoDecisionAsync(trace, policy, "output does not match the schema after one repair request", ct);
                }

                repaired = true;
                messages.Add(new AiMessage(AiRole.Model, response.Text));
                messages.Add(new AiMessage(AiRole.User,
                    "The previous output did not match the required JSON schema (" + string.Join("; ", errors) + "). Reply again with only JSON that matches the schema."));
                continue;
            }

            foreach (var validator in input.Validators ?? [])
            {
                var failures = await validator.ValidateAsync(value!, trace.At, ct);
                trace.Validators.AddRange(failures.Select(f => $"{validator.Name}: {f}"));
                if (failures.Count > 0)
                {
                    return await NoDecisionAsync(trace, policy, $"{validator.Name} validator rejected the output", ct);
                }
            }

            trace.Validators.Add("schema: ok");
            return await FinishAsync(trace, policy, TaskOutcome.Completed, value, trace.Degraded ? "degraded: reported model differs from the requested model" : null, ct);
        }

        return await NoDecisionAsync(trace, policy, "tool round limit reached", ct);
    }

    private async Task<TaskResult> NoCallAsync(TaskClass task, TaskInput input, JobContext job, RouterMode mode, TaskPolicy policy, string reason, CancellationToken ct)
    {
        var trace = new TraceBuilder(task, input, job, mode, null, null, null, time.GetUtcNow());
        return await FinishAsync(trace, policy, Fallback(policy), null, reason, ct);
    }

    private Task<TaskResult> NoDecisionAsync(TraceBuilder trace, TaskPolicy policy, string reason, CancellationToken ct) =>
        FinishAsync(trace, policy, Fallback(policy), null, "no decision: " + reason, ct);

    private async Task<TaskResult> FinishAsync(TraceBuilder trace, TaskPolicy policy, TaskOutcome outcome, JsonNode? output, string? reason, CancellationToken ct)
    {
        await traces.AddAsync(trace.Build(outcome, reason), CancellationToken.None);
        return new TaskResult(trace.TaskId, outcome, output, trace.Degraded, trace.Mode, policy.OnUnavailable, trace.Violations.ToList(), reason);
    }

    private static TaskOutcome Fallback(TaskPolicy policy) => policy.OnUnavailable switch
    {
        UnavailableBehaviour.DeterministicFallback => TaskOutcome.DeterministicFallback,
        UnavailableBehaviour.ManualReview => TaskOutcome.ManualReview,
        _ => TaskOutcome.Abstained,
    };

    private sealed class TraceBuilder(TaskClass task, TaskInput input, JobContext job, RouterMode mode, ModelChoice? choice, long? qualificationId, string? protocolHash, DateTimeOffset at)
    {
        private int _calls;
        private int _inputTokens;
        private int _outputTokens;
        private TimeSpan _latency;
        private UsdAmount _estimated = UsdAmount.Zero;
        private UsdAmount _actual = UsdAmount.Zero;

        public Guid TaskId { get; } = Guid.NewGuid();

        public DateTimeOffset At => at;

        public RouterMode Mode => mode;

        public string? ActualModel { get; set; }

        public bool Degraded { get; set; }

        public List<string> Validators { get; } = [];

        public List<string> Violations { get; } = [];

        public void Add(GuardedCall call)
        {
            _calls += call.Attempts;
            _inputTokens += call.Response?.Usage.InputTokens ?? 0;
            _outputTokens += call.Response?.Usage.OutputTokens ?? 0;
            _latency += call.Latency;
            _estimated += call.Estimated;
            _actual += call.Actual;
        }

        public DecisionTrace Build(TaskOutcome outcome, string? reason) => new(
            TaskId, at, job.JobId, task, mode, input.InputHash, input.EvidenceIds, choice?.Provider, choice?.Model, ActualModel,
            choice?.PromptVersion, qualificationId, protocolHash, _calls, _inputTokens, _outputTokens, (long)_latency.TotalMilliseconds,
            _estimated, _actual, Validators.ToList(), Violations.ToList(), outcome, Degraded, reason);
    }
}
