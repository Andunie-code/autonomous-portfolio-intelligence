using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Execution;

public sealed class OpsRequestException(string message) : Exception(message);

/// <summary>The request is well formed but the system's state does not allow it (HTTP 409).</summary>
public sealed class OpsConflictException(string message) : Exception(message);

/// <summary>
/// The user's operational interface (m5-protocol §3; H23, H24), on the ops_api database role. Reads paper state,
/// positions, PnL by strategy and cohort (open cohorts included, D23), RiskConfig, correlation and lifecycle;
/// writes only kill switch events, operational actions, Disabled transitions, RiskConfig versions and audit.
/// It has no operation on manifests, results, validation status or cohort registration. M6: the user's
/// PaperTrading → TestnetActive transition (V10, D30) and the Testnet rebaseline (m6-protocol §4.4, §6.2).
/// </summary>
public sealed class OpsService(string connectionString, TimeProvider? time = null, bool testnetEnabled = false)
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

    /// <summary>Latest balances of every run (one per cohort, ADR-0019); a position close names run_id and sleeve.</summary>
    public Task<string> PositionsAsync(CancellationToken ct) => JsonQueryAsync("""
        SELECT b.run_id, b.sleeve, s.value ->> 'name' AS strategy, (s.value ->> 'cohortId')::bigint AS cohort_id,
               b.cash, b.equity, b.positions, b.bar_time
        FROM trading.balance_snapshots b
        JOIN trading.paper_runs r ON r.id = b.run_id
        CROSS JOIN LATERAL jsonb_array_elements(r.config -> 'sleeves') WITH ORDINALITY AS s(value, ordinality)
        WHERE s.ordinality - 1 = b.sleeve AND b.bar_time = (SELECT max(bar_time) FROM trading.balance_snapshots WHERE run_id = b.run_id)
        ORDER BY b.run_id, b.sleeve
        """, ct);

    /// <summary>PnL by strategy or cohort over every run: latest equity minus the sleeve capital from the run configuration.</summary>
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
            GROUP BY 1 ORDER BY 1
            """, ct);
    }

    public Task<string> RiskConfigAsync(CancellationToken ct) => JsonQueryAsync(
        "SELECT id, created_at, actor, reason, config, config_hash FROM trading.risk_config_versions ORDER BY id DESC LIMIT 1", ct);

    /// <summary>The latest correlation snapshot of every run.</summary>
    public Task<string> CorrelationAsync(CancellationToken ct) => JsonQueryAsync("""
        SELECT DISTINCT ON (run_id) run_id, bar_time, threshold, clusters, effective_count, max_cluster_share
        FROM trading.correlation_snapshots ORDER BY run_id, bar_time DESC
        """, ct);

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

    /// <summary>
    /// Close one position (run, sleeve, symbol) or everything in every run. Sleeve indices are per run: with more
    /// than one run the run id is required; with exactly one it defaults to that run (ADR-0019 §3).
    /// </summary>
    public async Task ClosePositionsAsync(long? runId, int? sleeve, string? symbol, string reason, CancellationToken ct)
    {
        Require(reason);
        if ((sleeve is null) != (symbol is null))
        {
            throw new OpsRequestException("give both sleeve and symbol, or neither for close-all");
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (sleeve is not null)
        {
            var runs = new List<(long Id, int Sleeves)>();
            await using (var find = new NpgsqlCommand("SELECT id, jsonb_array_length(config -> 'sleeves') FROM trading.paper_runs ORDER BY id", connection, transaction))
            await using (var reader = await find.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    runs.Add((reader.GetInt64(0), reader.GetInt32(1)));
                }
            }

            runId ??= runs.Count == 1 ? runs[0].Id : throw new OpsRequestException("runId is required: there is more than one paper run");
            if (!runs.Any(r => r.Id == runId && sleeve >= 0 && sleeve < r.Sleeves))
            {
                throw new OpsRequestException($"run {runId} has no sleeve {sleeve}");
            }
        }
        else
        {
            runId = null;
        }

        await Execute(connection, transaction, "INSERT INTO trading.ops_actions (actor, action, run_id, sleeve, symbol, reason) VALUES ('user', $1, $2, $3, $4, $5)",
            ct, sleeve is null ? "close-all" : "close-position", (object?)runId ?? DBNull.Value, (object?)sleeve ?? DBNull.Value, (object?)symbol ?? DBNull.Value, reason);
        await Audit(connection, transaction, sleeve is null ? "close_all_requested" : "close_position_requested", new { runId, sleeve, symbol, reason }, ct);
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

    /// <summary>
    /// PaperTrading → TestnetActive (V10; m6-protocol §6.2): only with confirm = true (400 otherwise), only from
    /// PaperTrading, only with Testnet enabled, only when D30 holds and the definition matches its manifest hash (409
    /// otherwise). The lifecycle event carries the D30 snapshot; the database trigger checks role and evidence again.
    /// </summary>
    public async Task<PromotionStatus> ActivateTestnetAsync(string name, string reason, bool confirm, CancellationToken ct)
    {
        Require(reason);
        if (!confirm)
        {
            throw new OpsRequestException("PaperTrading -> TestnetActive needs confirm = true");
        }

        var now = (time ?? TimeProvider.System).GetUtcNow();
        await using var connection = await OpenAsync(ct);
        var status = (await Promotion.ReadAsync(connection, now, name, ct)).LastOrDefault() ?? throw new OpsRequestException($"unknown strategy {name}");
        if (status.State != "PaperTrading")
        {
            throw new OpsConflictException($"{name} is {status.State}; only a PaperTrading strategy can become TestnetActive");
        }

        if (!testnetEnabled)
        {
            throw new OpsConflictException("Testnet is not enabled on Trading.Host (Trading:Testnet and the Testnet key)");
        }

        if (!status.Eligible)
        {
            throw new OpsConflictException($"D30 is not met for {name}: {string.Join(", ", status.Missing)} (days {status.Days}/{Promotion.MinimumDays}, bars {status.Bars}/{Promotion.MinimumBars}, critical errors in 7 days {status.CriticalErrors7d})");
        }

        // V14: the definition run on Testnet must still match its manifest hash.
        await using (var find = new NpgsqlCommand("""
            SELECT f.manifest, f.member_index, v.definition::text FROM research.frozen_strategies f
            LEFT JOIN research.strategy_versions v ON v.definition_hash = f.definition_hash WHERE f.id = $1
            """, connection))
        {
            find.Parameters.AddWithValue(status.FrozenId);
            await using var reader = await find.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            try
            {
                var manifest = CohortManifestReader.Read(reader.GetString(0));
                var expected = manifest.Members.Single(m => m.Index == reader.GetInt32(1)).DefinitionHash;
                StrategyHashGuard.LoadVerified(reader.IsDBNull(2) ? "{}" : reader.GetString(2), expected);
            }
            catch (Exception e) when (e is StrategyHashMismatchException or FormatException or InvalidOperationException or JsonException)
            {
                throw new OpsConflictException($"{name}: the definition does not match its manifest hash (V14)");
            }
        }

        var evidence = JsonSerializer.Serialize(new
        {
            d30 = new { status.PaperSince, status.Days, status.Bars, status.CriticalErrors7d, status.OutageRatio, checkedAt = now },
            confirm = true,
            reason,
            experimental = status.Experimental,
        }, PaperJson.Options);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute(connection, transaction, "INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES ($1, 'PaperTrading', 'TestnetActive', 'user', $2::jsonb)",
            ct, status.FrozenId, evidence);
        await Execute(connection, transaction, "INSERT INTO trading.ops_actions (actor, action, reason) VALUES ('user', 'testnet-activate', $1)", ct, $"{name}: {reason}");
        await Audit(connection, transaction, "strategy_testnet_activated", new { name, reason, status.Days, status.Bars, status.Experimental }, ct);
        await transaction.CommitAsync(ct);
        return status;
    }

    /// <summary>After a Testnet reset (m6-protocol §4.4): the next cycle closes the open Testnet runs and starts new ones with a new baseline.</summary>
    public async Task RebaselineTestnetAsync(string reason, bool confirm, CancellationToken ct)
    {
        Require(reason);
        if (!confirm)
        {
            throw new OpsRequestException("a Testnet rebaseline needs confirm = true");
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute(connection, transaction, "INSERT INTO trading.ops_actions (actor, action, reason) VALUES ('user', 'testnet-rebaseline', $1)", ct, reason);
        await Audit(connection, transaction, "testnet_rebaseline_requested", new { reason }, ct);
        await transaction.CommitAsync(ct);
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
