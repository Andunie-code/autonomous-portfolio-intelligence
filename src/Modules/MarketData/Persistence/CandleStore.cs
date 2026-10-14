using Npgsql;
using NpgsqlTypes;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData.Persistence;

public enum ImportOutcome
{
    Imported,
    AlreadyImported,
}

/// <summary>Raised when an already imported archive file now has a different checksum (republished file).</summary>
public sealed class SourceChangedException(string message) : Exception(message);

/// <summary>
/// Writes candles of one archive file atomically into <c>market.candles</c> (migration 0002). Idempotent per
/// archive file; a duplicate candle across files and any cutoff CHECK violation reject the whole file.
/// </summary>
public sealed class CandleStore(string connectionString)
{
    public async Task<ImportOutcome> ImportAsync(ArchiveFileKey key, string sha256, long sizeBytes, IReadOnlyList<Candle> candles, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var existing = new NpgsqlCommand("SELECT sha256 FROM market.source_files WHERE archive_key = $1", connection, transaction))
        {
            existing.Parameters.AddWithValue(key.Path);
            if (await existing.ExecuteScalarAsync(cancellationToken) is string previous)
            {
                if (!string.Equals(previous, sha256, StringComparison.Ordinal))
                {
                    throw new SourceChangedException($"{key.FileName} was imported before with a different checksum.");
                }

                return ImportOutcome.AlreadyImported;
            }
        }

        long sourceId;
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO market.source_files (archive_key, sha256, size_bytes, row_count) VALUES ($1, $2, $3, $4) RETURNING id",
            connection, transaction))
        {
            insert.Parameters.AddWithValue(key.Path);
            insert.Parameters.AddWithValue(sha256);
            insert.Parameters.AddWithValue(sizeBytes);
            insert.Parameters.AddWithValue(candles.Count);
            sourceId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        try
        {
            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY market.candles (symbol, candle_interval, open_time, open, high, low, close, base_volume, quote_volume, trade_count, source_file_id) FROM STDIN (FORMAT BINARY)",
                cancellationToken))
            {
                foreach (var c in candles)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(c.Symbol, NpgsqlDbType.Text, cancellationToken);
                    await writer.WriteAsync(c.Interval.Code, NpgsqlDbType.Text, cancellationToken);
                    await writer.WriteAsync(c.OpenTime, NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(c.Open, NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(c.High, NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(c.Low, NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(c.Close, NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(c.BaseVolume, NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(c.QuoteVolume, NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(c.TradeCount, NpgsqlDbType.Bigint, cancellationToken);
                    await writer.WriteAsync(sourceId, NpgsqlDbType.Bigint, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return ImportOutcome.Imported;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation)
        {
            if (ex.ConstraintName == "candles_before_research_cutoff")
            {
                throw new CutoffViolationException($"{key.FileName}: the database rejected a candle closing after the research cutoff.");
            }

            throw new ArchiveDataException($"{key.FileName}: rejected by the database ({ex.SqlState} {ex.ConstraintName}).");
        }
    }

    /// <summary>SHA-256 recorded for an already imported archive file, or null.</summary>
    public async Task<string?> ImportedShaAsync(ArchiveFileKey key, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT sha256 FROM market.source_files WHERE archive_key = $1", connection);
        command.Parameters.AddWithValue(key.Path);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<IReadOnlyList<Candle>> ReadAsync(string symbol, CandleInterval interval, TimeRange range, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT open_time, open, high, low, close, base_volume, quote_volume, trade_count
            FROM market.candles
            WHERE symbol = $1 AND candle_interval = $2 AND open_time >= $3 AND open_time < $4
            ORDER BY open_time
            """, connection);
        command.Parameters.AddWithValue(symbol);
        command.Parameters.AddWithValue(interval.Code);
        command.Parameters.AddWithValue(range.Start);
        command.Parameters.AddWithValue(range.End);

        var candles = new List<Candle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var open = new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero);
            candles.Add(new Candle(symbol, interval, open, reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetInt64(7)));
        }

        return candles.Where(c => range.ContainsCandle(c.OpenTime, interval)).ToList();
    }

    /// <summary>H1: number of stored candles that close after the research cutoff (must be 0).</summary>
    public async Task<long> CountCutoffViolationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM market.candles WHERE market.candle_close_time(open_time, candle_interval) > market.research_cutoff()",
            connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<long> SaveSnapshotAsync(string policyVersion, string contentHash, string manifestJson, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "INSERT INTO market.dataset_snapshots (policy_version, content_hash, manifest) VALUES ($1, $2, $3::jsonb) RETURNING id",
            connection);
        command.Parameters.AddWithValue(policyVersion);
        command.Parameters.AddWithValue(contentHash);
        command.Parameters.AddWithValue(manifestJson);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
