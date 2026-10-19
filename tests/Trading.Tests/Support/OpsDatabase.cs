using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.Trading.Tests.Support;

/// <summary>
/// Throwaway PostgreSQL with all ops migrations and real per-role logins (m5-protocol §4.2), so grants are tested
/// as the applications will use them. Each test creates its own database from a migrated template.
/// </summary>
public sealed class OpsDatabase : IAsyncLifetime
{
    public static readonly string[] Roles = ["research_app", "trading_app", "ops_api", "market_import", "status_relay"];
    // Throwaway login secret of the test container, generated per run (no literal in the repository).
    private static readonly string LoginSecret = Guid.NewGuid().ToString("N");
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private int _next;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var template = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "ops_template", Pooling = false };
        await Admin($"CREATE DATABASE ops_template");
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(template.ConnectionString, SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly), CancellationToken.None);
        foreach (var role in Roles)
        {
            await Admin($"ALTER ROLE {role} LOGIN PASSWORD '{LoginSecret}'");
        }
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    public async Task<OpsDb> NewAsync()
    {
        var name = $"ops_{Interlocked.Increment(ref _next)}";
        await Admin($"CREATE DATABASE {name} TEMPLATE ops_template");
        // Database ACLs are not copied from a template: apply the migration's CONNECT rule again (H26).
        await Admin($"REVOKE CONNECT, TEMPORARY ON DATABASE {name} FROM PUBLIC");
        await Admin($"GRANT CONNECT ON DATABASE {name} TO {string.Join(", ", Roles)}");
        return new OpsDb(new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString);
    }

    private async Task Admin(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public sealed class OpsDb(string owner)
    {
        public string Owner { get; } = owner;

        public string As(string role) => new NpgsqlConnectionStringBuilder(Owner) { Username = role, Password = (LoginSecret) }.ConnectionString;

        public async Task ExecuteAsync(string sql, string? role = null, params object[] parameters)
        {
            await using var connection = new NpgsqlConnection(role is null ? Owner : As(role));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var p in parameters)
            {
                command.Parameters.AddWithValue(p);
            }

            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, string? role = null)
        {
            await using var connection = new NpgsqlConnection(role is null ? Owner : As(role));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>Kohort 0 (docs/cohorts/cohort-0.json) as cohort <paramref name="cohortId"/>, frozen and in PaperTrading.</summary>
        public async Task<OperationalManifest> SeedKohort0Async(long cohortId = 1, bool paper = true)
        {
            var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cohort-0.json"));
            var manifest = CohortManifestReader.Read(json);
            foreach (var member in manifest.Members)
            {
                await ExecuteAsync("INSERT INTO research.strategy_versions (name, definition_hash, definition) VALUES ($1, $2, $3::jsonb) ON CONFLICT DO NOTHING",
                    "research_app", member.Name, member.DefinitionHash, member.DefinitionJson);
                await ExecuteAsync("""
                    INSERT INTO research.frozen_strategies (cohort_id, manifest_hash, member_index, name, definition_hash, manifest)
                    VALUES ($1, $2, $3, $4, $5, $6)
                    """, "research_app", cohortId, manifest.Hash, member.Index, member.Name, member.DefinitionHash, json);
                var id = await ScalarAsync<long>($"SELECT id FROM research.frozen_strategies WHERE cohort_id = {cohortId} AND member_index = {member.Index}");
                await ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'Candidate', 'Frozen', 'test', '{}')", "research_app", id);
                if (paper)
                {
                    await ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'Frozen', 'PaperTrading', 'test', '{\"parityReportHash\": \"x\"}')", "research_app", id);
                }
            }

            return manifest;
        }

        public async Task AddCandlesAsync(IEnumerable<Candle> candles)
        {
            await using var connection = new NpgsqlConnection(Owner);
            await connection.OpenAsync();
            await using var writer = await connection.BeginBinaryImportAsync(
                "COPY market.live_candles (symbol, candle_interval, open_time, open, high, low, close, base_volume, quote_volume, trade_count) FROM STDIN (FORMAT BINARY)");
            foreach (var c in candles)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(c.Symbol, NpgsqlDbType.Text);
                await writer.WriteAsync(c.Interval.Code, NpgsqlDbType.Text);
                await writer.WriteAsync(c.OpenTime, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(c.Open, NpgsqlDbType.Numeric);
                await writer.WriteAsync(c.High, NpgsqlDbType.Numeric);
                await writer.WriteAsync(c.Low, NpgsqlDbType.Numeric);
                await writer.WriteAsync(c.Close, NpgsqlDbType.Numeric);
                await writer.WriteAsync(c.BaseVolume, NpgsqlDbType.Numeric);
                await writer.WriteAsync(c.QuoteVolume, NpgsqlDbType.Numeric);
                await writer.WriteAsync(c.TradeCount, NpgsqlDbType.Bigint);
            }

            await writer.CompleteAsync();
        }
    }
}

/// <summary>Seeded synthetic candles after the research cutoff (no real market data).</summary>
public static class LiveSynthetic
{
    public static List<Candle> Walk(string symbol, DateTimeOffset start, int bars, int seed)
    {
        var random = new Random(seed);
        var candles = new List<Candle>(bars);
        var close = 100m;
        for (var i = 0; i < bars; i++)
        {
            var open = close;
            close = Math.Max(1m, open * (1 + (decimal)((random.NextDouble() - 0.5) * 0.06)));
            var high = Math.Max(open, close) * (1 + (decimal)(random.NextDouble() * 0.02));
            var low = Math.Min(open, close) * (1 - (decimal)(random.NextDouble() * 0.02));
            candles.Add(new Candle(symbol, CandleInterval.FourHours, start.AddHours(4 * i), Math.Round(open, 6), Math.Round(high, 6) + 0.000001m,
                Math.Round(low, 6) - 0.000001m, Math.Round(close, 6), 1, close, 1));
        }

        return candles;
    }

    public static string DefinitionHash(string json) => DslStrategy.FromJson(json).DefinitionHash;
}
