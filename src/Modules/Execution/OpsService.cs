using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution;

public sealed class OpsRequestException(string message) : Exception(message);

/// <summary>
/// The user's operational interface (m5-protocol §3; H23, H24), on the ops_api database role. Reads paper state,
/// positions, PnL by strategy and cohort (open cohorts included, D23), RiskConfig, correlation and lifecycle;
/// writes only kill switch events, operational actions, Disabled transitions, RiskConfig versions and audit.
/// It has no operation on manifests, results, validation status or cohort registration.
/// </summary>
public sealed class OpsService(string connectionString)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task<string> JsonQueryAsync(string sql, CancellationToken ct, params object[] parameters)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT coalesce(json_agg(q), '[]'::json)::text FROM ({sql}) q", connection);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }

        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    public Task<string> StatusAsync(CancellationToken ct) => JsonQueryAsync("""
        SELECT (SELECT max(bar_time) FROM trading.steps) AS last_bar,
               (SELECT coalesce((SELECT engaged FROM trading.kill_switch_events ORDER BY id DESC LIMIT 1), false)) AS kill_switch,
               (SELECT row_to_json(c) FROM (SELECT scheduled_for, started_at, finished_at, bars_processed, catch_up, stale, outcome, detail
                                            FROM trading.cycle_metrics ORDER BY id DESC LIMIT 1) c) AS last_cycle,
               (SELECT row_to_json(r) FROM (SELECT checked_at, ok, detail FROM trading.reconciliation_runs ORDER BY id DESC LIMIT 1) r) AS last_reconciliation
        """, ct);

    public Task<string> PositionsAsync(CancellationToken ct) => JsonQueryAsync("""
        SELECT b.sleeve, b.cash, b.equity, b.positions, b.bar_time
        FROM trading.balance_snapshots b
        WHERE b.run_id = (SELECT max(id) FROM trading.paper_runs) AND b.bar_time = (SELECT max(bar_time) FROM trading.balance_snapshots WHERE run_id = b.run_id)
        ORDER BY b.sleeve
        """, ct);

    /// <summary>PnL by strategy or cohort: latest equity minus the sleeve capital from the run configuration.</summary>
    public Task<string> PnlAsync(string by, CancellationToken ct)
    {
        var key = by switch
        {
            "strategy" => "s.value ->> 'name'",
            "cohort" => "(s.value ->> 'cohortId')::bigint",
            _ => throw new OpsRequestException("by must be strategy or cohort"),
        };
        return JsonQueryAsync($"""
            SELECT {key} AS key, sum(b.equity) AS equity, sum((s.value ->> 'capital')::numeric) AS capital,
                   sum(b.equity) - sum((s.value ->> 'capital')::numeric) AS pnl
            FROM trading.paper_runs r
            CROSS JOIN LATERAL jsonb_array_elements(r.config -> 'sleeves') WITH ORDINALITY AS s(value, ordinality)
            JOIN trading.balance_snapshots b ON b.run_id = r.id AND b.sleeve = s.ordinality - 1
                 AND b.bar_time = (SELECT max(bar_time) FROM trading.balance_snapshots WHERE run_id = r.id)
            WHERE r.id = (SELECT max(id) FROM trading.paper_runs)
            GROUP BY 1 ORDER BY 1
            """, ct);
    }

    public Task<string> RiskConfigAsync(CancellationToken ct) => JsonQueryAsync(
        "SELECT id, created_at, actor, reason, config, config_hash FROM trading.risk_config_versions ORDER BY id DESC LIMIT 1", ct);

    public Task<string> CorrelationAsync(CancellationToken ct) => JsonQueryAsync(
        "SELECT bar_time, threshold, clusters, effective_count, max_cluster_share FROM trading.correlation_snapshots ORDER BY run_id DESC, bar_time DESC LIMIT 1", ct);

    public Task<string> LifecycleAsync(CancellationToken ct) => JsonQueryAsync("""
        SELECT f.cohort_id, f.member_index, f.name, l.from_state, l.to_state, l.actor, l.recorded_at
        FROM research.lifecycle_events l JOIN research.frozen_strategies f ON f.id = l.frozen_id ORDER BY l.id
        """, ct);

    public async Task SetKillSwitchAsync(bool engaged, string reason, CancellationToken ct)
    {
        Require(reason);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute(connection, transaction, "INSERT INTO trading.kill_switch_events (engaged, actor, reason) VALUES ($1, 'user', $2)", ct, engaged, reason);
        await Audit(connection, transaction, engaged ? "trading_kill_switch_engaged" : "trading_kill_switch_released", new { reason }, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Operational disable (* → Disabled): always allowed; does not touch cohort records (V9).</summary>
    public async Task DisableStrategyAsync(string name, string reason, CancellationToken ct)
    {
        Require(reason);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var find = new NpgsqlCommand("""
            SELECT f.id, coalesce((SELECT l.to_state FROM research.lifecycle_events l WHERE l.frozen_id = f.id ORDER BY l.id DESC LIMIT 1), 'Candidate')
            FROM research.frozen_strategies f WHERE f.name = $1 ORDER BY f.id DESC LIMIT 1
            """, connection, transaction);
        find.Parameters.AddWithValue(name);
        long id;
        string state;
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                throw new OpsRequestException($"unknown strategy {name}");
            }

            (id, state) = (reader.GetInt64(0), reader.GetString(1));
        }

        if (state == "Disabled")
        {
            return;
        }

        await Execute(connection, transaction, "INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, $2, 'Disabled', 'user', $3::jsonb)",
            ct, id, state, JsonSerializer.Serialize(new { reason }));
        await Execute(connection, transaction, "INSERT INTO trading.ops_actions (actor, action, reason) VALUES ('user', 'disable-strategy', $1)", ct, $"{name}: {reason}");
        await Audit(connection, transaction, "strategy_disabled", new { name, reason }, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ClosePositionsAsync(int? sleeve, string? symbol, string reason, CancellationToken ct)
    {
        Require(reason);
        if ((sleeve is null) != (symbol is null))
        {
            throw new OpsRequestException("give both sleeve and symbol, or neither for close-all");
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute(connection, transaction, "INSERT INTO trading.ops_actions (actor, action, sleeve, symbol, reason) VALUES ('user', $1, $2, $3, $4)",
            ct, sleeve is null ? "close-all" : "close-position", (object?)sleeve ?? DBNull.Value, (object?)symbol ?? DBNull.Value, reason);
        await Audit(connection, transaction, sleeve is null ? "close_all_requested" : "close_position_requested", new { sleeve, symbol, reason }, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>New RiskConfig version (m5-protocol §3). Loosening any limit needs <paramref name="confirmLoosening"/>.</summary>
    public async Task<long> AddRiskConfigAsync(RiskConfigDocument next, RiskConfigDocument current, string reason, bool confirmLoosening, CancellationToken ct)
    {
        Require(reason);
        if (next.Errors() is { Count: > 0 } errors)
        {
            throw new OpsRequestException(string.Join("; ", errors));
        }

        var loosened = current.IsLoosenedBy(next);
        if (loosened && !confirmLoosening)
        {
            throw new OpsRequestException("this version relaxes a risk limit; resend with confirm = true");
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO trading.risk_config_versions (actor, reason, config, config_hash) VALUES ('user', $1, $2::jsonb, $3) RETURNING id", connection, transaction);
        insert.Parameters.AddWithValue(reason);
        insert.Parameters.AddWithValue(next.Json());
        insert.Parameters.AddWithValue(next.Hash());
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;
        await Audit(connection, transaction, "risk_config_version_added", new { id, hash = next.Hash(), loosened, reason }, ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>The RiskConfig in force: the latest version, or the paper run's initial configuration.</summary>
    public async Task<RiskConfigDocument?> CurrentRiskAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT coalesce((SELECT config::text FROM trading.risk_config_versions ORDER BY id DESC LIMIT 1),
                            (SELECT (config -> 'initialRisk')::text FROM trading.paper_runs ORDER BY id DESC LIMIT 1))
            """, connection);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<RiskConfigDocument>(json, PaperJson.Options) : null;
    }

    private static void Require(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new OpsRequestException("a reason is required");
        }
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var p in parameters)
        {
            command.Parameters.AddWithValue(p);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, string @event, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, PaperJson.Options);
        return Execute(connection, transaction, "INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ('user', $1, $2::jsonb, $3)", ct, @event, json, DslCanonical.Sha256(json));
    }
}
