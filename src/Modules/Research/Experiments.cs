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
}
