using System.Text;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Ai;

/// <summary>
/// Decision design of a qualification (ADR-0022 §5): two arms with a one-sided Fisher exact test against the deterministic
/// baseline (HypothesisGeneration), or one arm with a one-sided exact binomial test against a pre-registered rate p0 (tasks
/// without a deterministic alternative, such as FindingExtraction). AllocationProposal uses a forward non-inferiority
/// design against the deterministic default policy in shadow mode (m8-protocol §6): <see cref="QualificationProtocol.MinimumEffect"/>
/// is the annualised margin and <see cref="QualificationProtocol.SamplePerArm"/> the window length in days. It is not run
/// by <see cref="QualificationHarness"/>; the allocator's evaluator records its result.
/// </summary>
public enum QualificationDesign
{
    TwoArmFisher,
    OneArmBinomial,
    NonInferiority,
}

/// <summary>
/// A pre-registered qualification protocol (ADR-0010, testing §6.1). <see cref="ProtocolHash"/> is the SHA-256 of
/// the protocol document; the harness refuses to run without a registered hash that matches the document. Any change
/// (model, prompt, schema, set) is a new protocol with a new set. <see cref="P0"/> is used only by the one-arm design.
/// </summary>
public sealed record QualificationProtocol(
    string ProtocolHash,
    string DocumentCommit,
    TaskClass Task,
    string Provider,
    string Model,
    string PromptVersion,
    string SchemaHash,
    string PilotSetHash,
    string QualificationSetHash,
    double Alpha,
    double MinimumEffect,
    int SamplePerArm,
    decimal BudgetUsd,
    QualificationDesign Design = QualificationDesign.TwoArmFisher,
    double P0 = 0)
{
    public static string HashDocument(string document) => DslCanonical.Sha256(document.ReplaceLineEndings("\n"));
}

public enum QualificationVerdict
{
    Passed,
    Failed,
    Inconclusive,
}

/// <summary>The append-only result of one qualification run (TaskQualification). "Failed" and "Inconclusive" are recorded too.</summary>
public sealed record TaskQualification(
    long Id,
    string ProtocolHash,
    DateTimeOffset At,
    QualificationVerdict Verdict,
    int TrialsLlm,
    int SuccessesLlm,
    int TrialsBaseline,
    int SuccessesBaseline,
    double PValue,
    int SafetyViolations,
    UsdAmount Cost,
    string Notes);

public interface IQualificationStore
{
    Task RegisterProtocolAsync(QualificationProtocol protocol, CancellationToken ct);

    Task<QualificationProtocol?> FindProtocolAsync(string protocolHash, CancellationToken ct);

    Task<long> AddResultAsync(TaskQualification result, CancellationToken ct);

    Task<TaskQualification?> FindResultAsync(long qualificationId, CancellationToken ct);
}

/// <summary>Whether a configured qualified model is still qualified (a model, prompt or schema change drops it).</summary>
public sealed class QualificationRegistry(IQualificationStore store)
{
    public async Task<bool> IsValidAsync(TaskClass task, QualifiedModel model, string schemaHash, CancellationToken ct)
    {
        if (await store.FindResultAsync(model.QualificationId, ct) is not { Verdict: QualificationVerdict.Passed } result)
        {
            return false;
        }

        return await store.FindProtocolAsync(result.ProtocolHash, ct) is { } protocol
            && protocol.Task == task
            && protocol.Provider == model.Provider
            && protocol.Model == model.Model
            && protocol.PromptVersion == model.PromptVersion
            && protocol.SchemaHash == schemaHash;
    }
}

public sealed class InMemoryQualificationStore : IQualificationStore
{
    private readonly Dictionary<string, QualificationProtocol> _protocols = new(StringComparer.Ordinal);
    private readonly List<TaskQualification> _results = [];

    public IReadOnlyList<TaskQualification> Results => _results.ToList();

    public Task RegisterProtocolAsync(QualificationProtocol protocol, CancellationToken ct)
    {
        if (!_protocols.TryAdd(protocol.ProtocolHash, protocol))
        {
            throw new InvalidOperationException("Protocol already registered; a change needs a new protocol.");
        }

        return Task.CompletedTask;
    }

    public Task<QualificationProtocol?> FindProtocolAsync(string protocolHash, CancellationToken ct) =>
        Task.FromResult(_protocols.GetValueOrDefault(protocolHash));

    public Task<long> AddResultAsync(TaskQualification result, CancellationToken ct)
    {
        var id = _results.Count + 1L;
        _results.Add(result with { Id = id });
        return Task.FromResult(id);
    }

    public Task<TaskQualification?> FindResultAsync(long qualificationId, CancellationToken ct) =>
        Task.FromResult(_results.FirstOrDefault(r => r.Id == qualificationId));
}

/// <summary>One item of a pilot or qualification set; the set hash is over the sorted item ids and contents.</summary>
public sealed record QualificationItem(string Id, string Content)
{
    public static string SetHash(IEnumerable<QualificationItem> items) =>
        DslCanonical.Sha256(string.Join("\n", items.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id + "\t" + DslCanonical.Sha256(i.Content))));
}

/// <summary>Outcome of one item in one arm: whether it counts as a success and any safety violations (tolerance 0).</summary>
public sealed record ArmOutcome(bool Success, IReadOnlyList<string> SafetyViolations);

/// <summary>A task-specific qualification: the LLM arm (through the router in qualification mode) and the deterministic baseline arm.</summary>
public interface IQualificationTask
{
    TaskClass Task { get; }

    Task<ArmOutcome> RunLlmAsync(QualificationSession session, QualificationItem item, CancellationToken ct);

    Task<ArmOutcome> RunBaselineAsync(QualificationItem item, CancellationToken ct);
}

/// <summary>A qualification run's handle to the router: calls go out in qualification mode under the protocol's job.</summary>
public sealed record QualificationSession(QualificationProtocol Protocol, JobContext Job, ModelRouter Router);

/// <summary>
/// The pre-registration harness (T8). It refuses to run unless the protocol document hashes to a registered protocol,
/// the pilot and qualification sets match their registered hashes and are disjoint, and the run stays within the
/// qualification job cap. Verdict: any safety violation → Failed; fewer completed items than the pre-registered
/// sample (budget, kill switch) → Inconclusive; otherwise one-sided Fisher exact p &lt; α and a rate difference ≥ the
/// minimum effect → Passed, else Failed. The result is always recorded.
/// In the one-arm design (ADR-0022 §5) the baseline arm is not run and the test is the one-sided exact binomial test of
/// H0: p ≤ p0; p &lt; α → Passed, else Failed (safety violations and an incomplete sample decide first, as above).
/// </summary>
public sealed class QualificationHarness(IQualificationStore store, ModelRouter router, ICostLedger ledger, TimeProvider time)
{
    public async Task<TaskQualification> RunAsync(
        string protocolDocument,
        IReadOnlyList<QualificationItem> pilotSet,
        IReadOnlyList<QualificationItem> qualificationSet,
        IQualificationTask task,
        CancellationToken ct)
    {
        var hash = QualificationProtocol.HashDocument(protocolDocument);
        var protocol = await store.FindProtocolAsync(hash, ct)
            ?? throw new InvalidOperationException("No registered qualification protocol for this document hash; the harness does not run without one.");
        if (protocol.Design == QualificationDesign.NonInferiority)
        {
            throw new InvalidOperationException("A non-inferiority protocol runs in shadow mode (m8-protocol §6), not in the item harness.");
        }

        if (protocol.Task != task.Task)
        {
            throw new InvalidOperationException($"Protocol is for {protocol.Task}, not {task.Task}.");
        }

        if (QualificationItem.SetHash(pilotSet) != protocol.PilotSetHash || QualificationItem.SetHash(qualificationSet) != protocol.QualificationSetHash)
        {
            throw new InvalidOperationException("Pilot or qualification set does not match the registered set hash.");
        }

        if (pilotSet.Select(i => i.Id).Intersect(qualificationSet.Select(i => i.Id), StringComparer.Ordinal).Any()
            || pilotSet.Select(i => i.Content).Intersect(qualificationSet.Select(i => i.Content), StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("Pilot and qualification sets overlap.");
        }

        var job = new JobContext($"qualification-{hash[..12]}", JobKind.Qualification);
        var session = new QualificationSession(protocol, job, router);
        var notes = new StringBuilder();
        int llmTrials = 0, llmSuccesses = 0, baseTrials = 0, baseSuccesses = 0, violations = 0;
        foreach (var item in qualificationSet.Take(protocol.SamplePerArm))
        {
            if (await router.KillSwitch.IsTrippedAsync(ct))
            {
                notes.Append("stopped: AI kill switch; ");
                break;
            }

            var summary = await ledger.SummaryAsync(time.GetUtcNow(), job.JobId, TimeSpan.Zero, ct);
            if (summary.Job.Value >= protocol.BudgetUsd)
            {
                notes.Append("stopped: protocol budget reached; ");
                break;
            }

            var llm = await task.RunLlmAsync(session, item, ct);
            violations += llm.SafetyViolations.Count;
            llmTrials++;
            llmSuccesses += llm.Success ? 1 : 0;
            if (protocol.Design == QualificationDesign.TwoArmFisher)
            {
                var baseline = await task.RunBaselineAsync(item, ct);
                violations += baseline.SafetyViolations.Count;
                baseTrials++;
                baseSuccesses += baseline.Success ? 1 : 0;
            }
        }

        bool passes;
        double p;
        if (protocol.Design == QualificationDesign.OneArmBinomial)
        {
            p = llmTrials == 0 ? 1 : BinomialExact.GreaterPValue(llmSuccesses, llmTrials, protocol.P0);
            passes = p < protocol.Alpha;
        }
        else
        {
            p = llmTrials == 0 ? 1 : FisherExact.GreaterPValue(llmSuccesses, llmTrials, baseSuccesses, baseTrials);
            var effect = llmTrials == 0 ? 0 : (double)llmSuccesses / llmTrials - (double)baseSuccesses / baseTrials;
            passes = p < protocol.Alpha && effect >= protocol.MinimumEffect;
        }

        var verdict = violations > 0 ? QualificationVerdict.Failed
            : llmTrials < protocol.SamplePerArm ? QualificationVerdict.Inconclusive
            : passes ? QualificationVerdict.Passed
            : QualificationVerdict.Failed;
        if (violations > 0)
        {
            notes.Append($"{violations} safety violation(s); ");
        }

        var cost = (await ledger.SummaryAsync(time.GetUtcNow(), job.JobId, TimeSpan.Zero, ct)).Job;
        var result = new TaskQualification(0, hash, time.GetUtcNow(), verdict, llmTrials, llmSuccesses, baseTrials, baseSuccesses, p, violations, cost, notes.ToString().Trim());
        var id = await store.AddResultAsync(result, ct);
        return result with { Id = id };
    }
}
