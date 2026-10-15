using System.Globalization;
using System.Text.Json;

namespace PortfolioIntelligence.Kernel.Dsl;

// DSL v1 (ADR-0016): a shallow JSON strategy definition built from whitelisted indicators and operators.

public enum DslSource
{
    Self,
    Btc,
}

public sealed record DslIndicator(string Id, string Type, IReadOnlyDictionary<string, decimal> Params, DslSource Source);

public abstract record DslNode;

public sealed record DslCondition(string Op, IReadOnlyList<DslNode> Args) : DslNode;

public abstract record DslOperand : DslNode;

public sealed record DslRef(string Indicator, string Output, int Lag = 0, decimal Mult = 1m) : DslOperand;

public sealed record DslPrice(string Field, DslSource Source = DslSource.Self, int Lag = 0, decimal Mult = 1m) : DslOperand;

public sealed record DslConst(decimal Value) : DslOperand;

public sealed record DslStop(string Type, decimal? Pct, DslRef? Ref, decimal? Multiple, bool Trailing);

public sealed record DslDefinition(
    string Name,
    string Interval,
    IReadOnlyList<DslIndicator> Indicators,
    DslCondition Entry,
    DslCondition Exit,
    DslStop? Stop)
{
    public const string Version = "1";
}

public sealed class DslException(IReadOnlyList<string> errors) : Exception("Invalid DSL definition: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Strict JSON parser for DSL v1: unknown properties and wrong types are rejected.</summary>
public static class DslParser
{
    public static DslDefinition Parse(string json)
    {
        var errors = new List<string>();
        DslDefinition? definition = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            definition = ParseRoot(document.RootElement, errors);
        }
        catch (JsonException e)
        {
            errors.Add("malformed JSON: " + e.Message);
        }

        if (errors.Count > 0 || definition is null)
        {
            throw new DslException(errors.Count > 0 ? errors : ["empty definition"]);
        }

        return DslValidator.Validate(definition);
    }

    private static DslDefinition? ParseRoot(JsonElement root, List<string> errors)
    {
        if (!Expect(root, JsonValueKind.Object, "$", errors) ||
            !Properties(root, "$", errors, ["dsl", "name", "interval", "indicators", "entry", "exit"], ["stop"]))
        {
            return null;
        }

        var dsl = String(root.GetProperty("dsl"), "$.dsl", errors);
        if (dsl is not null && dsl != DslDefinition.Version)
        {
            errors.Add($"$.dsl: unsupported version '{dsl}'");
        }

        var name = String(root.GetProperty("name"), "$.name", errors);
        var interval = String(root.GetProperty("interval"), "$.interval", errors);
        var indicators = new List<DslIndicator>();
        var list = root.GetProperty("indicators");
        if (Expect(list, JsonValueKind.Array, "$.indicators", errors))
        {
            var i = 0;
            foreach (var item in list.EnumerateArray())
            {
                if (ParseIndicator(item, $"$.indicators[{i++}]", errors) is { } indicator)
                {
                    indicators.Add(indicator);
                }
            }
        }

        var entry = ParseNode(root.GetProperty("entry"), "$.entry", errors) as DslCondition;
        var exit = ParseNode(root.GetProperty("exit"), "$.exit", errors) as DslCondition;
        if (entry is null || exit is null)
        {
            errors.Add("$.entry and $.exit must be conditions");
        }

        DslStop? stop = null;
        if (root.TryGetProperty("stop", out var stopElement))
        {
            stop = ParseStop(stopElement, "$.stop", errors);
        }

        return errors.Count == 0 && name is not null && interval is not null && entry is not null && exit is not null
            ? new DslDefinition(name, interval, indicators, entry, exit, stop)
            : null;
    }

    private static DslIndicator? ParseIndicator(JsonElement element, string path, List<string> errors)
    {
        if (!Expect(element, JsonValueKind.Object, path, errors) ||
            !Properties(element, path, errors, ["id", "type", "params"], ["source"]))
        {
            return null;
        }

        var id = String(element.GetProperty("id"), path + ".id", errors);
        var type = String(element.GetProperty("type"), path + ".type", errors);
        var parameters = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        var paramsElement = element.GetProperty("params");
        if (Expect(paramsElement, JsonValueKind.Object, path + ".params", errors))
        {
            foreach (var p in paramsElement.EnumerateObject())
            {
                if (Number(p.Value, $"{path}.params.{p.Name}", errors) is { } value)
                {
                    parameters[p.Name] = value;
                }
            }
        }

        var source = element.TryGetProperty("source", out var s) ? Source(s, path + ".source", errors) : DslSource.Self;
        return id is null || type is null || source is null ? null : new DslIndicator(id, type, parameters, source.Value);
    }

    private static DslNode? ParseNode(JsonElement element, string path, List<string> errors)
    {
        if (!Expect(element, JsonValueKind.Object, path, errors))
        {
            return null;
        }

        if (element.TryGetProperty("op", out _))
        {
            if (!Properties(element, path, errors, ["op", "args"], []))
            {
                return null;
            }

            var op = String(element.GetProperty("op"), path + ".op", errors);
            var argsElement = element.GetProperty("args");
            var args = new List<DslNode>();
            if (Expect(argsElement, JsonValueKind.Array, path + ".args", errors))
            {
                var i = 0;
                foreach (var item in argsElement.EnumerateArray())
                {
                    if (ParseNode(item, $"{path}.args[{i++}]", errors) is { } node)
                    {
                        args.Add(node);
                    }
                }
            }

            return op is null ? null : new DslCondition(op, args);
        }

        if (element.TryGetProperty("ref", out _))
        {
            if (!Properties(element, path, errors, ["ref"], ["lag", "mult"]))
            {
                return null;
            }

            return ParseRef(element, path, errors);
        }

        if (element.TryGetProperty("price", out _))
        {
            if (!Properties(element, path, errors, ["price"], ["source", "lag", "mult"]))
            {
                return null;
            }

            var field = String(element.GetProperty("price"), path + ".price", errors);
            var source = element.TryGetProperty("source", out var s) ? Source(s, path + ".source", errors) : DslSource.Self;
            var lag = element.TryGetProperty("lag", out var l) ? Integer(l, path + ".lag", errors) : 0;
            var mult = element.TryGetProperty("mult", out var m) ? Number(m, path + ".mult", errors) : 1m;
            return field is null || source is null || lag is null || mult is null ? null : new DslPrice(field, source.Value, lag.Value, mult.Value);
        }

        if (element.TryGetProperty("const", out _))
        {
            if (!Properties(element, path, errors, ["const"], []))
            {
                return null;
            }

            return Number(element.GetProperty("const"), path + ".const", errors) is { } value ? new DslConst(value) : null;
        }

        errors.Add($"{path}: expected a condition ('op') or an operand ('ref', 'price', 'const')");
        return null;
    }

    private static DslRef? ParseRef(JsonElement element, string path, List<string> errors)
    {
        var reference = String(element.GetProperty("ref"), path + ".ref", errors);
        var lag = element.TryGetProperty("lag", out var l) ? Integer(l, path + ".lag", errors) : 0;
        var mult = element.TryGetProperty("mult", out var m) ? Number(m, path + ".mult", errors) : 1m;
        if (reference is null || lag is null || mult is null)
        {
            return null;
        }

        var parts = reference.Split('.');
        if (parts.Length != 2)
        {
            errors.Add($"{path}.ref: expected '<id>.<output>'");
            return null;
        }

        return new DslRef(parts[0], parts[1], lag.Value, mult.Value);
    }

    private static DslStop? ParseStop(JsonElement element, string path, List<string> errors)
    {
        if (!Expect(element, JsonValueKind.Object, path, errors) ||
            !Properties(element, path, errors, ["type"], ["pct", "ref", "multiple", "trailing"]))
        {
            return null;
        }

        var type = String(element.GetProperty("type"), path + ".type", errors);
        var pct = element.TryGetProperty("pct", out var p) ? Number(p, path + ".pct", errors) : null;
        var multiple = element.TryGetProperty("multiple", out var m) ? Number(m, path + ".multiple", errors) : null;
        var trailing = false;
        if (element.TryGetProperty("trailing", out var t))
        {
            if (t.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                trailing = t.GetBoolean();
            }
            else
            {
                errors.Add(path + ".trailing: expected a boolean");
            }
        }

        var reference = element.TryGetProperty("ref", out _) ? ParseRef(element, path, errors) : null;
        return type is null ? null : new DslStop(type, pct, reference, multiple, trailing);
    }

    private static bool Properties(JsonElement element, string path, List<string> errors, string[] required, string[] optional)
    {
        var ok = true;
        foreach (var name in required)
        {
            if (!element.TryGetProperty(name, out _))
            {
                errors.Add($"{path}: missing '{name}'");
                ok = false;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name) && !optional.Contains(property.Name))
            {
                errors.Add($"{path}: unknown property '{property.Name}'");
                ok = false;
            }
        }

        return ok;
    }

    private static bool Expect(JsonElement element, JsonValueKind kind, string path, List<string> errors)
    {
        if (element.ValueKind == kind)
        {
            return true;
        }

        errors.Add($"{path}: expected {kind.ToString().ToLowerInvariant()}");
        return false;
    }

    private static string? String(JsonElement element, string path, List<string> errors) =>
        Expect(element, JsonValueKind.String, path, errors) ? element.GetString() : null;

    private static decimal? Number(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value))
        {
            return value;
        }

        errors.Add($"{path}: expected a number");
        return null;
    }

    private static int? Integer(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        errors.Add($"{path}: expected an integer");
        return null;
    }

    private static DslSource? Source(JsonElement element, string path, List<string> errors)
    {
        switch (String(element, path, errors))
        {
            case "self":
                return DslSource.Self;
            case "btc":
                return DslSource.Btc;
            case { } other:
                errors.Add($"{path}: unknown source '{other}'");
                return null;
            default:
                return null;
        }
    }

    internal static string FormatNumber(decimal value) =>
        (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}
