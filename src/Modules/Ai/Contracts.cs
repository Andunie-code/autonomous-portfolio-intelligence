using System.Globalization;
using System.Text.Json.Nodes;

namespace PortfolioIntelligence.Ai;

// Provider-neutral contracts (architecture.md §3.1, ADR-0007). Provider-specific details stay in the adapters.

public enum TaskClass
{
    HypothesisGeneration,
    FindingExtraction,
    AllocationProposal,
    ResultInterpretation,
    Summary,
}

public enum TaskKind
{
    /// <summary>Requires a passed, pre-registered qualification (T7).</summary>
    DecisionAffecting,

    /// <summary>No qualification; every output is validated, labelled and never affects a decision.</summary>
    Advisory,
}

/// <summary>What a task does when no qualified model is available or the call fails (D20).</summary>
public enum UnavailableBehaviour
{
    DeterministicFallback,
    Abstain,
    ManualReview,
    Skip,
}

public enum TaskOutcome
{
    Completed,
    Abstained,
    ManualReview,
    DeterministicFallback,
    Stopped,
}

public enum JobKind
{
    Qualification,
    ResearchCycle,
    Spike,
}

/// <summary>A job (qualification run, research cycle, spike) groups calls under one per-job cap.</summary>
public sealed record JobContext(string JobId, JobKind Kind);

/// <summary>An amount in US dollars at list prices (ADR-0010: the ledger is kept in USD).</summary>
public readonly record struct UsdAmount(decimal Value) : IComparable<UsdAmount>
{
    public static UsdAmount Zero { get; } = new(0m);

    public static UsdAmount operator +(UsdAmount a, UsdAmount b) => new(a.Value + b.Value);

    public static UsdAmount operator -(UsdAmount a, UsdAmount b) => new(a.Value - b.Value);

    public static UsdAmount operator *(UsdAmount a, decimal factor) => new(a.Value * factor);

    public static bool operator >(UsdAmount a, UsdAmount b) => a.Value > b.Value;

    public static bool operator <(UsdAmount a, UsdAmount b) => a.Value < b.Value;

    public static bool operator >=(UsdAmount a, UsdAmount b) => a.Value >= b.Value;

    public static bool operator <=(UsdAmount a, UsdAmount b) => a.Value <= b.Value;

    public int CompareTo(UsdAmount other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString("0.000000", CultureInfo.InvariantCulture) + " USD";
}

public enum AiRole
{
    User,
    Model,
    Tool,
}

public sealed record AiToolCall(string Name, JsonObject Arguments);

/// <summary>One conversation turn. History only grows by appending (architecture §3.1).</summary>
public sealed record AiMessage(AiRole Role, string? Text, IReadOnlyList<AiToolCall>? ToolCalls = null, string? ToolName = null, string? ToolResultJson = null);

public sealed record AiRequest(
    string Model,
    string SystemInstruction,
    IReadOnlyList<AiMessage> Messages,
    NeutralSchema? OutputSchema,
    NeutralToolSet? Tools,
    int MaxOutputTokens,
    TimeSpan Timeout);

public enum AiFinish
{
    Stop,
    ToolCall,
    MaxTokens,
    Refusal,
    Other,
}

/// <summary>Billed tokens. <see cref="OutputTokens"/> includes thinking tokens (S-G2: billed at the output price).</summary>
public sealed record AiUsage(int InputTokens, int OutputTokens);

public sealed record AiResponse(string ActualModel, string? Text, IReadOnlyList<AiToolCall> ToolCalls, AiFinish Finish, AiUsage Usage, string? ResponseId = null);

public enum AiBatchState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record AiBatchStatus(string Id, AiBatchState State, IReadOnlyList<AiResponse> Responses);

/// <summary>A provider failure. Retryable: network errors, 429 and 5xx (ADR-0010); 4xx otherwise is never retried.</summary>
public sealed class AiProviderException(string message, int? statusCode = null, TimeSpan? retryAfter = null, bool network = false, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool Network { get; } = network;

    public bool Retryable => Network || StatusCode is 429 or >= 500;
}

/// <summary>One adapter per provider; today only Gemini (D12).</summary>
public interface IAiProvider
{
    string Name { get; }

    Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken);

    Task<string> SubmitBatchAsync(IReadOnlyList<AiRequest> requests, CancellationToken cancellationToken);

    Task<AiBatchStatus> GetBatchAsync(string batchId, CancellationToken cancellationToken);

    Task CancelBatchAsync(string batchId, CancellationToken cancellationToken);

    /// <summary>Upper bound of the call's cost: estimated input tokens plus <see cref="AiRequest.MaxOutputTokens"/> at list price.</summary>
    UsdAmount EstimateMaxCost(AiRequest request);
}
