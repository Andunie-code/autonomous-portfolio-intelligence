using System.Security.Cryptography;
using System.Text;

namespace PortfolioIntelligence.Kernel.Dsl;

public enum CanonicalMode
{
    /// <summary>Everything that affects behaviour: the definition hash (one trial per distinct value).</summary>
    Definition,

    /// <summary>Parameters, constants, multipliers and the interval removed: the family structure hash.</summary>
    Structure,
}

/// <summary>
/// Normalisation of m3-protocol §8: canonical JSON independent of field order and whitespace; lt/lte turned into
/// gt/gte; not(not x) = x; and/or arguments sorted with duplicates removed and single arguments unwrapped.
/// Indicator ids do not appear: every reference is replaced by its indicator's descriptor, so renaming or
/// reordering indicators never changes a hash.
/// </summary>
public static class DslCanonical
{
    public static string Canonicalize(DslDefinition definition, CanonicalMode mode)
    {
        var byId = definition.Indicators.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var s = new StringBuilder("{");
        s.Append("\"dsl\":\"").Append(DslDefinition.Version).Append("\",");
        s.Append("\"entry\":").Append(Node(definition.Entry, byId, mode)).Append(',');
        s.Append("\"exit\":").Append(Node(definition.Exit, byId, mode));
        if (mode == CanonicalMode.Definition)
        {
            s.Append(",\"interval\":\"").Append(definition.Interval).Append('"');
        }

        if (definition.Stop is { } stop)
        {
            s.Append(",\"stop\":").Append(Stop(stop, byId, mode));
        }

        return s.Append('}').ToString();
    }

    public static string DefinitionHash(DslDefinition definition) => Sha256(Canonicalize(definition, CanonicalMode.Definition));

    public static string StructureHash(DslDefinition definition) => Sha256(Canonicalize(definition, CanonicalMode.Structure));

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Node(DslNode node, Dictionary<string, DslIndicator> byId, CanonicalMode mode)
    {
        switch (node)
        {
            case DslCondition { Op: "lt" } c:
                return Node(c with { Op = "gt", Args = [c.Args[1], c.Args[0]] }, byId, mode);
            case DslCondition { Op: "lte" } c:
                return Node(c with { Op = "gte", Args = [c.Args[1], c.Args[0]] }, byId, mode);
            case DslCondition { Op: "not", Args: [DslCondition { Op: "not", Args: [var inner] }] }:
                return Node(inner, byId, mode);
            case DslCondition { Op: "and" or "or" } c:
                var args = c.Args.Select(a => Node(a, byId, mode)).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
                return args.Count == 1 ? args[0] : Condition(c.Op, args);
            case DslCondition c:
                return Condition(c.Op, c.Args.Select(a => Node(a, byId, mode)));
            case DslRef r:
                return Ref(r, byId, mode);
            case DslPrice p:
                return "{" + (mode == CanonicalMode.Definition ? $"\"lag\":{p.Lag},\"mult\":{Number(p.Mult)}," : $"\"lag\":{p.Lag},")
                    + $"\"price\":\"{p.Field}\",\"src\":\"{Source(p.Source)}\"}}";
            case DslConst k:
                return mode == CanonicalMode.Definition ? $"{{\"const\":{Number(k.Value)}}}" : "{\"const\":\"*\"}";
            default:
                throw new ArgumentOutOfRangeException(nameof(node));
        }
    }

    private static string Condition(string op, IEnumerable<string> args) => $"{{\"args\":[{string.Join(',', args)}],\"op\":\"{op}\"}}";

    private static string Ref(DslRef r, Dictionary<string, DslIndicator> byId, CanonicalMode mode) =>
        "{\"ind\":" + Descriptor(byId[r.Indicator], mode) + $",\"lag\":{r.Lag},"
        + (mode == CanonicalMode.Definition ? $"\"mult\":{Number(r.Mult)}," : "")
        + $"\"out\":\"{r.Output}\"}}";

    private static string Descriptor(DslIndicator indicator, CanonicalMode mode)
    {
        var s = new StringBuilder("{");
        if (mode == CanonicalMode.Definition)
        {
            s.Append("\"params\":{")
                .AppendJoin(',', indicator.Params.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"\"{p.Key}\":{Number(p.Value)}"))
                .Append("},");
        }

        return s.Append($"\"src\":\"{Source(indicator.Source)}\",\"type\":\"{indicator.Type}\"}}").ToString();
    }

    private static string Stop(DslStop stop, Dictionary<string, DslIndicator> byId, CanonicalMode mode)
    {
        var parts = new List<string>();
        if (mode == CanonicalMode.Definition && stop.Multiple is { } multiple)
        {
            parts.Add($"\"multiple\":{Number(multiple)}");
        }

        if (mode == CanonicalMode.Definition && stop.Pct is { } pct)
        {
            parts.Add($"\"pct\":{Number(pct)}");
        }

        if (stop.Ref is { } r)
        {
            parts.Add("\"ref\":" + Ref(r, byId, mode));
        }

        parts.Add($"\"trailing\":{(stop.Trailing ? "true" : "false")}");
        parts.Add($"\"type\":\"{stop.Type}\"");
        return "{" + string.Join(',', parts) + "}";
    }

    private static string Source(DslSource source) => source == DslSource.Btc ? "btc" : "self";

    private static string Number(decimal value) => DslParser.FormatNumber(value);
}
