using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Researcher;

/// <summary>The Researcher's complete tool list (architecture §4). M7 adds SearchFindings and RequestSourceResearch.</summary>
public static class ResearcherToolNames
{
    public const string ListIndicators = "ListIndicators";
    public const string SearchExperiments = "SearchExperiments";
    public const string GetDatasetCatalog = "GetDatasetCatalog";
    public const string ValidateStrategyDefinition = "ValidateStrategyDefinition";
    public const string SubmitExperiment = "SubmitExperiment";
    public const string GetExperimentResult = "GetExperimentResult";

    public static IReadOnlyList<string> All { get; } =
        [ListIndicators, SearchExperiments, GetDatasetCatalog, ValidateStrategyDefinition, SubmitExperiment, GetExperimentResult];
}

public sealed record ExperimentSubmission(string HypothesisKey, string DefinitionJson, string Origin);

/// <summary>
/// The Researcher's only window to the outside (ADR-0017 §1): research-window experiments and the dataset catalog,
/// through the Research module's guards (ExperimentRequestGuard, trial counter, budgets). No holdout, evaluator,
/// SQL, shell, raw web, orders or risk configuration exist behind this port.
/// </summary>
public interface IResearchToolBackend
{
    Task<JsonNode> SearchExperimentsAsync(string query, int limit, CancellationToken ct);

    /// <summary>Datasets before the research cutoff only (H7); the holdout window is never listed.</summary>
    Task<JsonNode> GetDatasetCatalogAsync(CancellationToken ct);

    Task<JsonNode> SubmitExperimentAsync(ExperimentSubmission submission, CancellationToken ct);

    Task<JsonNode> GetExperimentResultAsync(long experimentId, CancellationToken ct);
}

/// <summary>Records safety violations (testing §6.1: tolerance 0).</summary>
public interface ISafetyLog
{
    Task RecordAsync(string violation, CancellationToken ct);
}

public sealed class InMemorySafetyLog : ISafetyLog
{
    private readonly List<string> _violations = [];

    public IReadOnlyList<string> Violations => _violations.ToList();

    public Task RecordAsync(string violation, CancellationToken ct)
    {
        _violations.Add(violation);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Researcher's toolbox: exactly the six tools, with neutral schemas, dispatched by name. It is the same path for
/// the LLM (as the router's tool executor) and for the LLM-less loop. A call to any other name is refused and recorded
/// as a safety violation, even though the router already blocks it (defence in depth).
/// </summary>
public sealed class ResearcherToolbox(IResearchToolBackend backend, ISafetyLog safety) : IToolExecutor
{
    public const int MaxSearchResults = 50;

    public NeutralToolSet Tools { get; } = new([
        new NeutralTool(ResearcherToolNames.ListIndicators, "Lists the whitelisted DSL v1 indicators with parameter ranges and outputs.",
            NeutralSchema.Parse("""{"type":"object","properties":{"filter":{"type":"string","description":"optional indicator type prefix"}},"additionalProperties":false}""")),
        new NeutralTool(ResearcherToolNames.SearchExperiments, "Searches recorded research-window experiments by strategy name.",
            NeutralSchema.Parse("""{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["query"],"additionalProperties":false}""")),
        new NeutralTool(ResearcherToolNames.GetDatasetCatalog, "Lists the research datasets (pairs, intervals, ranges); all end before the research cutoff.",
            NeutralSchema.Parse("""{"type":"object","properties":{"interval":{"type":"string","enum":["1h","4h"]}},"additionalProperties":false}""")),
        new NeutralTool(ResearcherToolNames.ValidateStrategyDefinition, "Validates a DSL v1 strategy definition (JSON text) and returns its hashes or the errors.",
            NeutralSchema.Parse("""{"type":"object","properties":{"definition":{"type":"string"}},"required":["definition"],"additionalProperties":false}""")),
        new NeutralTool(ResearcherToolNames.SubmitExperiment, "Submits a validated definition for a research-window backtest; counts as a trial and is subject to the budgets.",
            NeutralSchema.Parse("""{"type":"object","properties":{"hypothesis":{"type":"string"},"definition":{"type":"string"},"origin":{"type":"string","enum":["llm","grid","human","dev-assisted"]}},"required":["hypothesis","definition","origin"],"additionalProperties":false}""")),
        new NeutralTool(ResearcherToolNames.GetExperimentResult, "Returns the metrics of a submitted experiment.",
            NeutralSchema.Parse("""{"type":"object","properties":{"experimentId":{"type":"integer","minimum":1}},"required":["experimentId"],"additionalProperties":false}""")),
    ]);

    public async Task<string> ExecuteAsync(AiToolCall call, CancellationToken ct) => (await InvokeAsync(call.Name, call.Arguments, ct)).ToJsonString();

    public async Task<JsonNode> InvokeAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        if (!Tools.Contains(name))
        {
            await safety.RecordAsync($"disallowed tool call '{name}'", ct);
            return Error($"tool '{name}' is not available");
        }

        var errors = Tools.Tools.Single(t => t.Name == name).Parameters.Validate(arguments);
        if (errors.Count > 0)
        {
            return Error("invalid arguments: " + string.Join("; ", errors));
        }

        return name switch
        {
            ResearcherToolNames.ListIndicators => ListIndicators(arguments["filter"]?.GetValue<string>()),
            ResearcherToolNames.SearchExperiments => await backend.SearchExperimentsAsync(arguments["query"]!.GetValue<string>(),
                Math.Min(arguments["limit"]?.GetValue<int>() ?? 20, MaxSearchResults), ct),
            ResearcherToolNames.GetDatasetCatalog => await Catalog(arguments["interval"]?.GetValue<string>(), ct),
            ResearcherToolNames.ValidateStrategyDefinition => Validate(arguments["definition"]!.GetValue<string>()),
            ResearcherToolNames.SubmitExperiment => await Submit(arguments, ct),
            ResearcherToolNames.GetExperimentResult => await backend.GetExperimentResultAsync(arguments["experimentId"]!.GetValue<long>(), ct),
            _ => Error($"tool '{name}' is not implemented"),
        };
    }

    public static JsonNode Validate(string definitionJson)
    {
        try
        {
            var definition = DslParser.Parse(definitionJson);
            return new JsonObject
            {
                ["valid"] = true,
                ["name"] = definition.Name,
                ["definitionHash"] = DslCanonical.DefinitionHash(definition),
                ["structureHash"] = DslCanonical.StructureHash(definition),
            };
        }
        catch (DslException e)
        {
            return new JsonObject { ["valid"] = false, ["errors"] = new JsonArray(e.Errors.Select(x => (JsonNode)x).ToArray()) };
        }
    }

    private async Task<JsonNode> Submit(JsonObject arguments, CancellationToken ct)
    {
        var definition = arguments["definition"]!.GetValue<string>();
        if (Validate(definition) is JsonObject { } validation && validation["valid"]!.GetValue<bool>() is false)
        {
            return validation;
        }

        return await backend.SubmitExperimentAsync(
            new ExperimentSubmission(arguments["hypothesis"]!.GetValue<string>(), definition, arguments["origin"]!.GetValue<string>()), ct);
    }

    private async Task<JsonNode> Catalog(string? interval, CancellationToken ct)
    {
        var catalog = await backend.GetDatasetCatalogAsync(ct);
        if (interval is null || catalog is not JsonArray rows)
        {
            return catalog;
        }

        return new JsonArray(rows.Where(r => r?["interval"]?.GetValue<string>() == interval).Select(r => r!.DeepClone()).ToArray());
    }

    private static JsonNode ListIndicators(string? filter) => new JsonArray(DslCatalog.Indicators.Values
        .Where(i => filter is null || i.Type.StartsWith(filter, StringComparison.Ordinal))
        .OrderBy(i => i.Type, StringComparer.Ordinal)
        .Select(i => (JsonNode)new JsonObject
        {
            ["type"] = i.Type,
            ["parameters"] = new JsonArray(i.Parameters.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["min"] = p.Min, ["max"] = p.Max, ["integer"] = p.Integer,
            }).ToArray()),
            ["outputs"] = new JsonArray(i.Outputs.Select(o => (JsonNode)o).ToArray()),
        }).ToArray());

    private static JsonObject Error(string message) => new() { ["error"] = message };
}
