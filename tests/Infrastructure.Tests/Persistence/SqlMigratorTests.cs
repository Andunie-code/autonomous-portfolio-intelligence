using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.Infrastructure.Tests.Persistence;

/// <summary>Runs the real migrations against a throwaway PostgreSQL container (requires Docker).</summary>
public class SqlMigratorTests : IAsyncLifetime
{
    // Same major version as docker-compose.yml.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly SqlMigrator _migrator = new(NullLogger<SqlMigrator>.Instance);
    private readonly IReadOnlyList<Migration> _migrations = SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly);

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public void Embedded_migrations_are_found_and_ordered()
    {
        Assert.NotEmpty(_migrations);
        Assert.Equal(1, _migrations[0].Version);
        Assert.Equal(_migrations.OrderBy(m => m.Version).Select(m => m.Version), _migrations.Select(m => m.Version));
    }

    [Fact]
    public async Task Migrations_create_module_schemas_and_are_idempotent()
    {
        var first = await _migrator.MigrateAsync(_postgres.GetConnectionString(), _migrations, CancellationToken.None);
        var second = await _migrator.MigrateAsync(_postgres.GetConnectionString(), _migrations, CancellationToken.None);

        Assert.Equal(_migrations.Count, first.Applied.Count);
        Assert.Empty(second.Applied);
        Assert.Equal(_migrations.Count, second.AlreadyApplied.Count);

        var schemas = await QueryStringsAsync("SELECT schema_name FROM information_schema.schemata");
        Assert.Superset(new HashSet<string> { "market", "research", "trading", "audit" }, schemas.ToHashSet());
        Assert.DoesNotContain(schemas, s => s.Contains("holdout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Modified_applied_migration_is_rejected()
    {
        await _migrator.MigrateAsync(_postgres.GetConnectionString(), _migrations, CancellationToken.None);
        var tampered = _migrations.Select((m, i) => i == 0 ? m with { Sql = m.Sql + "\n-- edited" } : m).ToList();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migrator.MigrateAsync(_postgres.GetConnectionString(), tampered, CancellationToken.None));
        Assert.Contains("was modified after it was applied", error.Message);
    }

    [Fact]
    public async Task Failing_migration_is_rolled_back_and_not_recorded()
    {
        var broken = new List<Migration> { new(9001, "broken", "CREATE SCHEMA partial_schema; SELECT * FROM missing_table;") };

        await Assert.ThrowsAsync<PostgresException>(
            () => _migrator.MigrateAsync(_postgres.GetConnectionString(), broken, CancellationToken.None));

        Assert.DoesNotContain("partial_schema", await QueryStringsAsync("SELECT schema_name FROM information_schema.schemata"));
        Assert.Empty(await QueryStringsAsync("SELECT name FROM public.schema_migrations WHERE version = 9001"));
    }

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
