using System.Globalization;
using System.Text.Json.Nodes;

namespace PortfolioIntelligence.Ai;

public enum RouterMode
{
    Production,
    Qualification,
}

/// <summary>
/// One record per LLM task (architecture §3.3). <see cref="Degraded"/> is set when the reported model differs from
/// the requested one; a degraded result never passes a gate.
/// </summary>
public sealed record DecisionTrace(
    Guid TaskId,
    DateTimeOffset At,
    string JobId,
    TaskClass Task,
    RouterMode Mode,
    string InputHash,
    IReadOnlyList<string> EvidenceIds,
    string? Provider,
    string? RequestedModel,
    string? ActualModel,
    string? PromptVersion,
    long? QualificationId,
    string? QualificationProtocolHash,
    int Calls,
    int InputTokens,
    int OutputTokens,
    long LatencyMs,
    UsdAmount Estimated,
    UsdAmount Actual,
    IReadOnlyList<string> ValidatorResults,
    IReadOnlyList<string> SafetyViolations,
    TaskOutcome Outcome,
    bool Degraded,
    string? Reason);

public interface IDecisionTraceStore
{
    Task AddAsync(DecisionTrace trace, CancellationToken ct);
}

public sealed class InMemoryDecisionTraceStore : IDecisionTraceStore
{
    private readonly List<DecisionTrace> _traces = [];

    public IReadOnlyList<DecisionTrace> Traces => _traces.ToList();

    public Task AddAsync(DecisionTrace trace, CancellationToken ct)
    {
        _traces.Add(trace);
        return Task.CompletedTask;
    }
}

/// <summary>A runtime check on a task's output, run after schema validation.</summary>
public interface IOutputValidator
{
    string Name { get; }

    Task<IReadOnlyList<string>> ValidateAsync(JsonNode output, DateTimeOffset decisionTime, CancellationToken ct);
}

/// <summary>
/// What an evidence reference resolves to: when it became available, its primary numeric value and, optionally, named
/// metrics. A claim that names a <c>metric</c> is checked against that metric; otherwise against <see cref="Value"/>.
/// </summary>
public sealed record EvidenceItem(string Id, DateTimeOffset AvailableAt, decimal? Value, IReadOnlyDictionary<string, decimal>? Metrics = null);

public interface IEvidenceResolver
{
    Task<EvidenceItem?> ResolveAsync(string evidenceRef, CancellationToken ct);
}

/// <summary>
/// EvidenceValidator (architecture §3.3). The output carries <c>claims: [{ text, critical, evidenceRef, value }]</c>.
/// Every claim with a reference must resolve to evidence that was available at decision time; a numeric value must
/// match the source within a relative tolerance. A critical claim without a reference is an error. In a
/// decision-affecting task a single error rejects the output; a fabricated reference never passes (testing §6.1).
/// </summary>
public sealed class EvidenceValidator(IEvidenceResolver resolver, decimal relativeTolerance = 0.005m) : IOutputValidator
{
    public string Name => "evidence";

    public async Task<IReadOnlyList<string>> ValidateAsync(JsonNode output, DateTimeOffset decisionTime, CancellationToken ct)
    {
        var errors = new List<string>();
        if (output["claims"] is not JsonArray claims)
        {
            return errors;
        }

        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i] as JsonObject;
            var critical = claim?["critical"]?.GetValue<bool>() ?? true;
            var reference = claim?["evidenceRef"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reference))
            {
                if (critical)
                {
                    errors.Add($"claim {i}: critical claim without evidenceRef");
                }

                continue;
            }

            var evidence = await resolver.ResolveAsync(reference, ct);
            if (evidence is null)
            {
                errors.Add($"claim {i}: evidenceRef '{reference}' does not exist");
                continue;
            }

            if (evidence.AvailableAt > decisionTime)
            {
                errors.Add($"claim {i}: evidenceRef '{reference}' was not available at decision time");
                continue;
            }

            if (claim?["value"] is { } valueNode)
            {
                var claimed = decimal.Parse(valueNode.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                var metric = claim["metric"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
                decimal? source = metric is null ? evidence.Value : evidence.Metrics?.TryGetValue(metric, out var named) == true ? named : null;
                if (source is not { } actual)
                {
                    errors.Add(metric is null ? $"claim {i}: numeric claim but '{reference}' has no value" : $"claim {i}: '{reference}' has no metric '{metric}'");
                }
                else if (Math.Abs(claimed - actual) > Math.Abs(actual) * relativeTolerance + 1e-12m)
                {
                    errors.Add(string.Create(CultureInfo.InvariantCulture, $"claim {i}: value {claimed} does not match '{reference}'{(metric is null ? "" : "." + metric)} ({actual})"));
                }
            }
        }

        return errors;
    }
}
