using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Live;

namespace PortfolioIntelligence.Execution.Radar;

/// <summary>
/// Loads the radar's inputs as ops_api (live candles, frozen strategies in PaperTrading or TestnetActive, latest paper positions) and keeps
/// the last report: it is recomputed only when a new bar, strategy state or position appears (ADR-0020 §5).
/// Computed on request, outside the trading cycle.
/// </summary>
public sealed class RadarService(string connectionString, IReadOnlyList<string> watchSymbols)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Key, string Json)? _last;

    public async Task<string> ReportAsync(DateTimeOffset now, CancellationToken ct)
    {
        var strategies = await StrategiesAsync(ct);
        var positions = await PositionsAsync(ct);
        var watch = watchSymbols.ToHashSet(StringComparer.Ordinal);
        var symbols = strategies.SelectMany(s => s.Symbols).Concat(watch).Append(SignalRadar.ReferenceSymbol).Distinct().Order(StringComparer.Ordinal).ToList();
        // The window ends at the latest stored bar (not at the clock), so a data outage shows as an old asOf, not as blanks.
        var length = CandleInterval.FourHours.FixedLength!.Value;
        var store = new LiveCandleStore(connectionString);
        DateTimeOffset? latest = null;
        foreach (var symbol in symbols)
        {
            if (await store.LastOpenAsync(symbol, CandleInterval.FourHours, ct) is { } last && (latest is null || last > latest))
            {
                latest = last;
            }
        }

        var end = latest is { } l && l + length <= now ? l + length : now;
        var candles = await store.ReadAsync(symbols, CandleInterval.FourHours, end - length * (SignalRadar.Bars + 6), end, ct);
        var key = string.Join('|', candles.Select(kv => $"{kv.Key}:{kv.Value.Count}:{(kv.Value.Count > 0 ? kv.Value[^1].OpenTime.UtcTicks : 0)}")
            .Concat(strategies.Select(s => $"{s.CohortId}:{s.Name}"))
            .Concat(positions.Select(p => $"{p.Strategy}:{p.Symbol}:{p.Stop}")));

        await _gate.WaitAsync(ct);
        try
        {
            if (_last is { } cached && cached.Key == key)
            {
                return cached.Json;
            }

            var json = JsonSerializer.Serialize(SignalRadar.Compute(candles, strategies, positions, watch), PaperJson.Options);
            _last = (key, json);
            return json;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Members that are trading (PaperTrading or TestnetActive, <see cref="PaperTrader.PaperStates"/>: a TestnetActive
    /// strategy keeps its paper run), from their frozen manifests (definition hashes verified by the reader).
    /// </summary>
    private async Task<IReadOnlyList<RadarStrategy>> StrategiesAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT f.cohort_id, f.member_index, f.manifest
            FROM research.frozen_strategies f
            WHERE coalesce((SELECT l.to_state FROM research.lifecycle_events l WHERE l.frozen_id = f.id ORDER BY l.id DESC LIMIT 1), 'Candidate') = ANY($1)
            ORDER BY f.cohort_id, f.member_index
            """, connection);
        command.Parameters.AddWithValue(PaperTrader.PaperStates.Order(StringComparer.Ordinal).ToArray());
        var result = new List<RadarStrategy>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            try
            {
                var manifest = CohortManifestReader.Read(reader.GetString(2));
                var member = manifest.Members.Single(m => m.Index == reader.GetInt32(1));
                result.Add(new RadarStrategy(member.Name, reader.GetInt64(0), member.DefinitionJson, manifest.Pairs.Select(p => p.Symbol).ToList()));
            }
            catch (FormatException)
            {
                // A manifest that does not verify is not shown (Trading raises the hash alarm).
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<RadarPosition>> PositionsAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT s.value ->> 'name', p ->> 'symbol', (p ->> 'entryPrice')::numeric, (p ->> 'stop')::numeric
            FROM trading.balance_snapshots b
            JOIN trading.paper_runs r ON r.id = b.run_id
            CROSS JOIN LATERAL jsonb_array_elements(r.config -> 'sleeves') WITH ORDINALITY AS s(value, ordinality)
            CROSS JOIN LATERAL jsonb_array_elements(b.positions) AS p
            WHERE r.venue = 'paper' -- the paper run is the radar's reference; a Testnet run of the same strategy would duplicate it
              AND s.ordinality - 1 = b.sleeve AND b.bar_time = (SELECT max(bar_time) FROM trading.balance_snapshots WHERE run_id = b.run_id)
            ORDER BY 1, 2
            """, connection);
        var result = new List<RadarPosition>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RadarPosition(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3)));
        }

        return result;
    }
}
