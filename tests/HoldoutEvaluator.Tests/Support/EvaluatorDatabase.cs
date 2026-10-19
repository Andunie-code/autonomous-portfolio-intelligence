using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.HoldoutEvaluator.Tests.Support;

/// <summary>
/// One throwaway PostgreSQL (requires Docker) with the ops database migrated once and a fresh synthetic holdout
/// database per test (limits are global per database, so tests must not share one).
/// </summary>
public sealed class EvaluatorDatabase : IAsyncLifetime
{
    public const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000001";
    public const string EngineSha = "0123456789abcdef0123456789abcdef01234567";

    // Throwaway login secret of the test container, generated per run (no literal in the repository).
    private static readonly string LoginSecret = Guid.NewGuid().ToString("N");
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private int _databases;

    public string ServerConnectionString => _postgres.GetConnectionString();

    public string OpsConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        OpsConnectionString = await CreateDatabaseAsync("ops");
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance)
            .MigrateAsync(OpsConnectionString, SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly), CancellationToken.None);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    /// <summary>A new holdout_synth database with the holdout migrations and one evaluator release.</summary>
    public async Task<HoldoutDatabase> NewHoldoutAsync()
    {
        var connection = await CreateDatabaseAsync($"holdout_synth_{Interlocked.Increment(ref _databases)}");
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance)
            .MigrateAsync(connection, SqlMigrator.LoadEmbedded(typeof(HoldoutDatabase).Assembly), CancellationToken.None);
        await ExecuteAsync(connection, $"INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ('{Digest}', '{EngineSha}')");
        return new HoldoutDatabase(connection, OpsConnectionString);
    }

    /// <summary>A holdout database with an exact name (holdout_henv for the self-test) and optional real role logins.</summary>
    public async Task<HoldoutDatabase> NewNamedHoldoutAsync(string name, bool logins = false)
    {
        var connection = await CreateDatabaseAsync(name);
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance)
            .MigrateAsync(connection, SqlMigrator.LoadEmbedded(typeof(HoldoutDatabase).Assembly), CancellationToken.None);
        await ExecuteAsync(connection, $"INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ('{Digest}', '{EngineSha}')");
        var roles = new Dictionary<string, string>();
        if (logins)
        {
            foreach (var role in new[] { "eval_registry", "eval_diagnostic", "eval_prospective" })
            {
                await ExecuteAsync(ServerConnectionString, $"ALTER ROLE {role} LOGIN PASSWORD '{LoginSecret}'");
                roles[role] = new NpgsqlConnectionStringBuilder(connection) { Username = role, Password = (LoginSecret) }.ConnectionString;
            }
        }

        return new HoldoutDatabase(connection, OpsConnectionString, roles);
    }

    public static async Task ExecuteAsync(string connectionString, string sql, string? role = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        if (role is not null)
        {
            await using var set = new NpgsqlCommand($"SET ROLE {role}", connection);
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> CreateDatabaseAsync(string name)
    {
        await ExecuteAsync(ServerConnectionString, $"CREATE DATABASE {name}");
        return new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = name }.ConnectionString;
    }
}

/// <summary>Manifest builder for tests (schema v1, m3-protocol §9).</summary>
public static class Manifests
{
    public const string Trend = """
        { "dsl": "1", "name": "trend", "interval": "4h",
          "indicators": [ { "id": "f", "type": "sma", "params": { "period": 5 } }, { "id": "s", "type": "sma", "params": { "period": 30 } } ],
          "entry": { "op": "gt", "args": [ { "ref": "f.value" }, { "ref": "s.value" } ] },
          "exit": { "op": "lt", "args": [ { "ref": "f.value" }, { "ref": "s.value", "mult": 0.9 } ] } }
        """;

    /// <summary>Structurally distinct strategies (different indicator types), one family each.</summary>
    public static string Family(int i)
    {
        string[] types = ["sma", "ema", "rsi", "roc", "volume_sma", "atr", "adx", "macd", "bollinger", "donchian", "supertrend"];
        var type = types[i % types.Length];
        var (parameters, output, right) = type switch
        {
            "macd" => ("{ \"fast\": 5, \"slow\": 12, \"signal\": 4 }", "line", "{ \"const\": 0 }"),
            "adx" => ("{ \"period\": 7 }", "adx", "{ \"const\": 10 }"),
            "bollinger" => ("{ \"period\": 10, \"k\": 2 }", "middle", "{ \"price\": \"low\" }"),
            "donchian" => ("{ \"period\": 10 }", "middle", "{ \"price\": \"low\" }"),
            "supertrend" => ("{ \"period\": 7, \"multiplier\": 2 }", "dir", "{ \"const\": 0 }"),
            "rsi" => ("{ \"period\": 7 }", "value", "{ \"const\": 30 }"),
            "roc" => ("{ \"period\": 7 }", "value", "{ \"const\": -1 }"),
            "atr" => ("{ \"period\": 7 }", "value", "{ \"const\": 0 }"),
            "volume_sma" => ("{ \"period\": 7 }", "value", "{ \"const\": 0 }"),
            _ => ("{ \"period\": 7 }", "value", "{ \"price\": \"low\", \"mult\": 0.5 }"),
        };
        return $$"""
            { "dsl": "1", "name": "family-{{i}}", "interval": "4h",
              "indicators": [ { "id": "x", "type": "{{type}}", "params": {{parameters}} } ],
              "entry": { "op": "gt", "args": [ { "ref": "x.{{output}}" }, {{right}} ] },
              "exit": { "op": "lt", "args": [ { "price": "close" }, { "const": 0.001 } ] } }
            """;
    }

    public static string Build(
        IEnumerable<string> definitions,
        int months = 6,
        int plannedTrades = 1,
        bool underpowered = false,
        double researchMetric = 0,
        Action<JsonObject>? edit = null,
        string engineSha = EvaluatorDatabase.EngineSha,
        int seed = 7)
    {
        var root = new JsonObject
        {
            ["manifestVersion"] = "1",
            ["protocol"] = new JsonObject
            {
                ["version"] = "m3-protocol v1",
                ["commit"] = "9928531",
                ["alpha"] = 0.05,
                ["power"] = 0.80,
                ["deltaFloor"] = 0.05,
                ["deltaShrink"] = 0.50,
                ["deltaCap"] = 0.15,
                ["deltaMode"] = "a",
                ["maxDrawdown"] = 0.25,
                ["bootstrapReplicates"] = 2000,
                ["bootstrapSeed"] = seed,
                ["primaryMetric"] = "annualised-log-return",
                ["benchmark"] = "cash",
            },
            ["strategies"] = new JsonArray(definitions.Select(d => (JsonNode)new JsonObject
            {
                ["definition"] = JsonNode.Parse(d),
                ["researchMetric"] = researchMetric,
            }).ToArray()),
            ["pairs"] = new JsonArray(new JsonObject { ["symbol"] = "AAAUSDT", ["category"] = "core" }, new JsonObject { ["symbol"] = "BBBUSDT", ["category"] = "core" }),
            ["interval"] = "4h",
            ["dataSource"] = "synthetic",
            ["checksumPolicy"] = "sha256-required",
            ["engineSha"] = engineSha,
            ["imageDigest"] = EvaluatorDatabase.Digest,
            ["costModel"] = "cost-model v1",
            ["allocation"] = "allocation v1",
            ["risk"] = new JsonObject
            {
                ["maxOpenPositions"] = 5, ["maxAssetWeight"] = 0.20, ["maxTotalExposure"] = 0.80,
                ["dailyLossLimit"] = 0.03, ["maxDrawdownHalt"] = 0.15, ["maxPositionLoss"] = 0.10,
            },
            ["initialCapital"] = 10000,
            ["power"] = new JsonObject { ["months"] = months, ["plannedTrades"] = plannedTrades, ["underpowered"] = underpowered },
            ["dataQuality"] = "m1-data-policy v1.2 §6",
        };
        edit?.Invoke(root);
        return root.ToJsonString();
    }
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
