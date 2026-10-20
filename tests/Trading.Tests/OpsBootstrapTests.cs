using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// The database side of scripts/m5-bootstrap.ps1 (docs/m5-protocol.md §4.2) on an M0–M4 shaped database:
/// migrations 0001–0006 applied by the superuser to portfolio_dev, then the rename, the logins, the ownership
/// transfer (deploy/postgres/ops-ownership.sql) and the remaining migrations as the non-superuser ops_owner.
/// </summary>
public class OpsBootstrapTests : IAsyncLifetime
{
    private static readonly string LoginSecret = Guid.NewGuid().ToString("N");
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private string Database(string name, string? user = null) => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
    {
        Database = name, Pooling = false, Username = user ?? new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()).Username,
        Password = (user is null ? new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()).Password : LoginSecret),
    }.ConnectionString;

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Ops_migrations_run_as_ops_owner_after_the_ownership_transfer()
    {
        var all = SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly);
        var migrator = new SqlMigrator(NullLogger<SqlMigrator>.Instance);
        await Execute(Database("postgres"), "CREATE DATABASE portfolio_dev");
        await migrator.MigrateAsync(Database("portfolio_dev"), all.Where(m => m.Version <= 6).ToList(), CancellationToken.None);

        await Execute(Database("postgres"), "ALTER DATABASE portfolio_dev RENAME TO ops");
        foreach (var role in new[] { "ops_owner", "research_app", "trading_app", "ops_api", "market_import", "status_relay" })
        {
            await Execute(Database("postgres"), $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}') THEN CREATE ROLE {role}; END IF; END $$;");
            await Execute(Database("postgres"), $"ALTER ROLE {role} WITH LOGIN PASSWORD '{LoginSecret}'");
        }

        var ownership = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "ops-ownership.sql"));
        await Execute(Database("ops"), ownership);

        var result = await migrator.MigrateAsync(Database("ops", "ops_owner"), all, CancellationToken.None);

        Assert.Equal(all.Where(m => m.Version > 6).Select(m => m.Version), result.Applied.Select(m => m.Version));
        await Execute(Database("ops", "research_app"), "SELECT count(*) FROM research.candles");
        var denied = await Assert.ThrowsAsync<PostgresException>(() => Execute(Database("ops", "research_app"), "SELECT count(*) FROM market.live_candles"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }
}
