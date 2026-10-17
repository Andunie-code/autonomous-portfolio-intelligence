using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Ai;

/// <summary>
/// A neutral, shallow JSON schema (architecture §3.1): only the JSON Schema subset Gemini documents (S-G7) and a
/// depth limit. The same schema validates model output, so an off-schema output is never accepted (testing §6.1).
/// </summary>
public sealed class NeutralSchema
{
    public const int MaxDepth = 6;

    private static readonly HashSet<string> Types = ["object", "array", "string", "number", "integer", "boolean"];

    private static readonly Dictionary<string, HashSet<string>> Keywords = new(StringComparer.Ordinal)
    {
        ["object"] = ["type", "description", "properties", "required", "additionalProperties"],
        ["array"] = ["type", "description", "items", "minItems", "maxItems"],
        ["string"] = ["type", "description", "enum", "format"],
        ["number"] = ["type", "description", "enum", "minimum", "maximum"],
        ["integer"] = ["type", "description", "enum", "minimum", "maximum"],
        ["boolean"] = ["type", "description"],
    };

    private NeutralSchema(JsonObject root, string json)
    {
        Root = root;
        Json = json;
        Hash = DslCanonical.Sha256(json);
    }

    public JsonObject Root { get; }

    public string Json { get; }

    /// <summary>SHA-256 of the schema JSON: part of a qualification's identity (a schema change drops the qualification).</summary>
    public string Hash { get; }

    public static NeutralSchema Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("A schema must be a JSON object.", nameof(json));
        var errors = new List<string>();
        Check(root, "$", 1, errors);
        if (errors.Count > 0)
        {
            throw new ArgumentException("Schema outside the neutral subset: " + string.Join("; ", errors), nameof(json));
        }

        return new NeutralSchema(root, root.ToJsonString());
    }

    /// <summary>Errors of <paramref name="value"/> against this schema; empty if it conforms. Unknown properties are errors.</summary>
    public IReadOnlyList<string> Validate(JsonNode? value)
    {
        var errors = new List<string>();
        Conform(Root, value, "$", errors);
        return errors;
    }

    /// <summary>Parses <paramref name="text"/> as JSON and validates it; null with errors if it is not JSON at all.</summary>
    public (JsonNode? Value, IReadOnlyList<string> Errors) ParseAndValidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, ["empty output"]);
        }

        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
            return (node, Validate(node));
        }
        catch (JsonException e)
        {
            return (null, ["output is not JSON: " + e.Message]);
        }
    }

    private static void Check(JsonObject schema, string path, int depth, List<string> errors)
    {
        if (depth > MaxDepth)
        {
            errors.Add($"{path}: deeper than {MaxDepth}");
            return;
        }

        var type = schema["type"]?.GetValueKind() == JsonValueKind.String ? schema["type"]!.GetValue<string>() : null;
        if (type is null || !Types.Contains(type))
        {
            errors.Add($"{path}: missing or unsupported type");
            return;
        }

        foreach (var (key, _) in schema)
        {
            if (!Keywords[type].Contains(key))
            {
                errors.Add($"{path}: keyword '{key}' not in the neutral subset for {type}");
            }
        }

        if (type == "object")
        {
            if (schema["properties"] is not JsonObject properties || properties.Count == 0)
            {
                errors.Add($"{path}: an object needs properties");
                return;
            }

            if (schema["additionalProperties"] is { } additional && (additional.GetValueKind() != JsonValueKind.False))
            {
                errors.Add($"{path}: additionalProperties may only be false");
            }

            foreach (var (name, child) in properties)
            {
                if (child is JsonObject childSchema)
                {
                    Check(childSchema, $"{path}.{name}", depth + 1, errors);
                }
                else
                {
                    errors.Add($"{path}.{name}: not a schema");
                }
            }

            foreach (var required in (schema["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>()))
            {
                if (required is null || !properties.ContainsKey(required))
                {
                    errors.Add($"{path}: required '{required}' is not a property");
                }
            }
        }
        else if (type == "array")
        {
            if (schema["items"] is JsonObject items)
            {
                Check(items, path + "[]", depth + 1, errors);
            }
            else
            {
                errors.Add($"{path}: an array needs items");
            }
        }
    }

    private static void Conform(JsonObject schema, JsonNode? value, string path, List<string> errors)
    {
        var type = schema["type"]!.GetValue<string>();
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        switch (type)
        {
            case "object" when value is JsonObject o:
                var properties = (JsonObject)schema["properties"]!;
                foreach (var required in (schema["required"] as JsonArray ?? []).Select(r => r!.GetValue<string>()))
                {
                    if (!o.ContainsKey(required))
                    {
                        errors.Add($"{path}: missing '{required}'");
                    }
                }

                foreach (var (name, child) in o)
                {
                    if (properties[name] is JsonObject childSchema)
                    {
                        Conform(childSchema, child, $"{path}.{name}", errors);
                    }
                    else
                    {
                        errors.Add($"{path}: unknown property '{name}'");
                    }
                }

                break;
            case "array" when value is JsonArray a:
                if (schema["minItems"] is { } min && a.Count < min.GetValue<int>())
                {
                    errors.Add($"{path}: fewer than {min} items");
                }

                if (schema["maxItems"] is { } max && a.Count > max.GetValue<int>())
                {
                    errors.Add($"{path}: more than {max} items");
                }

                for (var i = 0; i < a.Count; i++)
                {
                    Conform((JsonObject)schema["items"]!, a[i], $"{path}[{i}]", errors);
                }

                break;
            case "string" when kind == JsonValueKind.String:
                Enum(schema, value!, path, errors);
                break;
            case "integer" when kind == JsonValueKind.Number && Number(value!) % 1 == 0:
            case "number" when kind == JsonValueKind.Number:
                var number = Number(value!);
                if (schema["minimum"] is { } minimum && number < Number(minimum))
                {
                    errors.Add($"{path}: below minimum");
                }

                if (schema["maximum"] is { } maximum && number > Number(maximum))
                {
                    errors.Add($"{path}: above maximum");
                }

                Enum(schema, value!, path, errors);
                break;
            case "boolean" when kind is JsonValueKind.True or JsonValueKind.False:
                break;
            default:
                errors.Add($"{path}: expected {type}");
                break;
        }
    }

    private static decimal Number(JsonNode node) => decimal.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void Enum(JsonObject schema, JsonNode value, string path, List<string> errors)
    {
        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: value not in enum"));
        }
    }
}

public sealed record NeutralTool(string Name, string Description, NeutralSchema Parameters);

/// <summary>The tools a task may call. A call to any other name is a safety violation (testing §6.1).</summary>
public sealed class NeutralToolSet(IEnumerable<NeutralTool> tools)
{
    public IReadOnlyList<NeutralTool> Tools { get; } = tools.ToList();

    public bool Contains(string name) => Tools.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}
