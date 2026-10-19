using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Kernel.Backtest;

public sealed record FrozenMember(int Index, string Name, string DefinitionJson, string DefinitionHash);

/// <summary>The subset of a cohort manifest that operations need (ADR-0018 §3); no protocol or evaluation fields.</summary>
public sealed record OperationalManifest(
    IReadOnlyList<FrozenMember> Members,
    IReadOnlyList<(string Symbol, PairCategory Category)> Pairs,
    CandleInterval Interval,
    RiskSettings Risk,
    decimal InitialCapital,
    string Hash)
{
    public IReadOnlySet<string> ExperimentalSymbols => Pairs.Where(p => p.Category != PairCategory.Core).Select(p => p.Symbol).ToHashSet();
}

/// <summary>
/// Reads the operational part of a cohort manifest (docs/cohorts/*.json). The manifest hash is SHA-256 of the
/// canonical JSON (object properties sorted ordinally, no whitespace, numbers as written): the same rule as the
/// evaluator's manifest parser, which a test pins. Trading may not reference the evaluator (architecture rule).
/// </summary>
public static class CohortManifestReader
{
    public static OperationalManifest Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("manifest: not a JSON object");
        var members = (root["strategies"] as JsonArray ?? throw new FormatException("manifest: strategies"))
            .Select((s, i) =>
            {
                var definition = s?["definition"] ?? throw new FormatException("manifest: definition");
                var definitionJson = definition.ToJsonString();
                var declared = s["definitionHash"]?.GetValue<string>() ?? throw new FormatException("manifest: definitionHash");
                var computed = DslStrategy.FromJson(definitionJson).DefinitionHash;
                if (!string.Equals(declared, computed, StringComparison.Ordinal))
                {
                    throw new FormatException($"manifest: definition hash {declared} does not match the definition ({computed})");
                }

                return new FrozenMember(i + 1, definition["name"]!.GetValue<string>(), definitionJson, declared);
            })
            .ToList();
        var pairs = (root["pairs"] as JsonArray ?? throw new FormatException("manifest: pairs"))
            .Select(p => (p!["symbol"]!.GetValue<string>(), Category(p["category"]!.GetValue<string>())))
            .ToList();
        var risk = root["risk"] ?? throw new FormatException("manifest: risk");
        return new OperationalManifest(
            members,
            pairs,
            CandleInterval.FromCode(root["interval"]!.GetValue<string>()),
            new RiskSettings(
                risk["maxOpenPositions"]!.GetValue<int>(),
                risk["maxAssetWeight"]!.GetValue<decimal>(),
                risk["maxTotalExposure"]!.GetValue<decimal>(),
                risk["dailyLossLimit"]!.GetValue<decimal>(),
                risk["maxDrawdownHalt"]!.GetValue<decimal>(),
                true,
                risk["maxPositionLoss"]!.GetValue<decimal>()),
            root["initialCapital"]!.GetValue<decimal>(),
            DslCanonical.Sha256(Canonical(root)));
    }

    private static PairCategory Category(string code) => code switch
    {
        "core" => PairCategory.Core,
        "E1" or "e1" => PairCategory.E1,
        "E2" or "e2" => PairCategory.E2,
        _ => throw new FormatException($"manifest: unknown pair category '{code}'"),
    };

    /// <summary>Canonical JSON: object properties sorted ordinally, no whitespace; numbers as written.</summary>
    public static string Canonical(JsonNode? node)
    {
        var s = new StringBuilder();
        Write(node, s);
        return s.ToString();

        static void Write(JsonNode? n, StringBuilder s)
        {
            switch (n)
            {
                case null:
                    s.Append("null");
                    break;
                case JsonObject o:
                    s.Append('{');
                    var first = true;
                    foreach (var p in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!first)
                        {
                            s.Append(',');
                        }

                        first = false;
                        s.Append(JsonSerializer.Serialize(p.Key)).Append(':');
                        Write(p.Value, s);
                    }

                    s.Append('}');
                    break;
                case JsonArray a:
                    s.Append('[');
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (i > 0)
                        {
                            s.Append(',');
                        }

                        Write(a[i], s);
                    }

                    s.Append(']');
                    break;
                default:
                    s.Append(n.ToJsonString());
                    break;
            }
        }
    }
}
