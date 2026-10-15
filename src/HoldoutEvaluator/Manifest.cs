using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.HoldoutEvaluator;

/// <summary>Blocking protocol values (m3-protocol §4, D26). Null when missing from the manifest.</summary>
public sealed record ProtocolValues(
    double? Alpha,
    double? Power,
    double? DeltaFloor,
    double? DeltaShrink,
    double? DeltaCap,
    string? DeltaMode,
    double? MaxDrawdown,
    int BootstrapReplicates,
    int BootstrapSeed)
{
    public bool Complete => Alpha is > 0 and < 1 && Power is > 0 and < 1 && DeltaFloor is >= 0 && DeltaShrink is >= 0
        && DeltaCap is > 0 && DeltaMode is "a" or "b" && MaxDrawdown is > 0 and < 1;

    /// <summary>δ = min(max(floor, shrink × M_R), cap).</summary>
    public double Delta(double researchMetric) =>
        Math.Min(Math.Max(DeltaFloor ?? 0, (DeltaShrink ?? 0) * researchMetric), DeltaCap ?? double.MaxValue);
}

public sealed record ManifestStrategy(DslStrategy Strategy, double ResearchMetric, double Delta);

public sealed record CohortManifest(
    string ProtocolVersion,
    string ProtocolCommit,
    ProtocolValues Protocol,
    IReadOnlyList<ManifestStrategy> Strategies,
    IReadOnlyList<(string Symbol, string Category)> Pairs,
    string EngineSha,
    string ImageDigest,
    RiskSettings Risk,
    decimal InitialCapital,
    int CohortMonths,
    int PlannedTrades,
    bool Underpowered,
    IReadOnlyList<string> Exemptions,
    string CanonicalJson,
    string Hash)
{
    public bool ProtocolComplete => Protocol.Complete;
}

public sealed class ManifestException(IReadOnlyList<string> errors) : Exception("Invalid manifest: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Manifest schema v1 (architecture §6.3, m3-protocol §9). Strategy definitions are parsed and validated by the
/// kernel DSL; the family is computed from the definition (a client "familyId" is accepted and ignored, H18);
/// δ is recomputed by the server from the research metric. A manifest missing blocking protocol values is
/// registrable but marked "not eligible for validation" (ProtocolComplete = false).
/// </summary>
public static class ManifestParser
{
    public static CohortManifest Parse(string json)
    {
        var errors = new List<string>();
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json) ?? throw new JsonException("empty");
        }
        catch (JsonException e)
        {
            throw new ManifestException(["malformed JSON: " + e.Message]);
        }

        string Text(JsonNode? node, string name) => node?[name]?.GetValueKind() == JsonValueKind.String ? node[name]!.GetValue<string>() : Missing(name);
        string Missing(string name)
        {
            errors.Add($"missing '{name}'");
            return "";
        }

        if (root["manifestVersion"]?.ToString() != "1")
        {
            errors.Add("manifestVersion must be \"1\"");
        }

        foreach (var forbidden in new[] { "registeredAt", "aC", "A_c", "windowEnd" })
        {
            if (root[forbidden] is not null)
            {
                errors.Add($"'{forbidden}' is assigned by the server and must not be sent");
            }
        }

        var protocolNode = root["protocol"];
        var protocol = new ProtocolValues(
            Number(protocolNode, "alpha"), Number(protocolNode, "power"), Number(protocolNode, "deltaFloor"),
            Number(protocolNode, "deltaShrink"), Number(protocolNode, "deltaCap"),
            protocolNode?["deltaMode"]?.GetValueKind() == JsonValueKind.String ? protocolNode["deltaMode"]!.GetValue<string>() : null,
            Number(protocolNode, "maxDrawdown"),
            (int)(Number(protocolNode, "bootstrapReplicates") ?? 10_000),
            (int)(Number(protocolNode, "bootstrapSeed") ?? 0));
        var protocolVersion = Text(protocolNode, "version");
        var protocolCommit = Text(protocolNode, "commit");

        var strategies = new List<ManifestStrategy>();
        if (root["strategies"] is JsonArray list && list.Count is >= 1 and <= 3)
        {
            for (var i = 0; i < list.Count; i++)
            {
                try
                {
                    var definition = list[i]?["definition"] ?? throw new DslException(["missing definition"]);
                    var strategy = DslStrategy.FromJson(definition.ToJsonString());
                    var metric = Number(list[i], "researchMetric") ?? double.NaN;
                    if (double.IsNaN(metric))
                    {
                        errors.Add($"strategies[{i}]: missing researchMetric");
                    }

                    strategies.Add(new ManifestStrategy(strategy, metric, protocol.Delta(metric)));
                }
                catch (DslException e)
                {
                    errors.AddRange(e.Errors.Select(m => $"strategies[{i}]: {m}"));
                }
            }
        }
        else
        {
            errors.Add("strategies: 1–3 required");
        }

        var pairs = new List<(string, string)>();
        if (root["pairs"] is JsonArray pairList && pairList.Count > 0)
        {
            foreach (var pair in pairList)
            {
                var category = pair?["category"]?.ToString();
                if (category is not ("core" or "e1" or "e2"))
                {
                    errors.Add("pairs: category must be core, e1 or e2");
                }

                pairs.Add((pair?["symbol"]?.ToString() ?? "", category ?? ""));
            }
        }
        else
        {
            errors.Add("pairs: at least one pair required");
        }

        if (root["interval"]?.ToString() != "4h")
        {
            errors.Add("interval: only 4h");
        }

        foreach (var required in new[] { "dataSource", "checksumPolicy", "costModel", "allocation", "dataQuality" })
        {
            Text(root, required);
        }

        var engineSha = Text(root, "engineSha");
        var imageDigest = Text(root, "imageDigest");
        var riskNode = root["risk"];
        var risk = new RiskSettings(
            (int)(Number(riskNode, "maxOpenPositions") ?? Fail("risk.maxOpenPositions")),
            (decimal)(Number(riskNode, "maxAssetWeight") ?? Fail("risk.maxAssetWeight")),
            (decimal)(Number(riskNode, "maxTotalExposure") ?? Fail("risk.maxTotalExposure")),
            (decimal)(Number(riskNode, "dailyLossLimit") ?? Fail("risk.dailyLossLimit")),
            (decimal)(Number(riskNode, "maxDrawdownHalt") ?? Fail("risk.maxDrawdownHalt")),
            DrawdownHaltEnabled: true,
            (decimal)(Number(riskNode, "maxPositionLoss") ?? Fail("risk.maxPositionLoss")));
        var capital = (decimal)(Number(root, "initialCapital") ?? Fail("initialCapital"));
        var power = root["power"];
        var months = (int)(Number(power, "months") ?? Fail("power.months"));
        if (months is < 6 or > 12)
        {
            errors.Add("power.months must be within [6, 12]");
        }

        var planned = (int)(Number(power, "plannedTrades") ?? Fail("power.plannedTrades"));
        var underpowered = power?["underpowered"]?.GetValueKind() is JsonValueKind.True;
        var exemptions = root["exemptions"] is JsonArray ex ? ex.Select(e => e?.ToString() ?? "").ToList() : [];

        if (errors.Count > 0)
        {
            throw new ManifestException(errors);
        }

        var canonical = Canonical(root);
        return new CohortManifest(protocolVersion, protocolCommit, protocol, strategies, pairs, engineSha, imageDigest, risk, capital,
            months, planned, underpowered, exemptions, canonical, DslCanonical.Sha256(canonical));

        double Fail(string name)
        {
            errors.Add($"missing '{name}'");
            return 0;
        }
    }

    private static double? Number(JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

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

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
