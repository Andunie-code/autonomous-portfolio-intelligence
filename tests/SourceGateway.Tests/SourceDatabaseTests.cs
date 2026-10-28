using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using PortfolioIntelligence.SourceGateway.Text;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.SourceGateway.Tests;

/// <summary>Migration 0010 (sources schema): append-only provenance, the cutoff CHECK, the finding rules and the store (requires Docker).</summary>
public class SourceDatabaseTests : IAsyncLifetime
{
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
    public async Task Documents_and_findings_round_trip_and_only_verified_findings_are_searchable()
    {
        var store = new NpgsqlSourceStore(Connection);
        var id = await store.AddDocumentAsync(Document("2301.01234", new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero)), default);
        var verified = await store.AddFindingAsync(Finding(id, true, "The rule earns a Sharpe ratio of 1.1.", "earns an annualized Sharpe ratio of 1.1"), default);
        await store.AddFindingAsync(Finding(id, false, "Momentum works always.", "momentum works in every market"), default);

        var document = Assert.Single(await store.DocumentsAsync(default));
        Assert.Equal(("2301.01234", 2, DocumentText.Pdf), (document.ArxivId, document.ArxivVersion, document.TextKind));
        Assert.Equal(2, (await store.FindingsAsync(id, default)).Count);
        var found = Assert.Single(await store.SearchVerifiedAsync(["sharpe"], null, 10, default));
        Assert.Equal((verified, "arXiv:2301.01234v2"), (found.Id, found.Source));
        Assert.Empty(await store.SearchVerifiedAsync(["momentum"], null, 10, default)); // rejected findings never reach the Researcher
        var (valid, latest) = await store.UsableForHypothesisAsync([verified, verified + 1, 999], default);
        Assert.Equal([verified], valid);
        Assert.Equal(new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero), latest);
    }

    [Fact]
    public async Task The_database_refuses_post_cutoff_versions_and_inconsistent_findings_and_rows_are_append_only()
    {
        var store = new NpgsqlSourceStore(Connection);
        var late = await Assert.ThrowsAsync<PostgresException>(() => store.AddDocumentAsync(Document("2301.09999", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), default));
        Assert.Equal(PostgresErrorCodes.CheckViolation, late.SqlState);

        var id = await store.AddDocumentAsync(Document("2301.01234", new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero)), default);
        var noReason = await Assert.ThrowsAsync<PostgresException>(() => store.AddFindingAsync(Finding(id, false, "c", "q") with { RejectionReason = null }, default));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noReason.SqlState);
        var noOffsets = await Assert.ThrowsAsync<PostgresException>(() => store.AddFindingAsync(Finding(id, true, "c", "q") with { QuoteStart = null }, default));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noOffsets.SqlState);

        await store.AddFindingAsync(Finding(id, false, "c", "q"), default); // a row-level trigger needs a row to fire on
        foreach (var sql in new[] { "UPDATE sources.source_documents SET truncated = true", "DELETE FROM sources.source_documents", "UPDATE sources.findings SET verified = true" })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => Execute(sql));
            Assert.Contains("append-only", e.MessageText);
        }
    }

    [Fact]
    public async Task A_hypothesis_may_name_only_verified_pre_cutoff_findings()
    {
        var store = new NpgsqlSourceStore(Connection);
        var id = await store.AddDocumentAsync(Document("2301.01234", new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero)), default);
        var verified = await store.AddFindingAsync(Finding(id, true, "c", "q"), default);
        var rejected = await store.AddFindingAsync(Finding(id, false, "c", "q"), default);
        const string insert = """
            INSERT INTO research.hypotheses (hypothesis_key, protocol_version, title, origin, author, knowledge_overlaps_diagnostic, structure_hash, source_finding_ids)
            VALUES ($1, 'm7-protocol v1', 't', 'human', 'test', false, repeat('a', 64), $2)
            """;

        await Execute(insert, "h-ok", new[] { verified });
        foreach (var ids in new[] { new[] { rejected }, new[] { verified, 4242L } })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => Execute(insert, "h-" + ids[^1], ids));
            Assert.Contains("source finding", e.MessageText);
        }
    }

    [Fact]
    public async Task One_arm_protocols_need_a_p0_and_existing_protocols_stay_two_arm()
    {
        await Execute("""
            INSERT INTO ai.qualification_protocols (protocol_hash, document_commit, task_class, provider, model, prompt_version, schema_hash, pilot_set_hash,
                qualification_set_hash, alpha, minimum_effect, sample_per_arm, budget_usd)
            VALUES (repeat('1', 64), 'c', 'HypothesisGeneration', 'gemini', 'm', 'p', repeat('2', 64), repeat('3', 64), repeat('4', 64), 0.05, 0.15, 10, 1)
            """);
        Assert.Equal("TwoArmFisher", await Scalar<string>("SELECT design FROM ai.qualification_protocols"));

        var e = await Assert.ThrowsAsync<PostgresException>(() => Execute("""
            INSERT INTO ai.qualification_protocols (protocol_hash, document_commit, task_class, provider, model, prompt_version, schema_hash, pilot_set_hash,
                qualification_set_hash, alpha, minimum_effect, sample_per_arm, budget_usd, design, p0)
            VALUES (repeat('5', 64), 'c', 'FindingExtraction', 'gemini', 'm', 'p', repeat('2', 64), repeat('3', 64), repeat('4', 64), 0.05, 0, 10, 1, 'OneArmBinomial', 0)
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, e.SqlState);
    }

    private static SourceDocument Document(string arxivId, DateTimeOffset versionAt) => new(0, $"https://arxiv.org/pdf/{arxivId}v2", $"https://arxiv.org/abs/{arxivId}v2",
        SourceKind.ArxivPdf, TrustTier.T1, DocumentText.Pdf, arxivId, 2, new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero), versionAt,
        DateTimeOffset.UtcNow, new string('a', 64), new string('b', 64), 1234, "application/pdf", PdfTextExtractor.Version, TextNormalizer.Version, 0, 0, false);

    private static Finding Finding(long documentId, bool verified, string claim, string quote) => new(0, documentId, Guid.NewGuid(), "empiricalResult", claim, quote,
        verified ? 10 : null, verified ? 10 + quote.Length : null, ["1.1"], "high", verified, verified ? null : "fabricated-quote");

    private async Task Execute(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
