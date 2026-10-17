using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai.Tests.Support;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>Neutral schemas, EvidenceValidator (architecture §3.3) and the AI configuration (T24, D4, ADR-0010).</summary>
public class SchemaEvidenceConfigurationTests
{
    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"anyOf":[{"type":"string"}]}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"$ref":"#"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","pattern":"x"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":true}""")]
    [InlineData("""{"type":"array"}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["b"]}""")]
    public void Schemas_outside_the_neutral_subset_are_rejected(string json)
    {
        Assert.Throws<ArgumentException>(() => NeutralSchema.Parse(json));
    }

    [Fact]
    public void Deep_schemas_are_rejected()
    {
        var json = """{"type":"string"}""";
        for (var i = 0; i < NeutralSchema.MaxDepth; i++)
        {
            json = """{"type":"object","properties":{"x":""" + json + "}}";
        }

        Assert.Throws<ArgumentException>(() => NeutralSchema.Parse(json));
    }

    [Fact]
    public void Validation_rejects_unknown_properties_wrong_types_missing_fields_and_enum_values()
    {
        var schema = NeutralSchema.Parse("""{"type":"object","properties":{"n":{"type":"integer","minimum":1},"c":{"type":"string","enum":["a","b"]},"l":{"type":"array","items":{"type":"number"},"maxItems":2}},"required":["n"],"additionalProperties":false}""");

        Assert.Empty(schema.Validate(JsonNode.Parse("""{"n":3,"c":"a","l":[1.5,2]}""")));
        Assert.NotEmpty(schema.Validate(JsonNode.Parse("""{"n":3,"x":1}""")));
        Assert.NotEmpty(schema.Validate(JsonNode.Parse("""{"n":1.5}""")));
        Assert.NotEmpty(schema.Validate(JsonNode.Parse("""{"n":0}""")));
        Assert.NotEmpty(schema.Validate(JsonNode.Parse("""{"c":"a"}""")));
        Assert.NotEmpty(schema.Validate(JsonNode.Parse("""{"n":3,"c":"z"}""")));
        Assert.NotEmpty(schema.Validate(JsonNode.Parse("""{"n":3,"l":[1,2,3]}""")));
        Assert.NotEmpty(schema.ParseAndValidate("prose, not JSON").Errors);
    }

    [Fact]
    public async Task Evidence_validator_rejects_fabricated_late_and_mismatched_references()
    {
        var decision = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var validator = new EvidenceValidator(new Resolver(new Dictionary<string, EvidenceItem>
        {
            ["exp:1"] = new("exp:1", decision.AddDays(-1), 0.125m),
            ["exp:2"] = new("exp:2", decision.AddHours(1), 0.5m),
        }));

        static JsonNode Claims(params JsonObject[] claims) => new JsonObject { ["answer"] = "x", ["claims"] = new JsonArray(claims) };

        Assert.Empty(await validator.ValidateAsync(Claims(new JsonObject { ["text"] = "M_R is 12.5 %", ["evidenceRef"] = "exp:1", ["value"] = 0.125 }), decision, default));
        Assert.Empty(await validator.ValidateAsync(Claims(new JsonObject { ["text"] = "context", ["critical"] = false }), decision, default));
        Assert.Contains("does not exist", Assert.Single(await validator.ValidateAsync(Claims(new JsonObject { ["text"] = "x", ["evidenceRef"] = "exp:999" }), decision, default)));
        Assert.Contains("not available at decision time", Assert.Single(await validator.ValidateAsync(Claims(new JsonObject { ["text"] = "x", ["evidenceRef"] = "exp:2" }), decision, default)));
        Assert.Contains("does not match", Assert.Single(await validator.ValidateAsync(Claims(new JsonObject { ["text"] = "x", ["evidenceRef"] = "exp:1", ["value"] = 0.2 }), decision, default)));
        Assert.Contains("without evidenceRef", Assert.Single(await validator.ValidateAsync(Claims(new JsonObject { ["text"] = "critical, unsupported" }), decision, default)));
    }

    [Fact]
    public async Task Fabricated_reference_never_passes_through_the_router()
    {
        var rig = new RouterRig();
        var validator = new EvidenceValidator(new Resolver(new Dictionary<string, EvidenceItem>()));
        rig.Provider.Returns(RouterRig.Answer("x", [new JsonObject { ["text"] = "made up", ["evidenceRef"] = "exp:404" }]));

        var result = await rig.Router.ExecuteAsync(TaskClass.Summary, TestConfig.Input([validator]), TestConfig.Research, default);

        Assert.Null(result.Output);
        Assert.False(result.MayAffectDecisions);
        Assert.Contains("evidence validator rejected", result.Reason);
    }

    [Fact]
    public void Default_configuration_starts_at_the_lower_end_of_the_budget_and_has_no_qualified_model()
    {
        var configuration = AiConfiguration.LoadDefault();

        Assert.Equal(10m, configuration.Spend.MonthlyCapUsd);
        Assert.Equal(new UsdAmount(3m), configuration.Spend.JobCap(JobKind.Qualification));
        Assert.Equal(new UsdAmount(0.5m), configuration.Spend.JobCap(JobKind.ResearchCycle));
        Assert.Equal(1, configuration.Resilience.Concurrency);
        Assert.Equal(2, configuration.Resilience.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.Policy(TaskClass.HypothesisGeneration).Timeout);
        foreach (var task in new[] { TaskClass.HypothesisGeneration, TaskClass.FindingExtraction, TaskClass.AllocationProposal })
        {
            Assert.Equal(TaskKind.DecisionAffecting, configuration.Policy(task).Kind);
            Assert.Empty(configuration.Policy(task).QualifiedModels ?? []);
        }

        Assert.Equal(UnavailableBehaviour.DeterministicFallback, configuration.Policy(TaskClass.HypothesisGeneration).OnUnavailable);
        Assert.Equal(0.30m, configuration.Prices.For("gemini", configuration.Policy(TaskClass.Summary).Model!.Model).InputPerMillionUsd);
    }

    [Fact]
    public void Monthly_cap_above_the_user_approved_range_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => TestConfig.Configuration(monthlyCap: 31m));
    }

    [Fact]
    public void Model_names_are_not_hard_coded_in_the_ai_sources()
    {
        // T24: model names live in configuration only.
        var root = RepositoryRoot.Path;
        var offenders = new[] { "src/Modules/Ai", "src/Modules/Ai.Gemini", "src/Modules/Researcher" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("gemini-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    private sealed class Resolver(Dictionary<string, EvidenceItem> items) : IEvidenceResolver
    {
        public Task<EvidenceItem?> ResolveAsync(string evidenceRef, CancellationToken ct) => Task.FromResult(items.GetValueOrDefault(evidenceRef));
    }
}
