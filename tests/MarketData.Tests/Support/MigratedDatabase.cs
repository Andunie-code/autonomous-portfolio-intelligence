using Microsoft.Extensions.Logging.Abstractions;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.MarketData.Tests.Support;

/// <summary>Throwaway PostgreSQL with all embedded migrations applied (requires Docker).</summary>
public sealed class MigratedDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var migrator = new SqlMigrator(NullLogger<SqlMigrator>.Instance);
        await migrator.MigrateAsync(ConnectionString, SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly), CancellationToken.None);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}
