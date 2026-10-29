namespace PortfolioIntelligence.Kernel.Backtest;

/// <summary>
/// The allocator's adjustment of one entry candidate (m8-protocol §3.2, ADR-0023): a multiplier of its T11 weight or a
/// veto. It only ever reaches new entries of the step it is given to; exits and stops never read it.
/// </summary>
public sealed record AllocationOverride(int Sleeve, string Symbol, decimal Multiplier, bool Veto = false);

/// <summary>A new entry of the current step as T11 sizes it before any override (m8-protocol §3.1).</summary>
public sealed record EntryCandidate(int Sleeve, string Symbol, decimal Weight, decimal Volatility);

/// <summary>Approved band of the allocator multiplier (D34, U 2026-10-29): w' = m × w_T11 with m within [Low, High], or a veto.</summary>
public static class AllocatorBand
{
    public const string Version = "allocator-band v1";

    public const decimal Low = 0.5m;

    public const decimal High = 1.5m;

    public static decimal Clamp(decimal multiplier) => Math.Clamp(multiplier, Low, High);
}

/// <summary>Allocator mode (m8-protocol §4.1). Without a mode event the allocator is disabled.</summary>
public enum AllocatorMode
{
    Disabled,
    Shadow,
    Active,
}

/// <summary>An open position the allocator sees as context only; it can never be the subject of a proposal.</summary>
public sealed record AllocationHolding(int Sleeve, string Symbol, decimal Weight, bool PendingExit);

/// <summary>Summary of the remaining limits (m8-protocol §5.1); never the RiskConfig itself.</summary>
public sealed record AllocationLimits(decimal ExposureShare, decimal ExperimentalShare, int OpenPositions, int MaxPositions, decimal MaxAssetWeight);

/// <summary>
/// One allocator decision request (port between Execution and the Allocator module, ADR-0023): the step's entry
/// candidates of the sleeves the allocator may adjust, with read-only portfolio context. <see cref="Live"/> is false for
/// catch-up bars. <see cref="ProtocolHash"/> is the registered shadow protocol (shadow mode only).
/// </summary>
public sealed record AllocationRequest(
    string RunKey,
    DateTimeOffset DecisionTime,
    AllocatorMode Mode,
    bool Live,
    IReadOnlyList<string> Strategies,
    IReadOnlyList<EntryCandidate> Candidates,
    IReadOnlyList<AllocationHolding> Holdings,
    AllocationLimits Limits,
    string? MarketRegime,
    string? ProtocolHash);

/// <summary>A candidate as decided: the proposed and applied multiplier, a veto, and the band clamp if one applied.</summary>
public sealed record AllocationItem(int Sleeve, string Symbol, decimal T11Weight, decimal? Proposed, decimal Multiplier, bool Veto, string? Clamp);

/// <summary>Outcome codes of an allocator decision (m8-protocol §5.4): a valid proposal or the reason the step keeps T11.</summary>
public static class AllocationOutcome
{
    public const string Proposal = "proposal";
    public const string Disabled = "disabled";
    public const string NoCandidates = "no-candidates";
    public const string CatchUp = "catch-up";
    public const string NotValidated = "not-validated";
    public const string NoKey = "no-key";
    public const string NoProtocol = "no-protocol";
    public const string NoQualifiedModel = "no-qualified-model";
    public const string AiKillSwitch = "ai-kill-switch";
    public const string Budget = "budget";
    public const string Timeout = "timeout";
    public const string ProviderError = "provider-error";
    public const string Schema = "schema";
    public const string Evidence = "evidence";
    public const string Safety = "safety";

    /// <summary>Outcomes in which a model call was attempted (the denominator of the valid-proposal rate, A-3).</summary>
    public static bool IsCall(string outcome) => outcome is Proposal or Timeout or ProviderError or Schema or Evidence or Safety;
}

/// <summary>An allocator decision: overrides (empty means T11), the per-candidate items, and any safety events.</summary>
public sealed record AllocationDecision(
    string Outcome,
    IReadOnlyList<AllocationOverride> Overrides,
    IReadOnlyList<AllocationItem> Items,
    IReadOnlyList<string> SafetyEvents,
    Guid? TaskId = null)
{
    public static AllocationDecision Fallback(string outcome, Guid? taskId = null, IReadOnlyList<string>? safety = null) =>
        new(outcome, [], [], safety ?? [], taskId);
}

/// <summary>The allocator port (ADR-0023). Implemented by the Allocator module, wired by Trading.Host; Execution only sees this.</summary>
public interface IAllocationAdvisor
{
    Task<AllocationDecision> AdviseAsync(AllocationRequest request, CancellationToken ct);
}

/// <summary>The advisor without the allocator wired (no key, or the allocator is not configured): always T11.</summary>
public sealed class DeterministicAllocationAdvisor(string outcome = AllocationOutcome.NoKey) : IAllocationAdvisor
{
    public Task<AllocationDecision> AdviseAsync(AllocationRequest request, CancellationToken ct) =>
        Task.FromResult(AllocationDecision.Fallback(request.Mode == AllocatorMode.Disabled ? AllocationOutcome.Disabled : outcome));
}
