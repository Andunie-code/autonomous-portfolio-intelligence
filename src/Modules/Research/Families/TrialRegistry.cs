using System.Globalization;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.Statistics;

namespace PortfolioIntelligence.Research.Families;

public enum TrialOrigin
{
    Llm,
    Grid,
    Human,
    DevAssisted,
}

/// <summary>
/// A request to record a trial. <see cref="ClaimedFamilyId"/> is ignored: the system computes the family (H18).
/// <see cref="HypothesisId"/> links the trial to its research.hypotheses record (M4, migration 0006).
/// </summary>
public sealed record TrialRequest(DslDefinition Definition, TrialOrigin Origin, string ProtocolVersion, long? ClaimedFamilyId = null, long? HypothesisId = null);

public sealed record TrialOutcome(long TrialId, long FamilyId, bool NewTrial, bool NewFamily, string AssignedBy, double? Jaccard, double? Correlation);

public sealed record RepresentativeArtifacts(long FamilyId, FamilyArtifacts Artifacts);

/// <summary>Persistence for families and trials (in-memory for tests, PostgreSQL in <see cref="NpgsqlFamilyStore"/>).</summary>
public interface IFamilyStore
{
    Task<(long TrialId, long FamilyId)?> FindTrialAsync(string definitionHash, CancellationToken ct);

    Task<long?> FamilyOfStructureAsync(string structureHash, CancellationToken ct);

    /// <summary>Representatives' artefacts for one snapshot, in batches (never all in memory at once).</summary>
    IAsyncEnumerable<IReadOnlyList<RepresentativeArtifacts>> RepresentativesAsync(string snapshotHash, int batchSize, CancellationToken ct);

    Task<long> CreateFamilyAsync(string structureHash, string definitionHash, string snapshotHash, FamilyArtifacts artifacts, CancellationToken ct);

    Task AssignStructureAsync(string structureHash, long familyId, string assignedBy, string evidenceJson, CancellationToken ct);

    Task<long> AddTrialAsync(TrialRequest request, string definitionHash, string structureHash, long familyId, double periodSharpe, CancellationToken ct);

    /// <summary>Trial count and the per-period Sharpe ratios of all trials in the current cutoff period.</summary>
    Task<IReadOnlyList<double>> TrialSharpesAsync(CancellationToken ct);

    /// <summary>All trials of the current cutoff period, with or without a Sharpe ratio.</summary>
    Task<int> TrialCountAsync(CancellationToken ct);
}

/// <summary>
/// Family identity and the trial counter (m3-protocol §8, ADR-0015). A known structure hash keeps its family;
/// a new one is compared only with family representatives (batched) and joins the first family whose position
/// correlation ≥ 0.9 or entry Jaccard ≥ 0.8; otherwise it founds a family and becomes its representative.
/// The same definition hash is never a second trial.
/// </summary>
public sealed class TrialRegistry(IFamilyStore store, string snapshotHash, double correlationThreshold = 0.9, double jaccardThreshold = 0.8, int batchSize = 100)
{
    public async Task<TrialOutcome> RecordAsync(TrialRequest request, BacktestResult researchRun, CancellationToken ct)
    {
        var definitionHash = DslCanonical.DefinitionHash(request.Definition);
        if (await store.FindTrialAsync(definitionHash, ct) is { } existing)
        {
            return new TrialOutcome(existing.TrialId, existing.FamilyId, NewTrial: false, NewFamily: false, "existing-trial", null, null);
        }

        var structureHash = DslCanonical.StructureHash(request.Definition);
        var sharpe = ReturnStatistics.Sharpe(ReturnStatistics.LogReturns(researchRun.InitialCapital, researchRun.Equity));
        if (await store.FamilyOfStructureAsync(structureHash, ct) is { } known)
        {
            var id = await store.AddTrialAsync(request, definitionHash, structureHash, known, sharpe, ct);
            return new TrialOutcome(id, known, NewTrial: true, NewFamily: false, "structure", null, null);
        }

        var artifacts = FamilyArtifacts.From(researchRun);
        await foreach (var batch in store.RepresentativesAsync(snapshotHash, batchSize, ct))
        {
            foreach (var representative in batch)
            {
                var jaccard = FamilyArtifacts.Jaccard(artifacts, representative.Artifacts);
                var correlation = FamilyArtifacts.Correlation(artifacts, representative.Artifacts);
                if (correlation >= correlationThreshold || jaccard >= jaccardThreshold)
                {
                    var evidence = string.Create(CultureInfo.InvariantCulture,
                        $"{{\"jaccard\":{jaccard:R},\"correlation\":{correlation:R},\"snapshot\":\"{snapshotHash}\"}}");
                    await store.AssignStructureAsync(structureHash, representative.FamilyId, "similarity", evidence, ct);
                    var id = await store.AddTrialAsync(request, definitionHash, structureHash, representative.FamilyId, sharpe, ct);
                    return new TrialOutcome(id, representative.FamilyId, NewTrial: true, NewFamily: false, "similarity", jaccard, correlation);
                }
            }
        }

        var family = await store.CreateFamilyAsync(structureHash, definitionHash, snapshotHash, artifacts, ct);
        var trial = await store.AddTrialAsync(request, definitionHash, structureHash, family, sharpe, ct);
        return new TrialOutcome(trial, family, NewTrial: true, NewFamily: true, "first-member", null, null);
    }

    /// <summary>Whether this definition is already a trial of the current cutoff period (a repeat run is not a new trial).</summary>
    public async Task<bool> IsTrialAsync(DslDefinition definition, CancellationToken ct) =>
        await store.FindTrialAsync(DslCanonical.DefinitionHash(definition), ct) is not null;

    /// <summary>Global trial count of the current cutoff period.</summary>
    public async Task<int> CountAsync(CancellationToken ct) => await store.TrialCountAsync(ct);

    /// <summary>Trial context for the deflated Sharpe ratio (m3-protocol §6, G6): global count and Sharpe variance.</summary>
    public async Task<Evaluation.TrialContext> ContextAsync(double fallbackVariance, CancellationToken ct)
    {
        var sharpes = await store.TrialSharpesAsync(ct);
        if (sharpes.Count < 10)
        {
            return new Evaluation.TrialContext(Math.Max(1, sharpes.Count), fallbackVariance);
        }

        var mean = sharpes.Average();
        return new Evaluation.TrialContext(sharpes.Count, sharpes.Sum(s => (s - mean) * (s - mean)) / (sharpes.Count - 1));
    }
}

/// <summary>In-memory store (tests and dry runs). Representatives are held as artefacts only.</summary>
public sealed class InMemoryFamilyStore : IFamilyStore
{
    private readonly Dictionary<string, (long, long)> _trials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _structures = new(StringComparer.Ordinal);
    private readonly List<RepresentativeArtifacts> _representatives = [];
    private readonly List<double> _sharpes = [];

    public int RepresentativeReads { get; private set; }

    public Task<(long TrialId, long FamilyId)?> FindTrialAsync(string definitionHash, CancellationToken ct) =>
        Task.FromResult<(long, long)?>(_trials.TryGetValue(definitionHash, out var t) ? t : null);

    public Task<long?> FamilyOfStructureAsync(string structureHash, CancellationToken ct) =>
        Task.FromResult<long?>(_structures.TryGetValue(structureHash, out var f) ? f : null);

    public async IAsyncEnumerable<IReadOnlyList<RepresentativeArtifacts>> RepresentativesAsync(string snapshotHash, int batchSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < _representatives.Count; i += batchSize)
        {
            var batch = _representatives.Skip(i).Take(batchSize).ToList();
            RepresentativeReads += batch.Count;
            yield return batch;
            await Task.Yield();
        }
    }

    public Task<long> CreateFamilyAsync(string structureHash, string definitionHash, string snapshotHash, FamilyArtifacts artifacts, CancellationToken ct)
    {
        var id = _representatives.Count + 1L;
        _representatives.Add(new RepresentativeArtifacts(id, artifacts));
        _structures[structureHash] = id;
        return Task.FromResult(id);
    }

    public Task AssignStructureAsync(string structureHash, long familyId, string assignedBy, string evidenceJson, CancellationToken ct)
    {
        _structures.Add(structureHash, familyId);
        return Task.CompletedTask;
    }

    public Task<long> AddTrialAsync(TrialRequest request, string definitionHash, string structureHash, long familyId, double periodSharpe, CancellationToken ct)
    {
        var id = _trials.Count + 1L;
        _trials.Add(definitionHash, (id, familyId));
        _sharpes.Add(periodSharpe);
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<double>> TrialSharpesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<double>>(_sharpes.ToList());

    public Task<int> TrialCountAsync(CancellationToken ct) => Task.FromResult(_trials.Count);

    public int Families => _representatives.Count;
}
