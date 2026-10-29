using System.Globalization;
using System.Text;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Allocator;

/// <summary>
/// The read-only input packet of one allocator decision (m8-protocol §5.1). Every item gets an opaque reference: the
/// first 16 hex digits of the SHA-256 of its canonical text. Only these references reach <c>ai.decision_traces</c>
/// (research_app can read the ai schema), never a symbol, strategy, weight or price.
/// </summary>
public sealed class AllocationPacket : IEvidenceResolver
{
    private readonly Dictionary<string, EvidenceItem> _evidence = new(StringComparer.Ordinal);

    private AllocationPacket(string text, IReadOnlyDictionary<string, EntryCandidate> candidates, IReadOnlySet<string> holdings, IReadOnlyList<string> evidenceIds)
    {
        Text = text;
        Candidates = candidates;
        Holdings = holdings;
        EvidenceIds = evidenceIds;
    }

    /// <summary>The prompt text sent to the model.</summary>
    public string Text { get; }

    public IReadOnlyDictionary<string, EntryCandidate> Candidates { get; }

    /// <summary>References of holdings: proposing anything for them is an exit-interference attempt.</summary>
    public IReadOnlySet<string> Holdings { get; }

    public IReadOnlyList<string> EvidenceIds { get; }

    public static AllocationPacket Build(AllocationRequest request)
    {
        var at = request.DecisionTime;
        var candidates = new Dictionary<string, EntryCandidate>(StringComparer.Ordinal);
        var holdings = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new List<EvidenceItem>();
        var s = new StringBuilder();
        string Name(int sleeve) => Neutralize(sleeve < request.Strategies.Count ? request.Strategies[sleeve] : $"sleeve-{sleeve}");
        string Ref(string canonical) => DslCanonical.Sha256(request.RunKey + "\n" + at.ToString("O", CultureInfo.InvariantCulture) + "\n" + canonical)[..16];

        s.Append(CultureInfo.InvariantCulture, $"{AllocationPrompt.Begin}{at:yyyy-MM-ddTHH:mm}Z>>>\n");
        s.Append("entry candidates (you may adjust these only):\n");
        foreach (var c in request.Candidates)
        {
            var reference = Ref($"candidate|{c.Sleeve}|{c.Symbol}");
            candidates[reference] = c;
            evidence.Add(new EvidenceItem(reference, at, c.Weight, new Dictionary<string, decimal> { ["defaultWeight"] = c.Weight, ["volatility30d"] = c.Volatility }));
            s.Append(CultureInfo.InvariantCulture,
                $"- ref {reference} | strategy {Name(c.Sleeve)} | symbol {Neutralize(c.Symbol)} | defaultWeight {c.Weight:0.######} | volatility30d {c.Volatility:0.######}\n");
        }

        s.Append("holdings (context only; never reference them in entries):\n");
        foreach (var h in request.Holdings)
        {
            var reference = Ref($"holding|{h.Sleeve}|{h.Symbol}");
            holdings.Add(reference);
            evidence.Add(new EvidenceItem(reference, at, h.Weight, new Dictionary<string, decimal> { ["weight"] = h.Weight }));
            s.Append(CultureInfo.InvariantCulture,
                $"- ref {reference} | strategy {Name(h.Sleeve)} | symbol {Neutralize(h.Symbol)} | weight {h.Weight:0.######} | pendingExit {(h.PendingExit ? "true" : "false")}\n");
        }

        var l = request.Limits;
        var limits = Ref("limits");
        evidence.Add(new EvidenceItem(limits, at, null, new Dictionary<string, decimal>
        {
            ["exposureShare"] = l.ExposureShare, ["experimentalShare"] = l.ExperimentalShare, ["openPositions"] = l.OpenPositions,
            ["maxPositions"] = l.MaxPositions, ["maxAssetWeight"] = l.MaxAssetWeight,
        }));
        s.Append(CultureInfo.InvariantCulture,
            $"limits: ref {limits} | exposureShare {l.ExposureShare:0.######} | experimentalShare {l.ExperimentalShare:0.######} | openPositions {l.OpenPositions} | maxPositions {l.MaxPositions} | maxAssetWeight {l.MaxAssetWeight:0.######}\n");
        if (request.MarketRegime is { } regime)
        {
            var reference = Ref("regime");
            evidence.Add(new EvidenceItem(reference, at, null));
            s.Append(CultureInfo.InvariantCulture, $"market regime: ref {reference} | {Neutralize(regime)}\n");
        }

        s.Append(AllocationPrompt.End);
        var text = s.ToString();
        if (text.Length > AllocationPrompt.MaxInputCharacters)
        {
            throw new InvalidOperationException("The allocation packet exceeds the input cap.");
        }

        var packet = new AllocationPacket(text, candidates, holdings, evidence.Select(e => e.Id).ToList());
        foreach (var item in evidence)
        {
            packet._evidence[item.Id] = item;
        }

        return packet;
    }

    public bool Contains(string reference) => _evidence.ContainsKey(reference);

    public Task<EvidenceItem?> ResolveAsync(string evidenceRef, CancellationToken ct) => Task.FromResult(_evidence.GetValueOrDefault(evidenceRef));

    private static string Neutralize(string text) =>
        new(text.Replace("<<<", "(((", StringComparison.Ordinal).Replace(">>>", ")))", StringComparison.Ordinal).Where(ch => !char.IsControl(ch) && ch != '|').ToArray());
}
