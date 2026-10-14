using Npgsql;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Persistence;
using PortfolioIntelligence.MarketData.Tests.Support;
using static PortfolioIntelligence.MarketData.Tests.Support.SyntheticKlines;

namespace PortfolioIntelligence.MarketData.Tests;

/// <summary>H1, H1c, H1d and H3 against real PostgreSQL; idempotent import (roadmap M1 criteria 1, 3, 4, 7).</summary>
public class CandleStoreTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    private readonly CandleStore _store = new(database.ConnectionString);

    [Fact]
    public async Task Database_cutoff_equals_the_code_constant()
    {
        Assert.Equal(ResearchCutoff.C, new DateTimeOffset(await ScalarAsync<DateTime>("SELECT market.research_cutoff()"), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Istanbul")]
    [InlineData("America/New_York")]
    public async Task Check_constraint_uses_utc_whatever_the_session_time_zone(string timeZone)
    {
        // Allowed: closes exactly at C. Forbidden: closes after C (weekly 2025-12-29, hourly 2026-01-01T00:00).
        Assert.True(await InsertRawAsync(timeZone, "1h", "2025-12-31 23:00:00+00"));
        Assert.True(await InsertRawAsync(timeZone, "1M", "2025-12-01 00:00:00+00"));
        Assert.False(await InsertRawAsync(timeZone, "1h", "2026-01-01 00:00:00+00"));
        Assert.False(await InsertRawAsync(timeZone, "1w", "2025-12-29 00:00:00+00"));
        Assert.False(await InsertRawAsync(timeZone, "1d", "2026-01-01 00:00:00+00"));
        Assert.Equal(0, await _store.CountCutoffViolationsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Import_is_idempotent_and_a_changed_checksum_is_refused()
    {
        var key = new ArchiveFileKey("IDEMUSDT", CandleInterval.OneDay, 2023, 3);
        var candles = KlineCsvParser.Parse(SyntheticKlines.Lines(key), key);
        var sha = new string('a', 64);

        Assert.Equal(ImportOutcome.Imported, await _store.ImportAsync(key, sha, 10, candles, CancellationToken.None));
        Assert.Equal(ImportOutcome.AlreadyImported, await _store.ImportAsync(key, sha, 10, candles, CancellationToken.None));
        await Assert.ThrowsAsync<SourceChangedException>(() => _store.ImportAsync(key, new string('b', 64), 10, candles, CancellationToken.None));
        Assert.Equal(31, (await _store.ReadAsync("IDEMUSDT", CandleInterval.OneDay, new TimeRange(Utc(2023, 1, 1), Utc(2024, 1, 1)), CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Duplicate_candle_from_another_file_rejects_that_file_entirely()
    {
        var march = new ArchiveFileKey("DUPUSDT", CandleInterval.OneDay, 2023, 3);
        var april = new ArchiveFileKey("DUPUSDT", CandleInterval.OneDay, 2023, 4);
        var marchCandles = KlineCsvParser.Parse(SyntheticKlines.Lines(march), march);
        var aprilCandles = KlineCsvParser.Parse(SyntheticKlines.Lines(april), april).Append(marchCandles[^1]).ToList();

        await _store.ImportAsync(march, new string('c', 64), 1, marchCandles, CancellationToken.None);
        await Assert.ThrowsAsync<ArchiveDataException>(() => _store.ImportAsync(april, new string('d', 64), 1, aprilCandles, CancellationToken.None));

        var april2023 = await _store.ReadAsync("DUPUSDT", CandleInterval.OneDay, new TimeRange(Utc(2023, 4, 1), Utc(2023, 5, 1)), CancellationToken.None);
        Assert.Empty(april2023);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM market.source_files WHERE archive_key LIKE '%DUPUSDT-1d-2023-04%'"));
    }

    [Fact]
    public async Task Store_rejects_a_candle_closing_after_the_cutoff_even_if_the_parser_were_bypassed()
    {
        var key = new ArchiveFileKey("CUTUSDT", CandleInterval.OneHour, 2025, 12);
        var bad = new Candle("CUTUSDT", CandleInterval.OneHour, ResearchCutoff.C, 1, 1, 1, 1, 1, 1, 1);

        await Assert.ThrowsAsync<CutoffViolationException>(() => _store.ImportAsync(key, new string('e', 64), 1, [bad], CancellationToken.None));
    }

    private async Task<bool> InsertRawAsync(string timeZone, string interval, string openTime)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var tz = new NpgsqlCommand($"SET TIME ZONE '{timeZone}'", connection))
        {
            await tz.ExecuteNonQueryAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"""
            WITH f AS (
                INSERT INTO market.source_files (archive_key, sha256, size_bytes, row_count)
                VALUES ('raw-{Guid.NewGuid():N}', '{new string('f', 64)}', 0, 1) RETURNING id)
            INSERT INTO market.candles (symbol, candle_interval, open_time, open, high, low, close, base_volume, quote_volume, trade_count, source_file_id)
            SELECT 'RAWUSDT', '{interval}', timestamptz '{openTime}', 1, 1, 1, 1, 0, 0, 0, id FROM f
            """, connection, transaction);
        try
        {
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == "candles_before_research_cutoff")
        {
            return false;
        }
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
