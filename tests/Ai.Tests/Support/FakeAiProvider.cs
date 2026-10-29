using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace PortfolioIntelligence.Ai.Tests.Support;

/// <summary>
/// Scripted provider (zero cost, no network): each call takes the next step. A step returns a response, throws, or
/// blocks until the call is cancelled. Requests, batch cancellations and the price used for estimates are recorded.
/// </summary>
public sealed class FakeAiProvider(PriceTable prices) : IAiProvider
{
    private readonly Queue<Func<AiRequest, CancellationToken, Task<AiResponse>>> _steps = new();

    public string Name => "gemini";

    public List<AiRequest> Requests { get; } = [];

    public List<string> CancelledBatches { get; } = [];

    /// <summary>Estimated input tokens per call (fixed, so tests control the reservation).</summary>
    public int EstimatedInputTokens { get; set; } = 1000;

    public TaskCompletionSource InFlight { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeAiProvider Returns(string? text, string model = TestConfig.Model, AiFinish finish = AiFinish.Stop, int input = 100, int output = 50, IReadOnlyList<AiToolCall>? toolCalls = null)
    {
        _steps.Enqueue((_, _) => Task.FromResult(new AiResponse(model, text, toolCalls ?? [], finish, new AiUsage(input, output))));
        return this;
    }

    public FakeAiProvider Calls(params AiToolCall[] calls) => Returns(null, finish: AiFinish.ToolCall, toolCalls: calls);

    public FakeAiProvider Throws(int? status, TimeSpan? retryAfter = null, bool network = false)
    {
        _steps.Enqueue((_, _) => throw new AiProviderException("scripted failure", status, retryAfter, network));
        return this;
    }

    /// <summary>The call stays in flight until its token is cancelled (kill switch, timeout).</summary>
    public FakeAiProvider Blocks()
    {
        _steps.Enqueue(async (_, ct) =>
        {
            InFlight.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    public Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("No scripted step left: an unexpected provider call.");
        }

        return _steps.Dequeue()(request, cancellationToken);
    }

    public Task<string> SubmitBatchAsync(IReadOnlyList<AiRequest> requests, CancellationToken cancellationToken) => Task.FromResult("batches/test-1");

    public Task<AiBatchStatus> GetBatchAsync(string batchId, CancellationToken cancellationToken) =>
        Task.FromResult(new AiBatchStatus(batchId, AiBatchState.Running, []));

    public Task CancelBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        CancelledBatches.Add(batchId);
        return Task.CompletedTask;
    }

    public UsdAmount EstimateMaxCost(AiRequest request) => prices.Cost(Name, request.Model, new AiUsage(EstimatedInputTokens, request.MaxOutputTokens));
}

/// <summary>Configuration and wiring for router tests. Model names come from this configuration, never from the code under test.</summary>
public static class TestConfig
{
    public const string Model = "test-model-a";
    public const string OtherModel = "test-model-b";

    public static AiConfiguration Configuration(decimal monthlyCap = 10m, decimal callCap = 0.10m, string? qualifiedModels = null, string? findingQualifiedModels = null, string? allocationQualifiedModels = null) => AiConfiguration.Parse($$"""
        {
          "version": "ai-config test",
          "spend": { "monthlyCapUsd": {{monthlyCap}}, "callCapUsd": {{callCap}}, "jobShares": { "Qualification": 0.30, "ResearchCycle": 0.05, "Spike": 0.05, "Allocation": 0.10 },
                     "velocityWindowMinutes": 60, "velocityShare": 0.20, "costDeviationFactor": 2, "costDeviationConsecutive": 3 },
          "resilience": { "concurrency": 1, "maxRetries": 2, "backoffBaseSeconds": 1, "maxRetryAfterSeconds": 60, "circuitBreakerFailures": 5,
                          "killSwitchPollSeconds": 2, "maxToolRounds": 3 },
          "prices": { "version": "test-prices", "models": [
            { "provider": "gemini", "model": "{{Model}}", "inputPerMillionUsd": 0.30, "outputPerMillionUsd": 2.50 },
            { "provider": "gemini", "model": "{{OtherModel}}", "inputPerMillionUsd": 0.30, "outputPerMillionUsd": 2.50 } ] },
          "tasks": {
            "HypothesisGeneration": { "kind": "DecisionAffecting", "onUnavailable": "DeterministicFallback", "maxOutputTokens": 2000, "timeoutSeconds": 120,
                                      "qualifiedModels": [ {{qualifiedModels ?? ""}} ] },
            "FindingExtraction": { "kind": "DecisionAffecting", "onUnavailable": "Skip", "maxOutputTokens": 2000, "timeoutSeconds": 120,
                                   "qualifiedModels": [ {{findingQualifiedModels ?? ""}} ] },
            "AllocationProposal": { "kind": "DecisionAffecting", "onUnavailable": "DeterministicFallback", "maxOutputTokens": 2000, "timeoutSeconds": 30,
                                    "qualifiedModels": [ {{allocationQualifiedModels ?? ""}} ] },
            "Summary": { "kind": "Advisory", "onUnavailable": "Abstain", "maxOutputTokens": 2000, "timeoutSeconds": 120,
                         "model": { "provider": "gemini", "model": "{{Model}}", "promptVersion": "summary test", "acceptedModelVersions": [ "{{Model}}" ] } }
          }
        }
        """);

    public static readonly NeutralSchema AnswerSchema = NeutralSchema.Parse(
        """{"type":"object","properties":{"answer":{"type":"string"},"claims":{"type":"array","items":{"type":"object","properties":{"text":{"type":"string"},"critical":{"type":"boolean"},"evidenceRef":{"type":"string"},"value":{"type":"number"}},"required":["text"],"additionalProperties":false}}},"required":["answer"],"additionalProperties":false}""");

    public static TaskInput Input(IReadOnlyList<IOutputValidator>? validators = null, NeutralToolSet? tools = null, IToolExecutor? executor = null) =>
        new("system", "prompt", AnswerSchema, ["exp:1"], validators, tools, executor);

    public static readonly JobContext Research = new("research-1", JobKind.ResearchCycle);
}

/// <summary>Router with in-memory stores and a fake clock.</summary>
public sealed class RouterRig
{
    public RouterRig(AiConfiguration? configuration = null)
    {
        Configuration = configuration ?? TestConfig.Configuration();
        Provider = new FakeAiProvider(Configuration.Prices);
        KillSwitch = new AiKillSwitch(KillSwitchStore, Audit, Time);
        Guard = new AiCallGuard(Provider, Configuration, Ledger, KillSwitch, Time);
        Router = new ModelRouter(Configuration, Guard, Traces, new QualificationRegistry(Qualifications), Time);
    }

    public AiConfiguration Configuration { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    public FakeAiProvider Provider { get; }

    public InMemoryCostLedger Ledger { get; } = new();

    public InMemoryKillSwitchStore KillSwitchStore { get; } = new();

    public InMemoryAiAudit Audit { get; } = new();

    public InMemoryDecisionTraceStore Traces { get; } = new();

    public InMemoryQualificationStore Qualifications { get; } = new();

    public AiKillSwitch KillSwitch { get; }

    public AiCallGuard Guard { get; }

    public ModelRouter Router { get; }

    public static string Answer(string answer = "ok", JsonArray? claims = null) =>
        new JsonObject { ["answer"] = answer, ["claims"] = claims ?? [] }.ToJsonString();

    /// <summary>Pre-fills the ledger with one settled call of <paramref name="amount"/> USD.</summary>
    public async Task SpendAsync(decimal amount, string jobId = "earlier-job", JobKind kind = JobKind.ResearchCycle, TimeSpan? ago = null)
    {
        var id = Guid.NewGuid();
        var at = Time.GetUtcNow() - (ago ?? TimeSpan.FromDays(1));
        await Ledger.TryReserveAsync(new ReservationRequest(id, new JobContext(jobId, kind), null, "gemini", TestConfig.Model, new UsdAmount(amount), "test"),
            new UsdAmount(1000), new UsdAmount(1000), new UsdAmount(1000), at, default);
        await Ledger.SettleAsync(id, new UsdAmount(amount), new AiUsage(0, 0), at, default);
    }
}
