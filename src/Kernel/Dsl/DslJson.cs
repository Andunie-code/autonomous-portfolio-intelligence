using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortfolioIntelligence.Kernel.Dsl;

/// <summary>
/// Serialises a definition back to parseable DSL v1 JSON (ADR-0016), so grid points and model output can be stored
/// and replayed. Unlike the canonical form it keeps names and indicator ids; <c>Parse(Serialize(d))</c> has the
/// definition hash of <c>d</c>. Defaults (lag 0, mult 1, source self) are omitted.
/// </summary>
public static class DslJson
{
    public static string Serialize(DslDefinition definition) => ToNode(definition).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    public static JsonObject ToNode(DslDefinition definition)
    {
        var root = new JsonObject
        {
            ["dsl"] = DslDefinition.Version,
            ["name"] = definition.Name,
            ["interval"] = definition.Interval,
            ["indicators"] = new JsonArray(definition.Indicators.Select(Indicator).ToArray()),
            ["entry"] = Node(definition.Entry),
            ["exit"] = Node(definition.Exit),
        };
        if (definition.Stop is { } stop)
        {
            root["stop"] = Stop(stop);
        }

        return root;
    }

    private static JsonNode Indicator(DslIndicator indicator)
    {
        var parameters = new JsonObject();
        foreach (var (name, value) in indicator.Params.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            parameters[name] = Number(value);
        }

        var node = new JsonObject { ["id"] = indicator.Id, ["type"] = indicator.Type, ["params"] = parameters };
        if (indicator.Source == DslSource.Btc)
        {
            node["source"] = "btc";
        }

        return node;
    }

    private static JsonNode Node(DslNode node) => node switch
    {
        DslCondition c => new JsonObject { ["op"] = c.Op, ["args"] = new JsonArray(c.Args.Select(Node).ToArray()) },
        DslRef r => Ref(r),
        DslPrice p => Price(p),
        DslConst k => new JsonObject { ["const"] = Number(k.Value) },
        _ => throw new ArgumentOutOfRangeException(nameof(node), node.GetType().Name, "unknown DSL node"),
    };

    private static JsonObject Ref(DslRef r)
    {
        var node = new JsonObject { ["ref"] = $"{r.Indicator}.{r.Output}" };
        if (r.Lag != 0)
        {
            node["lag"] = r.Lag;
        }

        if (r.Mult != 1m)
        {
            node["mult"] = Number(r.Mult);
        }

        return node;
    }

    private static JsonObject Price(DslPrice p)
    {
        var node = new JsonObject { ["price"] = p.Field };
        if (p.Source == DslSource.Btc)
        {
            node["source"] = "btc";
        }

        if (p.Lag != 0)
        {
            node["lag"] = p.Lag;
        }

        if (p.Mult != 1m)
        {
            node["mult"] = Number(p.Mult);
        }

        return node;
    }

    private static JsonObject Stop(DslStop stop)
    {
        var node = new JsonObject { ["type"] = stop.Type };
        if (stop.Pct is { } pct)
        {
            node["pct"] = Number(pct);
        }

        if (stop.Ref is { } reference)
        {
            node["ref"] = $"{reference.Indicator}.{reference.Output}";
        }

        if (stop.Multiple is { } multiple)
        {
            node["multiple"] = Number(multiple);
        }

        if (stop.Type != "percent")
        {
            node["trailing"] = stop.Trailing;
        }

        return node;
    }

    // Decimal values are written in their shortest exact form (2.50 → 2.5) and read back as the same decimal.
    private static JsonNode Number(decimal value) => JsonNode.Parse(value.ToString("0.############################", CultureInfo.InvariantCulture))!;
}
