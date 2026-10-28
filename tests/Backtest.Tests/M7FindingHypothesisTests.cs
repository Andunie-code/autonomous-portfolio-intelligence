using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Evaluation;
using PortfolioIntelligence.Research.Families;
using PortfolioIntelligence.Research.Hypotheses;
using PortfolioIntelligence.Research.Sources;
using PortfolioIntelligence.Researcher;
using PortfolioIntelligence.SourceGateway;
using PortfolioIntelligence.SourceGateway.Text;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>
/// Roadmap M7 criterion 5 (m7-protocol §8.2): a hypothesis written from a verified finding goes through the normal pipeline
/// (DSL, trial counter, G1–G6) with nothing skipped; unverified or post-cutoff findings cannot be named (requires Docker).
/// </summary>
public class M7FindingHypothesisTests : IAsyncLifetime
{
    internal const string ListHash = "89ab3965a8cbaf068788a95bc354f0340c29ae1b8d3834ef6c840bad1d4c2ec7";
    private const string Snapshot = "a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82";

    private static readonly HypothesisProvenance Human = new("human", "grid", "user (test)", null, false, "user (test)");

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
    public async Task A_finding_derived_hypothesis_passes_through_the_normal_pipeline()
    {
        var (finding, _) = await FindingsAsync();
        var hypothesis = First() with { SourceFindingIds = [finding] };
        var (backend, toolbox, safety) = Backend(SyntheticMarkets.Null(7), cap: 100);

        var report = await new ResearchCycle(toolbox, backend, safety).RunAsync(new ApprovedHypothesisSource(List(hypothesis), Human), _ => { }, default);

        var verdict = Assert.Single(report.Verdicts);
        Assert.NotEqual(HypothesisCategory.Error, verdict.Category);
        Assert.Equal(3, verdict.Details["noHalt"]!["folds"]!.AsArray().Count); // the same selection and gate evaluation as any hypothesis
        Assert.NotNull(verdict.SelectedPoint);
        Assert.Empty(report.SafetyViolations);
        Assert.Equal(hypothesis.Points.Count, await Scalar<long>("SELECT count(*) FROM research.trials WHERE origin = 'grid' AND hypothesis_id IS NOT NULL"));
        Assert.Equal(new[] { finding }, await Scalar<long[]>("SELECT source_finding_ids FROM research.hypotheses"));
        // The knowledge cutoff is at least the source version date (U-M7-2).
        Assert.Equal(new DateOnly(2023, 6, 1), DateOnly.FromDateTime(await Scalar<DateTime>("SELECT knowledge_cutoff::timestamp FROM research.hypotheses")));
    }

    [Fact]
    public async Task Unverified_or_unknown_findings_refuse_the_hypothesis_before_any_trial()
    {
        var (_, rejected) = await FindingsAsync();
        var (backend, _, _) = Backend(SyntheticMarkets.Null(8), cap: 100);

        foreach (var ids in new[] { new[] { rejected }, new[] { 4242L } })
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.RegisterHypothesisAsync(First() with { SourceFindingIds = ids }, Human, default));
            Assert.Contains("unverified or from a post-cutoff document", e.Message);
        }

        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM research.hypotheses"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM research.trials"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HypothesisStore(Connection).EnsureAsync(First() with { SourceFindingIds = [1] }, Human, "m7-protocol v1", default)); // no source store configured
    }

    private async Task<(long Verified, long Rejected)> FindingsAsync()
    {
        var store = new NpgsqlSourceStore(Connection);
        var document = await store.AddDocumentAsync(new SourceDocument(0, "https://arxiv.org/pdf/2301.01234v2", "https://arxiv.org/abs/2301.01234v2", SourceKind.ArxivPdf,
            TrustTier.T1, DocumentText.Pdf, "2301.01234", 2, new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow, new string('a', 64), new string('b', 64), 100, "application/pdf", PdfTextExtractor.Version, TextNormalizer.Version, 0, 0, false), default);
        var verified = await store.AddFindingAsync(new Finding(0, document, Guid.NewGuid(), "empiricalResult", "Trend rules on BTC earn 1.1.",
            "the rule earns an annualized Sharpe ratio of 1.1", 100, 146, ["1.1"], "high", true, null), default);
        var rejected = await store.AddFindingAsync(new Finding(0, document, Guid.NewGuid(), "empiricalResult", "It always wins.", "never in the text", null, null, [], "high", false,
            "fabricated-quote"), default);
        return (verified, rejected);
    }

    internal static Hypothesis First() =>
        HypothesisList.Load(File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "docs", "m4-hypotheses.v1.json")), ListHash).Hypotheses[0];

    private static HypothesisList List(Hypothesis hypothesis) => new("m7-test", [hypothesis], HypothesisList.ListHashOf(hypothesis.Points.Select(p => p.DefinitionHash)));

    private (ResearchToolBackend Backend, ResearcherToolbox Toolbox, InMemorySafetyLog Safety) Backend(BacktestInput input, int cap)
    {
        var audit = new AuditLog(Connection);
        var backend = new ResearchToolBackend(input,
            new ResearchCycleSettings("m7-protocol v1", "test", "test-sha", Snapshot, cap, 100, new GateThresholds(100, 0.75, 0.80, 0.90), BootstrapSeed: 1),
            new TrialRegistry(new NpgsqlFamilyStore(Connection), Snapshot), new ExperimentStore(Connection), new ExperimentRequestGuard(audit),
            new DatasetCatalog(Connection), new HypothesisStore(Connection, new NpgsqlSourceStore(Connection)));
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

/// <summary>Pure parts of M7 research: hypothesis lists with source findings and the descriptive comparison report (m7-protocol §8.2, §9).</summary>
public class M7ComparisonReportTests
{
    [Fact]
    public void Hypothesis_lists_may_name_source_findings_without_changing_the_list_hash()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "docs", "m4-hypotheses.v1.json")))!;
        json["hypotheses"]![0]!["sourceFindingIds"] = new JsonArray(3, 5);

        var list = HypothesisList.Load(json.ToJsonString(), M7FindingHypothesisTests.ListHash);

        Assert.Equal([3L, 5L], list.Hypotheses[0].SourceFindingIds);
        Assert.Null(list.Hypotheses[1].SourceFindingIds);
    }

    [Fact]
    public void Comparison_report_is_descriptive_and_says_when_web_research_is_closed()
    {
        var hypothesis = M7FindingHypothesisTests.First();
        var report = new CycleReport(
            [new HypothesisVerdict(hypothesis.Key, hypothesis.Title, HypothesisCategory.Rejected, "p1", ["G2", "G6"], new JsonObject())],
            hypothesis.Points.Select(p => new PointRecord(hypothesis.Key, p.Label, p.DefinitionHash, new JsonObject(), new JsonObject { ["newTrial"] = true }, null)).ToList(),
            []);

        var closed = WebComparison.Markdown(new ComparisonArm("web", [], null, 0), new ComparisonArm("no-web", [hypothesis], report, 0), false, "m7-protocol v1", "abc", 98, 106);
        var open = WebComparison.Markdown(new ComparisonArm("web", [hypothesis with { SourceFindingIds = [7] }], report, 0.012m),
            new ComparisonArm("no-web", [hypothesis], report, 0), true, "m7-protocol v1", "abc", 98, 114);

        Assert.Contains(WebResearchGate.ClosedNote, closed);
        Assert.Contains("| web | 0 | 0 | — | — | 0.000000 USD | — |", closed);
        Assert.Contains($"| no-web | 1 | {hypothesis.Points.Count} | 0 | 0 | 0.000000 USD | G2 (1), G6 (1) |", closed);
        Assert.DoesNotContain(WebResearchGate.ClosedNote, open);
        Assert.Contains($"| {hypothesis.Key} | 7 | Rejected | G2, G6 |", open);
        Assert.Contains("İstatistiksel iddia taşımaz", open);
    }
}
