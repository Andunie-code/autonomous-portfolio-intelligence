using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution;

public sealed record FrozenRecord(long Id, long CohortId, int MemberIndex, string Name, string DefinitionHash, string ManifestHash, string Manifest, string State);

public sealed record CycleMetric(DateTimeOffset ScheduledFor, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int BarsProcessed, bool CatchUp, bool Stale, string Outcome, object Detail);

/// <summary>An operational action; a position close names its run (sleeve indices are per run, ADR-0019 §3).</summary>
public sealed record OpsAction(long Id, string Action, long? RunId, int? Sleeve, string? Symbol);

public sealed record CorrelationSnapshot(DateTimeOffset BarTime, double Threshold, IReadOnlyDictionary<string, int> Clusters, double EffectiveCount, double MaxClusterShare);

/// <summary>A step to persist: inputs, resulting state hash, its events and fills, balances and correlation.</summary>
public sealed record StepWrite(
    DateTimeOffset BarTime,
    StepInputs Inputs,
    string StateHash,
    IReadOnlyList<EngineEvent> Events,
    IReadOnlyList<FillRecord> Fills,
    IReadOnlyList<(int Sleeve, decimal Cash, decimal Equity, object Positions)> Balances,
    CorrelationSnapshot? Correlation);

/// <summary>What <see cref="PaperTrader"/> needs from storage: PostgreSQL in production, memory for parity replay.</summary>
public interface IPaperStore
{
    Task<IReadOnlyList<FrozenRecord>> FrozenAsync(CancellationToken ct);

    Task<string?> DefinitionAsync(string definitionHash, CancellationToken ct);

    /// <summary>Every paper run, oldest first (one per cohort since ADR-0019).</summary>
    Task<IReadOnlyList<PaperRun>> RunsAsync(CancellationToken ct);

    Task<PaperRun> CreateRunAsync(DateTimeOffset tradingStart, DateTimeOffset warmupStart, PaperRunConfig config, CancellationToken ct);

    Task<IReadOnlyList<StepRecord>> StepsAsync(long runId, CancellationToken ct);

    Task<IReadOnlyDictionary<int, (long Fills, decimal Cash)>> LedgerAsync(long runId, CancellationToken ct);

    Task<bool> KillSwitchEngagedAsync(CancellationToken ct);

    Task<IReadOnlyList<OpsAction>> ActionsAfterAsync(long lastId, CancellationToken ct);

    /// <summary>The id of the latest operational action (0 if none): a new run applies only later ones.</summary>
    Task<long> LastActionIdAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<long, RiskConfigDocument>> RiskVersionsAsync(CancellationToken ct);

    Task AddStepAsync(PaperRun run, StepWrite step, CancellationToken ct);

    Task AddReconciliationAsync(long runId, DateTimeOffset? barTime, bool ok, object detail, CancellationToken ct);

    Task AuditAsync(string actor, string @event, object payload, CancellationToken ct);
}

/// <summary>Npgsql access to the paper-trading tables (migration 0007), as trading_app. Everything is append-only.</summary>
public sealed class PaperStore(string connectionString) : IPaperStore
{
    public string ConnectionString { get; } = connectionString;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>Frozen strategies with their current operational state (latest lifecycle event).</summary>
    public async Task<IReadOnlyList<FrozenRecord>> FrozenAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT f.id, f.cohort_id, f.member_index, f.name, f.definition_hash, f.manifest_hash, f.manifest,
                   coalesce((SELECT l.to_state FROM research.lifecycle_events l WHERE l.frozen_id = f.id ORDER BY l.id DESC LIMIT 1), 'Candidate')
            FROM research.frozen_strategies f ORDER BY f.cohort_id, f.member_index
            """, connection);
        var result = new List<FrozenRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FrozenRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }

        return result;
    }

    public async Task<string?> DefinitionAsync(string definitionHash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT definition::text FROM research.strategy_versions WHERE definition_hash = $1", connection);
        command.Parameters.AddWithValue(definitionHash);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<PaperRun>> RunsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id, trading_start, warmup_start, config::text, config_hash FROM trading.paper_runs ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<PaperRun>();
        while (await reader.ReadAsync(ct))
        {
            var config = PaperRunConfig.Parse(reader.GetString(3));
            if (config.Hash() != reader.GetString(4))
            {
                throw new InvalidOperationException($"Paper run {reader.GetInt64(0)} configuration does not match its stored hash.");
            }

            result.Add(new PaperRun(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2), config));
        }

        return result;
    }

    public async Task<PaperRun> CreateRunAsync(DateTimeOffset tradingStart, DateTimeOffset warmupStart, PaperRunConfig config, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO trading.paper_runs (trading_start, warmup_start, config, config_hash) VALUES ($1, $2, $3::jsonb, $4) RETURNING id", connection);
        command.Parameters.AddWithValue(tradingStart);
        command.Parameters.AddWithValue(warmupStart);
        command.Parameters.AddWithValue(config.Json());
        command.Parameters.AddWithValue(config.Hash());
        return new PaperRun((long)(await command.ExecuteScalarAsync(ct))!, tradingStart, warmupStart, config);
    }

    public async Task<IReadOnlyList<StepRecord>> StepsAsync(long runId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT bar_time, context::text, state_hash FROM trading.steps WHERE run_id = $1 ORDER BY bar_time", connection);
        command.Parameters.AddWithValue(runId);
        var result = new List<StepRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new StepRecord(reader.GetFieldValue<DateTimeOffset>(0), StepInputs.Parse(reader.GetString(1)), reader.GetString(2)));
        }

        return result;
    }

    /// <summary>
    /// Per sleeve: number of fills and the sum of ledger cash changes (reconciliation input). Summed here: the exact
    /// numeric sum of 28-digit decimals can exceed the decimal range.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, (long Fills, decimal Cash)>> LedgerAsync(long runId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT l.sleeve, l.fill_id, l.cash_delta FROM trading.ledger_entries l WHERE l.run_id = $1 ORDER BY l.id", connection);
        command.Parameters.AddWithValue(runId);
        var fills = new Dictionary<int, HashSet<long>>();
        var cash = new Dictionary<int, decimal>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sleeve = reader.GetInt32(0);
            (fills.TryGetValue(sleeve, out var set) ? set : fills[sleeve] = []).Add(reader.GetInt64(1));
            cash[sleeve] = cash.GetValueOrDefault(sleeve) + reader.GetDecimal(2);
        }

        return fills.ToDictionary(kv => kv.Key, kv => ((long)kv.Value.Count, cash[kv.Key]));
    }

    public async Task<bool> KillSwitchEngagedAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT engaged FROM trading.kill_switch_events ORDER BY id DESC LIMIT 1", connection);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    public async Task EngageKillSwitchAsync(string actor, string reason, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("INSERT INTO trading.kill_switch_events (engaged, actor, reason) VALUES (true, $1, $2)", connection);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Operational actions not yet applied; the next processed step applies them (m5-protocol §2.4).</summary>
    public async Task<IReadOnlyList<OpsAction>> ActionsAfterAsync(long lastId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id, action, run_id, sleeve, symbol FROM trading.ops_actions WHERE id > $1 ORDER BY id", connection);
        command.Parameters.AddWithValue(lastId);
        var result = new List<OpsAction>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new OpsAction(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    public async Task<long> LastActionIdAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT coalesce(max(id), 0) FROM trading.ops_actions", connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>All RiskConfig versions by id (replay needs the version in force at each step).</summary>
    public async Task<IReadOnlyDictionary<long, RiskConfigDocument>> RiskVersionsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id, config::text, config_hash FROM trading.risk_config_versions ORDER BY id", connection);
        var result = new Dictionary<long, RiskConfigDocument>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var document = JsonSerializer.Deserialize<RiskConfigDocument>(reader.GetString(1), PaperJson.Options)!;
            if (document.Hash() != reader.GetString(2))
            {
                throw new InvalidOperationException($"RiskConfig version {reader.GetInt64(0)} does not match its hash.");
            }

            result[reader.GetInt64(0)] = document;
        }

        return result;
    }

    public async Task AddStepAsync(PaperRun run, StepWrite step, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO trading.steps (run_id, bar_time, context, state_hash, l1_hash, l2_hash) VALUES ($1, $2, $3::jsonb, $4, $5, $6)", connection, transaction))
        {
            insert.Parameters.AddWithValue(run.Id);
            insert.Parameters.AddWithValue(step.BarTime);
            insert.Parameters.AddWithValue(step.Inputs.Json());
            insert.Parameters.AddWithValue(step.StateHash);
            insert.Parameters.AddWithValue(EngineEvents.Hash(step.Events, false));
            insert.Parameters.AddWithValue(EngineEvents.Hash(step.Events, true));
            await insert.ExecuteNonQueryAsync(ct);
        }

        var seq = 0;
        foreach (var e in step.Events)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO trading.engine_events (run_id, bar_time, seq, event_time, sleeve, symbol, kind, value, detail, before, after)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                """, connection, transaction);
            insert.Parameters.AddWithValue(run.Id);
            insert.Parameters.AddWithValue(step.BarTime);
            insert.Parameters.AddWithValue(seq++);
            insert.Parameters.AddWithValue(e.Time);
            insert.Parameters.AddWithValue(e.Sleeve);
            insert.Parameters.AddWithValue(e.Symbol);
            insert.Parameters.AddWithValue(e.Kind.ToString());
            insert.Parameters.AddWithValue(e.Value);
            insert.Parameters.AddWithValue(e.Detail);
            insert.Parameters.AddWithValue(e.Before);
            insert.Parameters.AddWithValue(e.After);
            await insert.ExecuteNonQueryAsync(ct);
        }

        foreach (var f in step.Fills)
        {
            var sleeve = run.Config.Sleeves[f.Sleeve];
            long fillId;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO trading.fills (run_id, bar_time, sleeve, strategy, cohort_id, symbol, side, fill_time, price, quantity, fee, reason)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12) RETURNING id
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue(run.Id);
                insert.Parameters.AddWithValue(step.BarTime);
                insert.Parameters.AddWithValue(f.Sleeve);
                insert.Parameters.AddWithValue(sleeve.Name);
                insert.Parameters.AddWithValue(sleeve.CohortId);
                insert.Parameters.AddWithValue(f.Symbol);
                insert.Parameters.AddWithValue(f.Buy ? "buy" : "sell");
                insert.Parameters.AddWithValue(f.Time);
                insert.Parameters.AddWithValue(f.Price);
                insert.Parameters.AddWithValue(f.Quantity);
                insert.Parameters.AddWithValue(f.Fee);
                insert.Parameters.AddWithValue(f.Reason);
                fillId = (long)(await insert.ExecuteScalarAsync(ct))!;
            }

            foreach (var (kind, delta) in new[] { ("trade", f.Buy ? -f.Notional : f.Notional), ("fee", -f.Fee) })
            {
                await using var ledger = new NpgsqlCommand(
                    "INSERT INTO trading.ledger_entries (run_id, fill_id, sleeve, kind, cash_delta) VALUES ($1, $2, $3, $4, $5)", connection, transaction);
                ledger.Parameters.AddWithValue(run.Id);
                ledger.Parameters.AddWithValue(fillId);
                ledger.Parameters.AddWithValue(f.Sleeve);
                ledger.Parameters.AddWithValue(kind);
                ledger.Parameters.AddWithValue(delta);
                await ledger.ExecuteNonQueryAsync(ct);
            }
        }

        foreach (var (sleeve, cash, equity, positions) in step.Balances)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO trading.balance_snapshots (run_id, bar_time, sleeve, cash, equity, positions) VALUES ($1, $2, $3, $4, $5, $6::jsonb)", connection, transaction);
            insert.Parameters.AddWithValue(run.Id);
            insert.Parameters.AddWithValue(step.BarTime);
            insert.Parameters.AddWithValue(sleeve);
            insert.Parameters.AddWithValue(cash);
            insert.Parameters.AddWithValue(equity);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(positions, PaperJson.Options));
            await insert.ExecuteNonQueryAsync(ct);
        }

        if (step.Correlation is { } c)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO trading.correlation_snapshots (run_id, bar_time, threshold, clusters, effective_count, max_cluster_share)
                VALUES ($1, $2, $3, $4::jsonb, $5, $6)
                """, connection, transaction);
            insert.Parameters.AddWithValue(run.Id);
            insert.Parameters.AddWithValue(step.BarTime);
            insert.Parameters.AddWithValue((decimal)c.Threshold);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(c.Clusters, PaperJson.Options));
            insert.Parameters.AddWithValue((decimal)Math.Round(c.EffectiveCount, 6));
            insert.Parameters.AddWithValue((decimal)Math.Round(c.MaxClusterShare, 6));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task AddReconciliationAsync(long runId, DateTimeOffset? barTime, bool ok, object detail, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO trading.reconciliation_runs (run_id, bar_time, ok, detail) VALUES ($1, $2, $3, $4::jsonb)", connection);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue((object?)barTime ?? DBNull.Value);
        command.Parameters.AddWithValue(ok);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(detail, PaperJson.Options));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AddCycleMetricAsync(CycleMetric metric, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO trading.cycle_metrics (scheduled_for, started_at, finished_at, bars_processed, catch_up, stale, outcome, detail)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)
            """, connection);
        command.Parameters.AddWithValue(metric.ScheduledFor);
        command.Parameters.AddWithValue(metric.StartedAt);
        command.Parameters.AddWithValue(metric.FinishedAt);
        command.Parameters.AddWithValue(metric.BarsProcessed);
        command.Parameters.AddWithValue(metric.CatchUp);
        command.Parameters.AddWithValue(metric.Stale);
        command.Parameters.AddWithValue(metric.Outcome);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(metric.Detail, PaperJson.Options));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Append-only audit (audit.events); the payload hash is SHA-256 of the JSON payload.</summary>
    public async Task AuditAsync(string actor, string @event, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, PaperJson.Options);
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ($1, $2, $3::jsonb, $4)", connection);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(@event);
        command.Parameters.AddWithValue(json);
        command.Parameters.AddWithValue(DslCanonical.Sha256(json));
        await command.ExecuteNonQueryAsync(ct);
    }
}
