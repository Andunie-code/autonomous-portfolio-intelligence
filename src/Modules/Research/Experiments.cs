using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Research;

public sealed record CoreUniverse(string Version, string SnapshotHash, IReadOnlyDictionary<string, int> Ranks)
{
    public static CoreUniverse LoadDefault()
    {
        var assembly = typeof(CoreUniverse).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Config.core-universe.v1.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return new CoreUniverse(
            root.GetProperty("version").GetString()!,
            root.GetProperty("snapshotHash").GetString()!,
            root.GetProperty("pairs").EnumerateArray().ToDictionary(p => p.GetProperty("symbol").GetString()!, p => p.GetProperty("rank").GetInt32()));
    }
}

public sealed record ExperimentRecord(
    IStrategy Strategy,
    string Variant,
    string ProtocolVersion,
    string ProtocolCommit,
    string DatasetSnapshotHash,
    string EngineGitSha,
    string ResultHash,
    PerformanceMetrics Metrics)
{
    public string StrategyHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Strategy.Definition))).ToLowerInvariant();
}

/// <summary>Append-only experiment registry (migration 0003; docs/m2-protocol.md §12).</summary>
public sealed class ExperimentStore(string connectionString)
{
    public async Task<long> AddAsync(ExperimentRecord record, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO research.experiments (strategy_name, strategy_version, strategy_hash, variant, protocol_version,
                protocol_commit, dataset_snapshot_hash, engine_git_sha, result_hash, metrics)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb) RETURNING id
            """, connection);
        command.Parameters.AddWithValue(record.Strategy.Name);
        command.Parameters.AddWithValue(record.Strategy.Version);
        command.Parameters.AddWithValue(record.StrategyHash);
        command.Parameters.AddWithValue(record.Variant);
        command.Parameters.AddWithValue(record.ProtocolVersion);
        command.Parameters.AddWithValue(record.ProtocolCommit);
        command.Parameters.AddWithValue(record.DatasetSnapshotHash);
        command.Parameters.AddWithValue(record.EngineGitSha);
        command.Parameters.AddWithValue(record.ResultHash);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(record.Metrics));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Experiments whose strategy name contains <paramref name="query"/> (case-insensitive), newest first.</summary>
    public async Task<IReadOnlyList<ExperimentSummary>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {SummaryColumns} FROM research.experiments
            WHERE strategy_name ILIKE '%' || $1 || '%' ORDER BY id DESC LIMIT $2
            """, connection);
        command.Parameters.AddWithValue(query.Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal));
        command.Parameters.AddWithValue(limit);
        return await ReadAsync(command, cancellationToken);
    }

    public async Task<ExperimentSummary?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {SummaryColumns} FROM research.experiments WHERE id = $1", connection);
        command.Parameters.AddWithValue(id);
        return (await ReadAsync(command, cancellationToken)).SingleOrDefault();
    }

    private const string SummaryColumns = "id, created_at, strategy_name, strategy_hash, variant, protocol_version, dataset_snapshot_hash, result_hash, metrics::text";

    private static async Task<IReadOnlyList<ExperimentSummary>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<ExperimentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ExperimentSummary(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
        }

        return result;
    }
}

/// <summary>A recorded experiment as the Researcher sees it: metrics only, never raw data.</summary>
public sealed record ExperimentSummary(
    long Id,
    DateTimeOffset CreatedAt,
    string StrategyName,
    string StrategyHash,
    string Variant,
    string ProtocolVersion,
    string DatasetSnapshotHash,
    string ResultHash,
    string MetricsJson);
