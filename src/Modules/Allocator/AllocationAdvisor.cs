using System.Globalization;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Allocator;

/// <summary>
/// Whether the allocator may act in active mode (m8-protocol §4.2): AllocationProposal has a configured qualified model
/// whose qualification is still valid for this prompt and schema. The rule is the router's own (QualificationRegistry).
/// </summary>
public sealed class AllocatorGate(AiConfiguration configuration, QualificationRegistry qualifications)
{
    public async Task<bool> IsQualifiedAsync(CancellationToken ct)
    {
        foreach (var model in configuration.Policy(TaskClass.AllocationProposal).QualifiedModels ?? [])
        {
            if (model.PromptVersion == AllocationPrompt.PromptVersion
                && await qualifications.IsValidAsync(TaskClass.AllocationProposal, model, AllocationPrompt.Schema.Hash, ct))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The allocator (ADR-0023, m8-protocol §4–§5). One tool-less router call per live decision with at least one candidate.
/// Shadow mode calls the model in qualification mode under the registered shadow protocol and its own job and budget;
/// the result never affects real orders. Active mode calls in production mode (qualified models only). Every failure
/// returns no overrides, i.e. the deterministic default policy T11 (D20).
/// </summary>
public sealed class AllocationAdvisor(ModelRouter router, IQualificationStore protocols, ICostLedger ledger, TimeProvider time) : IAllocationAdvisor
{
    public async Task<AllocationDecision> AdviseAsync(AllocationRequest request, CancellationToken ct)
    {
        if (request.Mode == AllocatorMode.Disabled)
        {
            return AllocationDecision.Fallback(AllocationOutcome.Disabled);
        }

        if (request.Candidates.Count == 0)
        {
            return AllocationDecision.Fallback(AllocationOutcome.NoCandidates);
        }

        if (!request.Live)
        {
            return AllocationDecision.Fallback(AllocationOutcome.CatchUp);
        }

        var packet = AllocationPacket.Build(request);
        var validator = new AllocationOutputValidator(packet);
        var input = new TaskInput(AllocationPrompt.System, packet.Text, AllocationPrompt.Schema, packet.EvidenceIds, [validator]);
        TaskResult result;
        if (request.Mode == AllocatorMode.Shadow)
        {
            if (await ShadowProtocolAsync(request.ProtocolHash, ct) is not { } protocol)
            {
                return AllocationDecision.Fallback(AllocationOutcome.NoProtocol);
            }

            var job = ShadowJob(protocol.ProtocolHash);
            var spent = await ledger.SummaryAsync(time.GetUtcNow(), job.JobId, TimeSpan.Zero, ct);
            if (spent.Job.Value >= protocol.BudgetUsd)
            {
                return AllocationDecision.Fallback(AllocationOutcome.Budget);
            }

            result = await router.ExecuteForQualificationAsync(new QualificationSession(protocol, job, router), input, ct);
        }
        else
        {
            result = await router.ExecuteAsync(TaskClass.AllocationProposal, input, ActiveJob(time.GetUtcNow()), ct);
        }

        var safety = validator.SafetyEvents.Concat(result.SafetyViolations).ToList();
        if (safety.Count > 0)
        {
            return AllocationDecision.Fallback(AllocationOutcome.Safety, result.TaskId, safety);
        }

        var accepted = request.Mode == AllocatorMode.Shadow
            ? result.Outcome == TaskOutcome.Completed && !result.Degraded
            : result.MayAffectDecisions;
        if (!accepted || result.Output is null)
        {
            return AllocationDecision.Fallback(Classify(result, validator), result.TaskId);
        }

        var items = AllocationProposalReader.Items(result.Output, packet);
        return new AllocationDecision(AllocationOutcome.Proposal, AllocationProposalReader.Overrides(items), items, [], result.TaskId);
    }

    /// <summary>Job of a shadow protocol: one per registered protocol (pilot or qualification window), qualification kind.</summary>
    public static JobContext ShadowJob(string protocolHash) => new($"alloc-{protocolHash[..12]}", JobKind.Qualification);

    /// <summary>Job of active-mode production calls: one per calendar month.</summary>
    public static JobContext ActiveJob(DateTimeOffset now) => new(string.Create(CultureInfo.InvariantCulture, $"allocation-{now:yyyy-MM}"), JobKind.Allocation);

    private async Task<QualificationProtocol?> ShadowProtocolAsync(string? hash, CancellationToken ct)
    {
        if (hash is null || await protocols.FindProtocolAsync(hash, ct) is not { } protocol)
        {
            return null;
        }

        // The registered protocol must be an allocator protocol of this very prompt and schema (m8-protocol §8).
        return protocol.Task == TaskClass.AllocationProposal
            && protocol.Design == QualificationDesign.NonInferiority
            && protocol.PromptVersion == AllocationPrompt.PromptVersion
            && protocol.SchemaHash == AllocationPrompt.Schema.Hash
            ? protocol
            : null;
    }

    private static string Classify(TaskResult result, AllocationOutputValidator validator)
    {
        var reason = result.Reason ?? "";
        if (result.Outcome == TaskOutcome.Stopped)
        {
            return AllocationOutcome.AiKillSwitch;
        }

        if (reason.StartsWith("no qualified model", StringComparison.Ordinal))
        {
            return AllocationOutcome.NoQualifiedModel;
        }

        if (reason.StartsWith(nameof(CallStatus.BudgetRefused), StringComparison.Ordinal))
        {
            return AllocationOutcome.Budget;
        }

        if (reason.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return AllocationOutcome.Timeout;
        }

        if (validator.Classification is { } classification)
        {
            return classification;
        }

        return reason.Contains("schema", StringComparison.Ordinal) ? AllocationOutcome.Schema : AllocationOutcome.ProviderError;
    }
}
