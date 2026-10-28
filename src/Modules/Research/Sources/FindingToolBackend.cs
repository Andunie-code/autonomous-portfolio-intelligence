using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PortfolioIntelligence.Researcher;
using PortfolioIntelligence.SourceGateway;

namespace PortfolioIntelligence.Research.Sources;

/// <summary>
/// The Research module's implementation of the Researcher's M7 tools (m7-protocol §8.1). Search is a deterministic term
/// match over verified findings; a request is only recorded. While <see cref="WebResearchGate"/> is closed nothing is
/// returned and a request is recorded as refused.
/// </summary>
public sealed partial class FindingToolBackend(WebResearchGate gate, ISourceStore store) : IFindingToolBackend
{
    public async Task<JsonNode> SearchFindingsAsync(string query, string? kind, int limit, CancellationToken ct)
    {
        if (!await gate.IsOpenAsync(ct))
        {
            return new JsonObject { ["findings"] = new JsonArray(), ["note"] = WebResearchGate.ClosedNote };
        }

        var terms = Term().Matches(query).Select(m => m.Value).Take(6).ToList();
        if (terms.Count == 0)
        {
            return new JsonObject { ["error"] = "query needs at least one word" };
        }

        var findings = await store.SearchVerifiedAsync(terms, kind, limit, ct);
        return new JsonObject
        {
            ["findings"] = new JsonArray(findings.Select(f => (JsonNode)new JsonObject
            {
                ["id"] = f.Id, ["kind"] = f.Kind, ["claim"] = f.Claim, ["quote"] = f.Quote, ["source"] = f.Source, ["tier"] = f.Tier.ToString(),
                ["versionDate"] = f.VersionAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["suspicious"] = f.Suspicious,
            }).ToArray()),
        };
    }

    public async Task<JsonNode> RequestSourceResearchAsync(string topic, IReadOnlyList<string> categories, CancellationToken ct)
    {
        var trimmed = topic.Trim();
        if (trimmed.Length is 0 or > 200)
        {
            return new JsonObject { ["error"] = "topic must be 1 to 200 characters" };
        }

        if (!await gate.IsOpenAsync(ct))
        {
            await store.AddResearchRequestAsync(trimmed, categories, "refused-closed", ct);
            return new JsonObject { ["error"] = WebResearchGate.ClosedNote };
        }

        var id = await store.AddResearchRequestAsync(trimmed, categories, "recorded", ct);
        return new JsonObject { ["requestId"] = id, ["status"] = "recorded; processed by the user's sources run" };
    }

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9\-]{1,39}")]
    private static partial Regex Term();
}
