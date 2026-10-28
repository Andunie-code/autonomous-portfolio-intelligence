using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Kernel.Dsl;

namespace PortfolioIntelligence.Ai.Persistence;

// PostgreSQL stores over migration 0006 (ai schema, append-only) and audit.events (migration 0005).

public sealed class NpgsqlAiAudit(string connectionString) : IAiAudit
{
    public async Task RecordAsync(string actor, string @event, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ($1, $2, $3::jsonb, $4)", connection);
        command.Parameters.AddWithValue(actor);
        command.Parameters.AddWithValue(@event);
        command.Parameters.AddWithValue(json);
        command.Parameters.AddWithValue(DslCanonical.Sha256(json));
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class NpgsqlKillSwitchStore(string connectionString) : IKillSwitchStore
{
    public async Task<KillSwitchEvent?> LatestAsync(CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand(
            "SELECT at, tripped, actor, trigger_kind, reason FROM ai.kill_switch_events ORDER BY id DESC LIMIT 1", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new KillSwitchEvent(reader.GetFieldValue<DateTimeOffset>(0), reader.GetBoolean(1), reader.GetString(2),
                Enum.Parse<KillSwitchTrigger>(reader.GetString(3)), reader.GetString(4))
            : null;
    }

    public async Task AppendAsync(KillSwitchEvent killSwitchEvent, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO ai.kill_switch_events (at, tripped, actor, trigger_kind, reason) VALUES ($1, $2, $3, $4, $5)", connection);
        command.Parameters.AddWithValue(killSwitchEvent.At);
        command.Parameters.AddWithValue(killSwitchEvent.Tripped);
        command.Parameters.AddWithValue(killSwitchEvent.Actor);
        command.Parameters.AddWithValue(killSwitchEvent.Trigger.ToString());
        command.Parameters.AddWithValue(killSwitchEvent.Reason);
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Ledger in ai.cost_ledger. A reservation takes a transaction-scoped advisory lock, so the cap check and the insert
/// are atomic across processes (both hosts and the CLI share the monthly cap).
/// </summary>
public sealed class NpgsqlCostLedger(string connectionString) : ICostLedger
{
    private const long LockKey = 0x4149_4C45_4447_4552; // "AILEDGER"

    private const string Columns = "at, call_id, job_id, job_kind, task_class, entry_kind, amount_usd, provider, model, input_tokens, output_tokens, price_version";

    public async Task<ReservationStatus> TryReserveAsync(ReservationRequest request, UsdAmount monthlyCap, UsdAmount jobCap, UsdAmount callCap, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue(LockKey);
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        var month = LedgerMath.MonthStart(now);
        await using var sums = new NpgsqlCommand("""
            WITH calls AS (
                SELECT r.at, r.job_id, COALESCE(s.amount_usd, r.amount_usd) AS committed
                FROM ai.cost_ledger r
                LEFT JOIN ai.cost_ledger s ON s.call_id = r.call_id AND s.entry_kind = 'Settlement'
                WHERE r.entry_kind = 'Reservation' AND (r.at >= $1 OR r.job_id = $2))
            SELECT COALESCE(sum(committed) FILTER (WHERE at >= $1), 0), COALESCE(sum(committed) FILTER (WHERE job_id = $2), 0) FROM calls
            """, connection, transaction);
        sums.Parameters.AddWithValue(month);
        sums.Parameters.AddWithValue(request.Job.JobId);
        decimal monthToDate, job;
        await using (var reader = await sums.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            monthToDate = reader.GetDecimal(0);
            job = reader.GetDecimal(1);
        }

        var status = LedgerMath.Check(new SpendSummary(new UsdAmount(monthToDate), new UsdAmount(job), UsdAmount.Zero), request.Amount, monthlyCap, jobCap, callCap);
        if (status == ReservationStatus.Reserved)
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO ai.cost_ledger ({Columns}) VALUES ($1, $2, $3, $4, $5, 'Reservation', $6, $7, $8, 0, 0, $9)", connection, transaction);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(request.CallId);
            insert.Parameters.AddWithValue(request.Job.JobId);
            insert.Parameters.AddWithValue(request.Job.Kind.ToString());
            insert.Parameters.AddWithValue((object?)request.Task?.ToString() ?? DBNull.Value);
            insert.Parameters.AddWithValue(request.Amount.Value);
            insert.Parameters.AddWithValue(request.Provider);
            insert.Parameters.AddWithValue(request.Model);
            insert.Parameters.AddWithValue(request.PriceVersion);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return status;
    }

    public async Task SettleAsync(Guid callId, UsdAmount actual, AiUsage usage, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO ai.cost_ledger ({Columns})
            SELECT $1, call_id, job_id, job_kind, task_class, 'Settlement', $2, provider, model, $3, $4, price_version
            FROM ai.cost_ledger WHERE call_id = $5 AND entry_kind = 'Reservation'
            """, connection);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(actual.Value);
        command.Parameters.AddWithValue(usage.InputTokens);
        command.Parameters.AddWithValue(usage.OutputTokens);
        command.Parameters.AddWithValue(callId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException($"No reservation for call {callId}.");
        }
    }

    public async Task<SpendSummary> SummaryAsync(DateTimeOffset now, string jobId, TimeSpan window, CancellationToken ct)
    {
        var entries = await QueryAsync("WHERE at >= $1 OR job_id = $2 OR at > $3",
            [LedgerMath.MonthStart(now), jobId, now - window], ct);
        return LedgerMath.Summary(entries, now, jobId, window);
    }

    public Task<IReadOnlyList<LedgerEntry>> EntriesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        QueryAsync("WHERE at >= $1 AND at < $2", [from, to], ct);

    private async Task<IReadOnlyList<LedgerEntry>> QueryAsync(string where, object[] parameters, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM ai.cost_ledger {where} ORDER BY id", connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var result = new List<LedgerEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new LedgerEntry(
                reader.GetFieldValue<DateTimeOffset>(0), reader.GetGuid(1), reader.GetString(2), Enum.Parse<JobKind>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Enum.Parse<TaskClass>(reader.GetString(4)), Enum.Parse<LedgerEntryKind>(reader.GetString(5)),
                new UsdAmount(reader.GetDecimal(6)), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetString(11)));
        }

        return result;
    }
}

public sealed class NpgsqlDecisionTraceStore(string connectionString) : IDecisionTraceStore
{
    public async Task AddAsync(DecisionTrace trace, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO ai.decision_traces (task_id, at, job_id, task_class, mode, input_hash, evidence_ids, provider, requested_model,
                actual_model, prompt_version, qualification_id, qualification_protocol_hash, calls, input_tokens, output_tokens, latency_ms,
                estimated_usd, actual_usd, validator_results, safety_violations, outcome, degraded, reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20::jsonb, $21::jsonb, $22, $23, $24)
            """, connection);
        object Nullable(object? value) => value ?? DBNull.Value;
        command.Parameters.AddWithValue(trace.TaskId);
        command.Parameters.AddWithValue(trace.At);
        command.Parameters.AddWithValue(trace.JobId);
        command.Parameters.AddWithValue(trace.Task.ToString());
        command.Parameters.AddWithValue(trace.Mode.ToString());
        command.Parameters.AddWithValue(trace.InputHash);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(trace.EvidenceIds));
        command.Parameters.AddWithValue(Nullable(trace.Provider));
        command.Parameters.AddWithValue(Nullable(trace.RequestedModel));
        command.Parameters.AddWithValue(Nullable(trace.ActualModel));
        command.Parameters.AddWithValue(Nullable(trace.PromptVersion));
        command.Parameters.AddWithValue(Nullable(trace.QualificationId));
        command.Parameters.AddWithValue(Nullable(trace.QualificationProtocolHash));
        command.Parameters.AddWithValue(trace.Calls);
        command.Parameters.AddWithValue(trace.InputTokens);
        command.Parameters.AddWithValue(trace.OutputTokens);
        command.Parameters.AddWithValue(trace.LatencyMs);
        command.Parameters.AddWithValue(trace.Estimated.Value);
        command.Parameters.AddWithValue(trace.Actual.Value);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(trace.ValidatorResults));
        command.Parameters.AddWithValue(JsonSerializer.Serialize(trace.SafetyViolations));
        command.Parameters.AddWithValue(trace.Outcome.ToString());
        command.Parameters.AddWithValue(trace.Degraded);
        command.Parameters.AddWithValue(Nullable(trace.Reason));
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class NpgsqlQualificationStore(string connectionString) : IQualificationStore
{
    public async Task RegisterProtocolAsync(QualificationProtocol p, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO ai.qualification_protocols (protocol_hash, document_commit, task_class, provider, model, prompt_version, schema_hash,
                pilot_set_hash, qualification_set_hash, alpha, minimum_effect, sample_per_arm, budget_usd, design, p0)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            """, connection);
        foreach (var value in new object[] { p.ProtocolHash, p.DocumentCommit, p.Task.ToString(), p.Provider, p.Model, p.PromptVersion, p.SchemaHash,
                     p.PilotSetHash, p.QualificationSetHash, p.Alpha, p.MinimumEffect, p.SamplePerArm, p.BudgetUsd, p.Design.ToString(), p.P0 })
        {
            command.Parameters.AddWithValue(value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<QualificationProtocol?> FindProtocolAsync(string protocolHash, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand("""
            SELECT protocol_hash, document_commit, task_class, provider, model, prompt_version, schema_hash, pilot_set_hash, qualification_set_hash,
                alpha, minimum_effect, sample_per_arm, budget_usd, design, p0
            FROM ai.qualification_protocols WHERE protocol_hash = $1
            """, connection);
        command.Parameters.AddWithValue(protocolHash);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new QualificationProtocol(reader.GetString(0), reader.GetString(1), Enum.Parse<TaskClass>(reader.GetString(2)), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetDouble(9),
                reader.GetDouble(10), reader.GetInt32(11), reader.GetDecimal(12), Enum.Parse<QualificationDesign>(reader.GetString(13)), reader.GetDouble(14))
            : null;
    }

    public async Task<long> AddResultAsync(TaskQualification r, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO ai.task_qualifications (at, protocol_hash, verdict, trials_llm, successes_llm, trials_baseline, successes_baseline,
                p_value, safety_violations, cost_usd, notes)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING id
            """, connection);
        foreach (var value in new object[] { r.At, r.ProtocolHash, r.Verdict.ToString(), r.TrialsLlm, r.SuccessesLlm, r.TrialsBaseline,
                     r.SuccessesBaseline, r.PValue, r.SafetyViolations, r.Cost.Value, r.Notes })
        {
            command.Parameters.AddWithValue(value);
        }

        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<TaskQualification?> FindResultAsync(long qualificationId, CancellationToken ct)
    {
        await using var connection = await Db.OpenAsync(connectionString, ct);
        await using var command = new NpgsqlCommand("""
            SELECT id, protocol_hash, at, verdict, trials_llm, successes_llm, trials_baseline, successes_baseline, p_value, safety_violations, cost_usd, notes
            FROM ai.task_qualifications WHERE id = $1
            """, connection);
        command.Parameters.AddWithValue(qualificationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new TaskQualification(reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), Enum.Parse<QualificationVerdict>(reader.GetString(3)),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetDouble(8), reader.GetInt32(9),
                new UsdAmount(reader.GetDecimal(10)), reader.GetString(11))
            : null;
    }
}

internal static class Db
{
    public static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
