using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Kernel.Statistics;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Evaluation;
using PortfolioIntelligence.Research.Families;
using PortfolioIntelligence.Research.Hypotheses;
using PortfolioIntelligence.Researcher;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>M4 kernel additions (Fisher exact test, DSL serializer) and the pre-registered grid selection (m4-protocol §5–§6).</summary>
public class M4KernelAndSelectionTests
{
    [Theory]
    [InlineData(3, 4, 1, 4, 17.0 / 70)] // [[3,1],[1,3]]: P(X ≥ 3) = (16 + 1) / 70
    [InlineData(6, 6, 0, 6, 1.0 / 924)]
    [InlineData(0, 10, 5, 10, 1.0)]
    [InlineData(5, 10, 5, 10, 0.6718591006516703)] // P(X ≥ 5), X ~ Hypergeometric(20, 10, 10)
    public void Fisher_exact_one_sided_matches_hand_computed_values(int a, int n, int b, int m, double expected)
    {
        Assert.Equal(expected, FisherExact.GreaterPValue(a, n, b, m), 9);
    }

    [Fact]
    public void Fisher_exact_p_value_falls_as_arm_a_improves()
    {
        var previous = 1.0;
        for (var a = 0; a <= 20; a++)
        {
            var p = FisherExact.GreaterPValue(a, 20, 4, 20);
            Assert.True(p <= previous + 1e-12);
            previous = p;
        }
    }

    [Fact]
    public void Serialized_definitions_parse_back_to_the_same_definition_hash()
    {
        var definitions = new List<DslDefinition> { DslParser.Parse(DslBaselines.EmaTrendJson), DslParser.Parse(DslBaselines.SupertrendJson) };
        var random = new RandomStrategies(4242);
        for (var i = 0; i < 500; i++)
        {
            definitions.Add(random.Next());
        }

        foreach (var definition in definitions)
        {
            var json = DslJson.Serialize(definition);
            var parsed = DslParser.Parse(json);
            Assert.Equal(DslCanonical.DefinitionHash(definition), DslCanonical.DefinitionHash(parsed));
            Assert.Equal(definition.Name, parsed.Name);
            Assert.Equal(json, DslJson.Serialize(parsed));
        }
    }

    [Fact]
    public void Candidate_is_the_highest_research_metric_and_ties_go_to_the_earlier_point()
    {
        var points = new[] { Run("a", 0.0001), Run("b", 0.0003), Run("c", 0.0003), Run("d", -0.0002) };

        Assert.Equal(1, GridSelection.SelectCandidate(points));
    }

    [Fact]
    public void Anchored_walk_forward_selects_on_earlier_years_only()
    {
        // a: strong in 2022, then negative; b: weak in 2022, strong afterwards.
        var a = Run("a", y => y == 2022 ? 0.001 : -0.0005);
        var b = Run("b", y => y == 2022 ? 0.0001 : 0.0008);

        var walkForward = GridSelection.Anchored([a, b]);

        Assert.Equal([2023, 2024, 2025], walkForward.Folds.Select(f => f.TestYear));
        Assert.Equal("a", walkForward.Folds[0].SelectedLabel); // only 2022 is known: a looks best
        Assert.True(walkForward.Folds[0].TestMetric < 0);
        Assert.Equal("b", walkForward.Folds[2].SelectedLabel); // by 2025, b's 2023–2024 lead is visible
        Assert.True(walkForward.Share < 1);

        // Changing year k's returns of every point never changes which point fold k selected.
        var changed = GridSelection.Anchored([Run("a", y => y == 2022 ? 0.001 : y == 2023 ? 0.01 : -0.0005), b]);
        Assert.Equal(walkForward.Folds[0].SelectedLabel, changed.Folds[0].SelectedLabel);
    }

    internal static GridPointRun Run(string label, double perBar) => Run(label, _ => perBar);

    internal static GridPointRun Run(string label, Func<int, double> perBarByYear)
    {
        var equity = new List<EquityPoint>();
        var value = 10_000.0;
        for (var t = ResearchCutoff.U.AddHours(4); t <= ResearchCutoff.C; t = t.AddHours(4))
        {
            value *= Math.Exp(perBarByYear(t.AddTicks(-1).Year));
            equity.Add(new EquityPoint(t, (decimal)value, 0));
        }

        return new GridPointRun(label, new BacktestResult(label, "no-halt", 10_000m, [], equity, null));
    }
}

/// <summary>The Research module's tool backend and hypothesis pipeline on a synthetic universe and a real PostgreSQL (requires Docker).</summary>
public class ResearchToolBackendTests : IAsyncLifetime
{
    private const string ListHash = "89ab3965a8cbaf068788a95bc354f0340c29ae1b8d3834ef6c840bad1d4c2ec7";
    private const string Snapshot = "a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private string Connection => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance)
            .MigrateAsync(Connection, SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly), CancellationToken.None);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Cycle_records_hypotheses_trials_and_experiments_and_gates_each_hypothesis()
    {
        var list = Load(1);
        var (backend, toolbox, safety) = Backend(SyntheticMarkets.Null(3), cap: 100);

        var report = await new ResearchCycle(toolbox, backend, safety).RunAsync(new ApprovedHypothesisSource(list, Provenance), _ => { }, default);

        var verdict = Assert.Single(report.Verdicts);
        Assert.NotEqual(HypothesisCategory.Error, verdict.Category);
        Assert.NotNull(verdict.SelectedPoint);
        Assert.Equal(3, verdict.Details["noHalt"]!["folds"]!.AsArray().Count);
        Assert.Equal(list.TrialCount, await Scalar<long>("SELECT count(*) FROM research.trials WHERE origin = 'grid' AND hypothesis_id IS NOT NULL"));
        Assert.Equal(list.TrialCount, await Scalar<long>("SELECT count(*) FROM research.experiments WHERE protocol_version = 'm4-protocol v1' AND variant = 'no-halt'"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM research.trial_families"));
        Assert.True(await Scalar<bool>("SELECT knowledge_overlaps_diagnostic FROM research.hypotheses WHERE hypothesis_key = 'h01' AND origin = 'dev-assisted'"));
        Assert.Empty(report.SafetyViolations);
    }

    [Fact]
    public async Task Repeat_run_creates_no_new_trial_and_the_global_cap_is_enforced()
    {
        var list = Load(1);
        var (backend, toolbox, safety) = Backend(SyntheticMarkets.Null(4), cap: 5);

        var report = await new ResearchCycle(toolbox, backend, safety).RunAsync(new ApprovedHypothesisSource(list, Provenance), _ => { }, default);

        Assert.Equal(5L, await Scalar<long>("SELECT count(*) FROM research.trials"));
        Assert.Equal(list.TrialCount - 5, report.Points.Count(p => p.Submission["error"]?.GetValue<string>().Contains("trial budget exhausted") == true));
        Assert.Equal(HypothesisCategory.Error, Assert.Single(report.Verdicts).Category);

        var (again, againTools, againSafety) = Backend(SyntheticMarkets.Null(4), cap: 5);
        var second = await new ResearchCycle(againTools, again, againSafety).RunAsync(new ApprovedHypothesisSource(list with { Hypotheses = [list.Hypotheses[0] with { Points = list.Hypotheses[0].Points.Take(5).ToList() }] }, Provenance), _ => { }, default);

        Assert.All(second.Points, p => Assert.False(p.Submission["newTrial"]!.GetValue<bool>()));
        Assert.Equal(5L, await Scalar<long>("SELECT count(*) FROM research.trials"));
    }

    [Fact]
    public async Task Experiment_reaching_past_the_cutoff_is_rejected_and_audited()
    {
        var synthetic = SyntheticMarkets.Null(5);
        var (backend, toolbox, _) = Backend(synthetic with { End = ResearchCutoff.C.AddHours(4) }, cap: 100);
        var hypothesis = Load(1).Hypotheses[0];
        await backend.RegisterHypothesisAsync(hypothesis, Provenance, default);

        var result = await toolbox.InvokeAsync(ResearcherToolNames.SubmitExperiment, new JsonObject
        {
            ["hypothesis"] = hypothesis.Key, ["definition"] = hypothesis.Points[0].DefinitionJson, ["origin"] = "grid",
        }, default);

        Assert.Contains("research cutoff", result["error"]!.GetValue<string>());
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM research.trials"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM audit.events WHERE event = 'research-request-rejected'"));
    }

    private static readonly HypothesisProvenance Provenance = new("dev-assisted", "grid", "test", new DateOnly(2026, 6, 30), true, "user (test)");

    private static HypothesisList Load(int hypotheses)
    {
        var path = Path.Combine(TestPaths.RepositoryRoot, "docs", "m4-hypotheses.v1.json");
        var list = HypothesisList.Load(File.ReadAllText(path), ListHash);
        return list with { Hypotheses = list.Hypotheses.Take(hypotheses).ToList() };
    }

    private (ResearchToolBackend Backend, ResearcherToolbox Toolbox, InMemorySafetyLog Safety) Backend(BacktestInput input, int cap)
    {
        var audit = new AuditLog(Connection);
        var backend = new ResearchToolBackend(input,
            new ResearchCycleSettings("m4-protocol v1", "test", "test-sha", Snapshot, cap, 98, new GateThresholds(100, 0.75, 0.80, 0.90), BootstrapSeed: 1),
            new TrialRegistry(new NpgsqlFamilyStore(Connection), Snapshot), new ExperimentStore(Connection), new ExperimentRequestGuard(audit),
            new DatasetCatalog(Connection), new HypothesisStore(Connection));
        var safety = new InMemorySafetyLog();
        return (backend, new ResearcherToolbox(backend, safety), safety);
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = Find();

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PortfolioIntelligence.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
