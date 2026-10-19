using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortfolioIntelligence.Kernel.Dsl;

/// <summary>
/// Repairs applied to model-written DSL JSON before the strict parser (ADR-0016 is unchanged: the parser still rejects
/// unused indicators). Removing an indicator that no condition or stop references cannot change behaviour, so the
/// repaired definition is the one the model's conditions describe. Callers record what was removed.
/// </summary>
public static class DslNormalizer
{
    public static (string Json, IReadOnlyList<string> Removed) PruneUnusedIndicators(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return (json, []);
        }

        if (root?["indicators"] is not JsonArray indicators)
        {
            return (json, []);
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in new[] { root["entry"], root["exit"], root["stop"] })
        {
            CollectReferences(part, referenced);
        }

        var removed = new List<string>();
        for (var i = indicators.Count - 1; i >= 0; i--)
        {
            if (indicators[i] is JsonObject indicator && indicator["id"] is JsonValue id && id.TryGetValue<string>(out var name) && !referenced.Contains(name))
            {
                removed.Insert(0, name);
                indicators.RemoveAt(i);
            }
        }

        return removed.Count == 0 ? (json, removed) : (root.ToJsonString(), removed);
    }

    private static void CollectReferences(JsonNode? node, HashSet<string> referenced)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, child) in o)
                {
                    if (key == "ref" && child is JsonValue value && value.TryGetValue<string>(out var reference))
                    {
                        referenced.Add(reference.Split('.', 2)[0]);
                    }
                    else
                    {
                        CollectReferences(child, referenced);
                    }
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    CollectReferences(child, referenced);
                }

                break;
        }
    }
}
