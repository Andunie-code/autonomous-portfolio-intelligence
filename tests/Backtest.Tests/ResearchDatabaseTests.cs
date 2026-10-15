using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Backtest.Tests.Support;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.Research;
using PortfolioIntelligence.Research.Families;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.Backtest.Tests;

/// <summary>Research-side database objects of M3 (migrations 0004–0005; H7, H8, V14; ADR-0015); requires Docker.</summary>
public class ResearchDatabaseTests : IAsyncLifetime
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
    public async Task Dataset_catalog_lists_nothing_at_or_after_the_cutoff()
    {
        await Execute("INSERT INTO market.source_files (archive_key, sha256, size_bytes, row_count) VALUES ('k', repeat('a', 64), 1, 2)");
        await Execute("""
            INSERT INTO market.candles VALUES
              ('XUSDT', '4h', timestamptz '2025-12-31 16:00+00', 1, 2, 0.5, 1.5, 1, 1, 1, 1),
              ('XUSDT', '4h', timestamptz '2025-12-31 20:00+00', 1, 2, 0.5, 1.5, 1, 1, 1, 1)
            """);
        var afterCutoff = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "INSERT INTO market.candles VALUES ('XUSDT', '4h', timestamptz '2026-01-01 00:00+00', 1, 2, 0.5, 1.5, 1, 1, 1, 1)"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, afterCutoff.SqlState);

        var catalog = await new DatasetCatalog(Connection).GetAsync(default);

        var entry = Assert.Single(catalog);
        Assert.Equal(ResearchCutoff.C, entry.End);
        Assert.All(catalog, e => Assert.True(e.End <= ResearchCutoff.C));
    }

    [Fact]
    public async Task Requests_reaching_past_the_cutoff_are_rejected_and_audited()
    {
        var guard = new ExperimentRequestGuard(new AuditLog(Connection));

        await guard.EnsureAllowedAsync("researcher", "experiment", ResearchCutoff.U, ResearchCutoff.C, default);
        await Assert.ThrowsAsync<ResearchRequestRejectedException>(() =>
            guard.EnsureAllowedAsync("researcher", "tuning", ResearchCutoff.U, ResearchCutoff.C.AddHours(4), default));

        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM audit.events WHERE event = 'research-request-rejected'"));
        var change = await Assert.ThrowsAsync<PostgresException>(() => Execute("DELETE FROM audit.events"));
        Assert.Contains("append-only", change.MessageText);
    }

    [Fact]
    public async Task Registry_strategy_versions_are_immutable_and_read_only_for_trading()
    {
        var strategy = DslBaselines.Supertrend();
        await Execute($"INSERT INTO research.strategy_versions (name, definition_hash, definition) VALUES ('supertrend', '{strategy.DefinitionHash}', '{strategy.Definition}')");

        foreach (var sql in new[] { "UPDATE research.strategy_versions SET name = 'x'", "DELETE FROM research.strategy_versions" })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => Execute(sql));
            Assert.Contains("append-only", e.MessageText);
        }

        var trading = await Assert.ThrowsAsync<PostgresException>(() => Execute("INSERT INTO research.strategy_versions (name, definition_hash, definition) VALUES ('x', repeat('b', 64), '{}')", "trading_app"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, trading.SqlState);
        var storedHash = await Scalar<string>("SELECT definition_hash FROM research.strategy_versions");
        Assert.Equal(storedHash, StrategyHashGuard.LoadVerified(DslBaselines.SupertrendJson, storedHash).DefinitionHash);
    }

    [Fact]
    public async Task Postgres_family_store_keeps_families_trials_and_representatives_append_only()
    {
        var store = new NpgsqlFamilyStore(Connection);
        var registry = new TrialRegistry(store, new string('c', 64), batchSize: 1);
        var symbols = Enumerable.Range(0, 3).Select(i => Synthetic.RandomWalk($"S{i}USDT", 8700, 90 + i)).ToList();
        var input = new BacktestInput(
            symbols.ToDictionary(s => s[0].Symbol, s => (IReadOnlyList<Candle>)s),
            symbols.Select((s, i) => (s[0].Symbol, i + 1)).ToDictionary(x => x.Symbol, x => x.Item2),
            Synthetic.At(300), symbols[0][^1].OpenTime.AddHours(4));
        BacktestResult Run(DslDefinition d) => new BacktestEngine(new DslStrategy(d), CostModel.V1, RiskSettings.NoHalt).Run(input);

        var supertrend = DslParser.Parse(DslBaselines.SupertrendJson);
        var ema = DslParser.Parse(DslBaselines.EmaTrendJson);
        var a = await registry.RecordAsync(new TrialRequest(supertrend, TrialOrigin.DevAssisted, "m2-protocol v1"), Run(supertrend), default);
        var b = await registry.RecordAsync(new TrialRequest(ema, TrialOrigin.DevAssisted, "m2-protocol v1"), Run(ema), default);
        var copy = DslParser.Parse(DslBaselines.SupertrendJson.Replace("\"stop\":  { \"type\": \"indicator\", \"ref\": \"st.lower\", \"trailing\": true }",
            "\"stop\":  { \"type\": \"percent\", \"pct\": 0.1 }"));
        var c = await registry.RecordAsync(new TrialRequest(copy, TrialOrigin.Llm, "m3-protocol v1"), Run(copy), default);

        Assert.True(a.NewFamily && b.NewFamily);
        Assert.NotEqual(a.FamilyId, b.FamilyId);
        Assert.Equal(a.FamilyId, c.FamilyId);
        Assert.Equal("similarity", c.AssignedBy);
        Assert.Equal(3L, await Scalar<long>("SELECT count(*) FROM research.trials"));
        Assert.Equal(2L, await Scalar<long>("SELECT count(DISTINCT family_id) FROM research.family_artifacts"));
        Assert.Equal(3, (await registry.ContextAsync(1e-4, default)).Trials);
        var e = await Assert.ThrowsAsync<PostgresException>(() => Execute("UPDATE research.trial_families SET representative_definition = repeat('0', 64)"));
        Assert.Contains("append-only", e.MessageText);
    }

    private async Task Execute(string sql, string? role = null)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        if (role is not null)
        {
            await using var set = new NpgsqlCommand($"SET ROLE {role}", connection);
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
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
