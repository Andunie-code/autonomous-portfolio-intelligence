using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData.Live;

/// <summary>
/// market.live_candles (migration 0007): append-only. A candle already stored keeps its first value; a later
/// different value is recorded in market.live_candle_anomalies (m5-protocol §2.3) and reported in L3.
/// </summary>
public sealed class LiveCandleStore(string connectionString)
{
    public async Task<(int Inserted, int Anomalies)> AddAsync(IReadOnlyList<Candle> candles, CancellationToken ct)
    {
        int inserted = 0, anomalies = 0;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var c in candles)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO market.live_candles (symbol, candle_interval, open_time, open, high, low, close, base_volume, quote_volume, trade_count)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue(c.Symbol);
            insert.Parameters.AddWithValue(c.Interval.Code);
            insert.Parameters.AddWithValue(c.OpenTime);
            insert.Parameters.AddWithValue(c.Open);
            insert.Parameters.AddWithValue(c.High);
            insert.Parameters.AddWithValue(c.Low);
            insert.Parameters.AddWithValue(c.Close);
            insert.Parameters.AddWithValue(c.BaseVolume);
            insert.Parameters.AddWithValue(c.QuoteVolume);
            insert.Parameters.AddWithValue(c.TradeCount);
            if (await insert.ExecuteNonQueryAsync(ct) == 1)
            {
                inserted++;
                continue;
            }

            decimal[] stored;
            await using (var existing = new NpgsqlCommand(
                "SELECT open, high, low, close, base_volume, quote_volume, trade_count FROM market.live_candles WHERE symbol = $1 AND candle_interval = $2 AND open_time = $3",
                connection, transaction))
            {
                existing.Parameters.AddWithValue(c.Symbol);
                existing.Parameters.AddWithValue(c.Interval.Code);
                existing.Parameters.AddWithValue(c.OpenTime);
                await using var reader = await existing.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                stored = [reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetInt64(6)];
            }

            decimal[] incoming = [c.Open, c.High, c.Low, c.Close, c.BaseVolume, c.QuoteVolume, c.TradeCount];
            if (stored.SequenceEqual(incoming))
            {
                continue;
            }

            anomalies++;
            await using var anomaly = new NpgsqlCommand(
                "INSERT INTO market.live_candle_anomalies (symbol, candle_interval, open_time, kind, detail) VALUES ($1, $2, $3, 'changed-after-store', $4::jsonb)",
                connection, transaction);
            anomaly.Parameters.AddWithValue(c.Symbol);
            anomaly.Parameters.AddWithValue(c.Interval.Code);
            anomaly.Parameters.AddWithValue(c.OpenTime);
            anomaly.Parameters.AddWithValue(JsonSerializer.Serialize(new { stored, incoming }));
            await anomaly.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return (inserted, anomalies);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Candle>>> ReadAsync(IReadOnlyList<string> symbols, CandleInterval interval, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var result = symbols.ToDictionary(s => s, _ => new List<Candle>());
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT symbol, open_time, open, high, low, close, base_volume, quote_volume, trade_count FROM market.live_candles
            WHERE symbol = ANY($1) AND candle_interval = $2 AND open_time >= $3 AND open_time < $4
            ORDER BY symbol, open_time
            """, connection);
        command.Parameters.AddWithValue(symbols.ToArray());
        command.Parameters.AddWithValue(interval.Code);
        command.Parameters.AddWithValue(from);
        command.Parameters.AddWithValue(to);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var symbol = reader.GetString(0);
            result[symbol].Add(new Candle(symbol, interval, reader.GetFieldValue<DateTimeOffset>(1), reader.GetDecimal(2), reader.GetDecimal(3),
                reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetInt64(8)));
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Candle>)kv.Value);
    }

    public async Task<DateTimeOffset?> LastOpenAsync(string symbol, CandleInterval interval, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT max(open_time) FROM market.live_candles WHERE symbol = $1 AND candle_interval = $2", connection);
        command.Parameters.AddWithValue(symbol);
        command.Parameters.AddWithValue(interval.Code);
        return await command.ExecuteScalarAsync(ct) is DateTime value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
    }
}
