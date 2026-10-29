using System.Globalization;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Allocator;

/// <summary>
/// Output check of one allocator call (m8-protocol §5.3), run by the router after the schema. A reference to a holding
/// is an exit-interference attempt: a safety event that rejects the whole proposal. Unknown or repeated candidate
/// references and an over-long rationale reject it as schema errors; a failed evidence or numeric claim as an evidence
/// error. Messages carry entry and claim indices only, never a reference value or a number: they are written to
/// <c>ai.decision_traces</c>. The instance is per call; <see cref="Classification"/> tells the advisor why it failed.
/// </summary>
public sealed class AllocationOutputValidator(AllocationPacket packet) : IOutputValidator
{
    private readonly EvidenceValidator _evidence = new(packet);

    public string Name => "allocation";

    /// <summary>Null when the output passed; otherwise <see cref="AllocationOutcome.Safety"/>, <see cref="AllocationOutcome.Evidence"/> or <see cref="AllocationOutcome.Schema"/>.</summary>
    public string? Classification { get; private set; }

    public IReadOnlyList<string> SafetyEvents => _safety;

    private readonly List<string> _safety = [];

    public async Task<IReadOnlyList<string>> ValidateAsync(JsonNode output, DateTimeOffset decisionTime, CancellationToken ct)
    {
        var schema = new List<string>();
        var evidence = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = output["entries"]?.AsArray() ?? [];
        for (var i = 0; i < entries.Count; i++)
        {
            var reference = entries[i]?["candidateRef"]?.GetValue<string>() ?? "";
            if (packet.Holdings.Contains(reference))
            {
                _safety.Add($"exit-interference: entry {i} references a holding");
                continue;
            }

            if (!packet.Candidates.ContainsKey(reference))
            {
                schema.Add($"entry {i}: unknown candidateRef");
            }
            else if (!seen.Add(reference))
            {
                schema.Add($"entry {i}: repeated candidateRef");
            }

            foreach (var node in entries[i]?["evidenceRefs"]?.AsArray() ?? [])
            {
                if (!packet.Contains(node?.GetValue<string>() ?? ""))
                {
                    evidence.Add($"entry {i}: unknown evidenceRef");
                }
            }
        }

        if ((output["rationale"]?.GetValue<string>() ?? "").Length > AllocationPrompt.MaxRationaleCharacters)
        {
            schema.Add("rationale too long");
        }

        // Claims may cite holdings as context; only proposing an entry for one is an interference attempt.
        var failed = (await _evidence.ValidateAsync(output, decisionTime, ct)).Count;
        if (failed > 0)
        {
            evidence.Add(string.Create(CultureInfo.InvariantCulture, $"{failed} claim(s) failed the evidence check"));
        }

        Classification = _safety.Count > 0 ? AllocationOutcome.Safety
            : schema.Count > 0 ? AllocationOutcome.Schema
            : evidence.Count > 0 ? AllocationOutcome.Evidence
            : null;
        return [.. _safety, .. schema, .. evidence];
    }
}

/// <summary>Deterministic reading of a validated proposal (m8-protocol §5.3): veto wins, the multiplier is clamped to the band.</summary>
public static class AllocationProposalReader
{
    public const string BandLow = "band-low";

    public const string BandHigh = "band-high";

    public static IReadOnlyList<AllocationItem> Items(JsonNode output, AllocationPacket packet)
    {
        var proposed = new Dictionary<string, (decimal Multiplier, bool Veto)>(StringComparer.Ordinal);
        foreach (var entry in output["entries"]?.AsArray() ?? [])
        {
            var reference = entry!["candidateRef"]!.GetValue<string>();
            proposed.TryAdd(reference, (entry["multiplier"]!.GetValue<decimal>(), entry["veto"]!.GetValue<bool>()));
        }

        var items = new List<AllocationItem>();
        foreach (var (reference, candidate) in packet.Candidates)
        {
            if (!proposed.TryGetValue(reference, out var p))
            {
                items.Add(new AllocationItem(candidate.Sleeve, candidate.Symbol, candidate.Weight, null, 1m, false, null));
                continue;
            }

            var clamp = p.Veto ? null : p.Multiplier < AllocatorBand.Low ? BandLow : p.Multiplier > AllocatorBand.High ? BandHigh : null;
            items.Add(new AllocationItem(candidate.Sleeve, candidate.Symbol, candidate.Weight, p.Multiplier, p.Veto ? 0m : AllocatorBand.Clamp(p.Multiplier), p.Veto, clamp));
        }

        return items.OrderBy(i => i.Sleeve).ThenBy(i => i.Symbol, StringComparer.Ordinal).ToList();
    }

    /// <summary>Overrides for the engine: only vetoes and multipliers other than 1.</summary>
    public static IReadOnlyList<AllocationOverride> Overrides(IEnumerable<AllocationItem> items) =>
        items.Where(i => i.Veto || i.Multiplier != 1m).Select(i => new AllocationOverride(i.Sleeve, i.Symbol, i.Multiplier, i.Veto)).ToList();
}
