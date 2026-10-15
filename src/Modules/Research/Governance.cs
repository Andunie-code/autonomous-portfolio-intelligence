using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.Research;

/// <summary>Append-only audit trail (audit.events, migration 0005). The payload hash is SHA-256 of the canonical JSON.</summary>
public sealed class AuditLog(string connectionString)
{
    public async Task<long> RecordAsync(string actor, string @event, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ($1, $2, $3::jsonb, $4) RETURNING id", connection);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(@event);
        command.Parameters.AddWithValue(json);
        command.Parameters.AddWithValue(DslCanonical.Sha256(json));
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}

public sealed record DatasetEntry(string Symbol, string Interval, DateTimeOffset Start, DateTimeOffset End, long Candles);

/// <summary>Dataset catalog for research (H7): only the research window; nothing at or after C is ever listed.</summary>
public sealed class DatasetCatalog(string connectionString)
{
    public async Task<IReadOnlyList<DatasetEntry>> GetAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT symbol, candle_interval, min(open_time), max(market.candle_close_time(open_time, candle_interval)), count(*)
            FROM market.candles
            WHERE market.candle_close_time(open_time, candle_interval) <= market.research_cutoff()
            GROUP BY symbol, candle_interval
            ORDER BY symbol, candle_interval
            """, connection);
        var result = new List<DatasetEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var end = reader.GetFieldValue<DateTimeOffset>(3);
            if (end > ResearchCutoff.C)
            {
                throw new InvalidOperationException("Catalog row past the research cutoff.");
            }

            result.Add(new DatasetEntry(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), end, reader.GetInt64(4)));
        }

        return result;
    }
}

public sealed class ResearchRequestRejectedException(string message) : Exception(message);

/// <summary>
/// Guard for experiment and tuning requests (H8): a requested range reaching past the research cutoff C is
/// rejected before any data is read, and the rejection is audited.
/// </summary>
public sealed class ExperimentRequestGuard(AuditLog audit)
{
    public async Task EnsureAllowedAsync(string actor, string requestKind, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (end <= ResearchCutoff.C && start < end)
        {
            return;
        }

        await audit.RecordAsync(actor, "research-request-rejected", new { requestKind, start, end, cutoff = ResearchCutoff.C }, ct);
        throw new ResearchRequestRejectedException($"{requestKind}: range [{start:O}, {end:O}) reaches past the research cutoff {ResearchCutoff.C:O}.");
    }
}
