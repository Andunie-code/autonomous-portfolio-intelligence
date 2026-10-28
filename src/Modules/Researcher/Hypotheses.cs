using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Researcher;

public sealed record HypothesisPoint(string Label, string DefinitionJson, string DefinitionHash);

/// <summary>
/// A hypothesis and its grid points. <see cref="SourceFindingIds"/> names the verified findings it was written from (M7,
/// m7-protocol §8.2); the hypothesis store refuses unverified or post-cutoff findings. The pipeline is the same either way.
/// </summary>
public sealed record Hypothesis(string Key, string Title, string StructureHash, IReadOnlyList<HypothesisPoint> Points, IReadOnlyList<long>? SourceFindingIds = null);

/// <summary>Who proposed a hypothesis and what the proposer could know (m4-protocol §3).</summary>
public sealed record HypothesisProvenance(string Origin, string PointOrigin, string Author, DateOnly? KnowledgeCutoff, bool KnowledgeOverlapsDiagnostic, string? ApprovedBy);

/// <summary>
/// A pre-registered hypothesis list (m4-hypotheses.v1.json). Loading re-parses and re-validates every definition and
/// recomputes its definition and structure hashes and the list hash; any mismatch refuses the list, so the run cannot
/// drift from the pre-registration.
/// </summary>
public sealed record HypothesisList(string Version, IReadOnlyList<Hypothesis> Hypotheses, string ListHash)
{
    public int TrialCount => Hypotheses.Sum(h => h.Points.Count);

    /// <summary>SHA-256 of the sorted lowercase definition hashes joined by "\n" (m4-protocol §2).</summary>
    public static string ListHashOf(IEnumerable<string> definitionHashes) =>
        DslCanonical.Sha256(string.Join("\n", definitionHashes.Order(StringComparer.Ordinal)));

    public static HypothesisList Load(string json, string expectedListHash)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Hypothesis list is not a JSON object.");
        var hypotheses = new List<Hypothesis>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in root["hypotheses"]!.AsArray())
        {
            var key = node!["id"]!.GetValue<string>();
            var structure = node["structureHash"]!.GetValue<string>();
            var points = new List<HypothesisPoint>();
            foreach (var point in node["points"]!.AsArray())
            {
                var definitionJson = point!["definition"]!.ToJsonString();
                var definition = DslParser.Parse(definitionJson);
                var hash = DslCanonical.DefinitionHash(definition);
                if (hash != point["definitionHash"]!.GetValue<string>())
                {
                    throw new InvalidDataException($"{key}/{point["point"]}: definition hash does not match the pre-registration.");
                }

                if (DslCanonical.StructureHash(definition) != structure)
                {
                    throw new InvalidDataException($"{key}/{point["point"]}: structure hash does not match the hypothesis.");
                }

                if (!seen.Add(hash))
                {
                    throw new InvalidDataException($"{key}/{point["point"]}: duplicate definition.");
                }

                points.Add(new HypothesisPoint(point["point"]!.GetValue<string>(), definitionJson, hash));
            }

            var findings = node["sourceFindingIds"]?.AsArray().Select(f => f!.GetValue<long>()).ToList();
            hypotheses.Add(new Hypothesis(key, node["title"]!.GetValue<string>(), structure, points, findings));
        }

        if (hypotheses.Select(h => h.StructureHash).Distinct(StringComparer.Ordinal).Count() != hypotheses.Count)
        {
            throw new InvalidDataException("Two hypotheses share a structure.");
        }

        var listHash = ListHashOf(seen);
        if (listHash != expectedListHash)
        {
            throw new InvalidDataException($"List hash {listHash} differs from the pre-registered {expectedListHash}.");
        }

        if (root["trialCount"]?.GetValue<int>() is { } count && count != seen.Count)
        {
            throw new InvalidDataException($"trialCount {count} differs from the {seen.Count} definitions.");
        }

        return new HypothesisList(root["version"]!.GetValue<string>(), hypotheses, listHash);
    }
}

/// <summary>A source of hypotheses for one research cycle.</summary>
public interface IHypothesisSource
{
    string Name { get; }

    Task<IReadOnlyList<Hypothesis>> HypothesesAsync(CancellationToken ct);
}

/// <summary>Human-approved hypotheses from a pre-registered list (LLM-less mode, architecture §3.2).</summary>
public sealed class ApprovedHypothesisSource(HypothesisList list, HypothesisProvenance provenance) : IHypothesisSource
{
    public string Name => "approved:" + list.Version;

    public HypothesisProvenance Provenance => provenance;

    public Task<IReadOnlyList<Hypothesis>> HypothesesAsync(CancellationToken ct) => Task.FromResult(list.Hypotheses);
}

/// <summary>Grid expansion: each grid point of a hypothesis becomes one experiment (one trial), in pre-registered order.</summary>
public static class GridExpansion
{
    public static IEnumerable<(Hypothesis Hypothesis, HypothesisPoint Point)> Expand(IEnumerable<Hypothesis> hypotheses) =>
        hypotheses.SelectMany(h => h.Points.Select(p => (h, p)));
}

/// <summary>
/// Hypotheses from the HypothesisGeneration task. Without a qualified model the router makes no call and returns the
/// deterministic fallback; this source then yields nothing and the cycle continues with grid and approved hypotheses (D20).
/// Only a result that may affect decisions (completed, not degraded, production, no safety violation) is used.
/// </summary>
public sealed class LlmHypothesisSource(ModelRouter router, JobContext job, Func<TaskInput> input) : IHypothesisSource
{
    public string Name => "llm";

    public TaskResult? LastResult { get; private set; }

    public async Task<IReadOnlyList<Hypothesis>> HypothesesAsync(CancellationToken ct)
    {
        LastResult = await router.ExecuteAsync(TaskClass.HypothesisGeneration, input(), job, ct);
        if (!LastResult.MayAffectDecisions || LastResult.Output?["hypotheses"] is not JsonArray items)
        {
            return [];
        }

        var result = new List<Hypothesis>();
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["definition"] is not JsonValue value || !value.TryGetValue<string>(out var definitionJson))
            {
                continue;
            }

            try
            {
                var definition = DslParser.Parse(definitionJson);
                var hash = DslCanonical.DefinitionHash(definition);
                var title = item["title"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : definition.Name;
                result.Add(new Hypothesis("llm-" + hash[..12], title,
                    DslCanonical.StructureHash(definition), [new HypothesisPoint("llm", definitionJson, hash)]));
            }
            catch (DslException)
            {
                // An invalid definition is dropped; the invalid-DSL rate is a secondary qualification metric.
            }
        }

        return result;
    }
}
