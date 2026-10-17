using System.Text.Json.Nodes;

namespace PortfolioIntelligence.Researcher;

public enum HypothesisCategory
{
    /// <summary>Passed every research gate. Opens no transition by itself (m4-protocol §6).</summary>
    Candidate,
    Rejected,
    Error,
}

public sealed record HypothesisVerdict(string Key, string Title, HypothesisCategory Category, string? SelectedPoint, IReadOnlyList<string> Failures, JsonNode Details);

/// <summary>
/// Deterministic, non-tool side of a research cycle (not exposed to any model): hypothesis records and the
/// pre-registered selection and gate evaluation of a hypothesis once all its points are submitted.
/// </summary>
public interface IHypothesisPipeline
{
    Task RegisterHypothesisAsync(Hypothesis hypothesis, HypothesisProvenance provenance, CancellationToken ct);

    Task<HypothesisVerdict> EvaluateHypothesisAsync(Hypothesis hypothesis, CancellationToken ct);
}

public sealed record PointRecord(string HypothesisKey, string Point, string DefinitionHash, JsonNode Validation, JsonNode Submission, JsonNode? Result);

public sealed record CycleReport(IReadOnlyList<HypothesisVerdict> Verdicts, IReadOnlyList<PointRecord> Points, IReadOnlyList<string> SafetyViolations);

/// <summary>
/// A research cycle in LLM-less mode (m4-protocol §5): every grid point of every approved hypothesis goes through the
/// Researcher tools exactly as a model's tool calls would (ValidateStrategyDefinition → SubmitExperiment →
/// GetExperimentResult); then the pipeline selects and gates each hypothesis. Order: hypotheses and points as listed.
/// </summary>
public sealed class ResearchCycle(ResearcherToolbox toolbox, IHypothesisPipeline pipeline, InMemorySafetyLog safety)
{
    public async Task<CycleReport> RunAsync(ApprovedHypothesisSource source, Action<string> progress, CancellationToken ct)
    {
        var hypotheses = await source.HypothesesAsync(ct);
        var points = new List<PointRecord>();
        var verdicts = new List<HypothesisVerdict>();
        foreach (var hypothesis in hypotheses)
        {
            await pipeline.RegisterHypothesisAsync(hypothesis, source.Provenance, ct);
            foreach (var (_, point) in GridExpansion.Expand([hypothesis]))
            {
                var validation = await toolbox.InvokeAsync(ResearcherToolNames.ValidateStrategyDefinition, new JsonObject { ["definition"] = point.DefinitionJson }, ct);
                var submission = await toolbox.InvokeAsync(ResearcherToolNames.SubmitExperiment, new JsonObject
                {
                    ["hypothesis"] = hypothesis.Key,
                    ["definition"] = point.DefinitionJson,
                    ["origin"] = source.Provenance.PointOrigin,
                }, ct);
                JsonNode? result = null;
                if (submission["experimentId"] is JsonValue id && id.TryGetValue<long>(out var experimentId))
                {
                    result = await toolbox.InvokeAsync(ResearcherToolNames.GetExperimentResult, new JsonObject { ["experimentId"] = experimentId }, ct);
                }

                points.Add(new PointRecord(hypothesis.Key, point.Label, point.DefinitionHash, validation, submission, result));
                progress($"{hypothesis.Key}/{point.Label}: {Summary(submission, result)}");
            }

            var verdict = await pipeline.EvaluateHypothesisAsync(hypothesis, ct);
            verdicts.Add(verdict);
            progress($"{hypothesis.Key}: {verdict.Category} (selected {verdict.SelectedPoint ?? "-"}; {(verdict.Failures.Count == 0 ? "no failures" : string.Join(", ", verdict.Failures))})");
        }

        return new CycleReport(verdicts, points, safety.Violations);
    }

    private static string Summary(JsonNode submission, JsonNode? result) =>
        submission["error"] is { } error ? "error " + error
        : result?["primaryMetric"] is { } metric ? $"trial {submission["trialId"]} ({(submission["newTrial"]?.GetValue<bool>() == true ? "new" : "existing")}), M_R {metric}"
        : submission.ToJsonString();
}
