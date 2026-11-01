using System.Text.Json;
using Npgsql;

namespace PortfolioIntelligence.Execution;

/// <summary>
/// The AI spend caps the dashboard and the alarms compare against (ai.v1.json). Trading.Host reads them with the Ai
/// module's configuration and passes the values in, so Execution stays AI-free (ADR-0023 §1, ADR-0024 §1).
/// </summary>
public sealed record AiBudget(decimal MonthlyCapUsd, IReadOnlyDictionary<string, decimal> JobShares)
{
    public static readonly AiBudget None = new(0m, new Dictionary<string, decimal>());
}

/// <summary>Committed AI spend from ai.cost_ledger, counted as the ledger's cap check counts it (LedgerMath).</summary>
public static class AiSpend
{
    /// <summary>
    /// A CTE <c>c</c> with one row per call: the settlement if the call was settled, otherwise its open reservation,
    /// at the reservation's time.
    /// </summary>
    public const string Committed = """
        WITH c AS (
          SELECT r.at, r.job_id, r.job_kind, coalesce(r.task_class, '(none)') AS task_class, r.call_id,
                 coalesce(s.amount_usd, r.amount_usd) AS committed_usd, coalesce(s.amount_usd, 0) AS settled_usd,
                 CASE WHEN s.call_id IS NULL THEN r.amount_usd ELSE 0 END AS open_usd,
                 coalesce(s.input_tokens, 0) AS input_tokens, coalesce(s.output_tokens, 0) AS output_tokens
          FROM ai.cost_ledger r LEFT JOIN ai.cost_ledger s ON s.call_id = r.call_id AND s.entry_kind = 'Settlement'
          WHERE r.entry_kind = 'Reservation')
        """;

    public static DateTimeOffset MonthStart(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<decimal> CommittedSinceAsync(NpgsqlConnection connection, DateTimeOffset from, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"{Committed} SELECT coalesce(sum(committed_usd), 0) FROM c WHERE at >= $1", connection);
        command.Parameters.AddWithValue(from);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Alarm thresholds B-1…B-5 (m9-m10-protocol §0.2; user approval 2026-11-01).</summary>
public static class AlarmThresholds
{
    public const decimal BudgetWarningShare = 0.80m;
    public const decimal BudgetCriticalShare = 1.00m;
    public static readonly TimeSpan HenvMaxAge = TimeSpan.FromDays(31);
    public static readonly TimeSpan HeartbeatMaxAge = TimeSpan.FromMinutes(75);
    public static readonly TimeSpan UnknownMaxAge = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan Window = Promotion.ErrorWindow;
    public static readonly TimeSpan CycleErrorWindow = TimeSpan.FromHours(24);

    /// <summary>The audit event m5-henv.ps1 writes to ops when it finishes (ADR-0024 §3).</summary>
    public const string HenvRunEvent = "henv_run_recorded";
}

public static class AlarmSeverity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
}

/// <summary>One active alarm (ADR-0024): code, severity, since when, a short operational detail and the runbook section.</summary>
public sealed record Alarm(string Code, string Severity, DateTimeOffset? Since, string Detail, string Runbook);

/// <summary>A count of events in a window and the earliest of them.</summary>
public sealed record EventCount(int Count, DateTimeOffset? First)
{
    public static readonly EventCount None = new(0, null);
}

/// <summary>Everything the alarms are computed from, read from the ops database at one instant.</summary>
public sealed record AlarmInputs(
    bool TradingKillSwitch,
    string? TradingKillSwitchActor,
    DateTimeOffset? TradingKillSwitchAt,
    bool AiKillSwitch,
    string? AiKillSwitchTrigger,
    DateTimeOffset? AiKillSwitchAt,
    decimal AiMonthUsedUsd,
    EventCount AiAutoTrips,
    string? AiAutoTripLastKind,
    string? HenvVerdict,
    DateTimeOffset? HenvAt,
    DateTimeOffset? LastCycleAt,
    DateTimeOffset? UnprotectedSince,
    DateTimeOffset? OldestUnknownSince,
    int UnknownOrders,
    DateTimeOffset? ResetSuspectedSince,
    EventCount ReconciliationFailures,
    EventCount HashMismatches,
    EventCount CycleErrors);

/// <summary>
/// The computed alarms of ADR-0024 (catalogue m9-m10-protocol §3.2): a pure function of the ops data at one instant.
/// Nothing is stored, acknowledged or sent; the dashboard and <c>GET /alarms</c> read it.
/// </summary>
public static class AlarmEvaluator
{
    public static readonly string[] AutoTriggers = ["MonthlyCap", "SpendVelocity", "CostDeviation", "CircuitBreaker"];

    public static IReadOnlyList<Alarm> Evaluate(AlarmInputs i, AiBudget budget, DateTimeOffset now)
    {
        var alarms = new List<Alarm>();
        void Add(string code, string severity, DateTimeOffset? since, string detail, string runbook) => alarms.Add(new Alarm(code, severity, since, detail, runbook));

        // A1, A2: the two kill switches.
        if (i.TradingKillSwitch)
        {
            Add("trading-kill-switch", AlarmSeverity.Critical, i.TradingKillSwitchAt, $"Trading kill switch engaged by {i.TradingKillSwitchActor ?? "?"}: no new entries; stops and exits stay.", "runbook §1 A1");
        }

        if (i.AiKillSwitch)
        {
            Add("ai-kill-switch", AlarmSeverity.Critical, i.AiKillSwitchAt, $"AI kill switch tripped ({i.AiKillSwitchTrigger ?? "?"}): no LLM call; the allocator falls back to T11.", "runbook §1 A2");
        }

        // A3: this UTC month's committed spend against the monthly cap (B-1).
        if (budget.MonthlyCapUsd > 0)
        {
            var share = i.AiMonthUsedUsd / budget.MonthlyCapUsd;
            if (share >= AlarmThresholds.BudgetWarningShare)
            {
                Add("ai-budget", share >= AlarmThresholds.BudgetCriticalShare ? AlarmSeverity.Critical : AlarmSeverity.Warning, AiSpend.MonthStart(now),
                    $"AI spend this month {Math.Round(i.AiMonthUsedUsd, 4)} of {budget.MonthlyCapUsd} USD ({Math.Round(share * 100, 1)} %).", "runbook §1 A3");
            }
        }

        // A4: automatic AI trips in the last 7 days (a manual trip is not one).
        if (i.AiAutoTrips.Count > 0)
        {
            Add("ai-auto-trip", AlarmSeverity.Warning, i.AiAutoTrips.First, $"{i.AiAutoTrips.Count} automatic AI kill switch trip(s) in 7 days (last: {i.AiAutoTripLastKind}).", "runbook §1 A4");
        }

        // A5: the monthly H-ENV run (B-2).
        if (i.HenvAt is null)
        {
            Add("henv-stale", AlarmSeverity.Warning, null, "No H-ENV run recorded in ops: run scripts/m5-henv.ps1.", "runbook §5");
        }
        else if (i.HenvVerdict != "PASS")
        {
            Add("henv-stale", AlarmSeverity.Critical, i.HenvAt, "The latest H-ENV run failed: do not set the flag; fix and run again.", "runbook §5");
        }
        else if (now - i.HenvAt.Value > AlarmThresholds.HenvMaxAge)
        {
            Add("henv-stale", AlarmSeverity.Warning, i.HenvAt.Value + AlarmThresholds.HenvMaxAge, $"The last passing H-ENV run is {(int)(now - i.HenvAt.Value).TotalDays} days old (monthly).", "runbook §5");
        }

        // A6: heartbeat (B-3); the cycle runs hourly.
        if (i.LastCycleAt is null || now - i.LastCycleAt.Value > AlarmThresholds.HeartbeatMaxAge)
        {
            Add("heartbeat", AlarmSeverity.Critical, i.LastCycleAt, i.LastCycleAt is null
                ? "No trading cycle recorded."
                : $"No trading cycle for {(int)(now - i.LastCycleAt.Value).TotalMinutes} minutes (machine, Docker or Trading.Host down?).", "runbook §3");
        }

        // A7: Testnet and integrity.
        if (i.UnprotectedSince is { } unprotected)
        {
            Add("unprotected-position", AlarmSeverity.Critical, unprotected, "A Testnet position has no accepted protective stop on the exchange; new entries are blocked.", "runbook §1 A7");
        }

        if (i.OldestUnknownSince is { } unknown && now - unknown > AlarmThresholds.UnknownMaxAge)
        {
            Add("order-unknown", AlarmSeverity.Critical, unknown, $"{i.UnknownOrders} exchange order(s) in Unknown or Submitting for more than {AlarmThresholds.UnknownMaxAge.TotalMinutes} minutes.", "runbook §1 A7");
        }

        if (i.ResetSuspectedSince is { } reset)
        {
            Add("testnet-reset-suspected", AlarmSeverity.Critical, reset, "Testnet reset suspected; new entries are blocked until the user's rebaseline.", "runbook §6");
        }

        if (i.ReconciliationFailures.Count > 0)
        {
            Add("reconciliation", AlarmSeverity.Critical, i.ReconciliationFailures.First, $"{i.ReconciliationFailures.Count} failed reconciliation(s) in 7 days.", "runbook §1 A7");
        }

        if (i.HashMismatches.Count > 0)
        {
            Add("strategy-hash-mismatch", AlarmSeverity.Critical, i.HashMismatches.First, $"{i.HashMismatches.Count} strategy hash mismatch(es) in 7 days; the strategy is not run.", "runbook §1 A7");
        }

        if (i.CycleErrors.Count > 0)
        {
            Add("cycle-error", AlarmSeverity.Warning, i.CycleErrors.First, $"{i.CycleErrors.Count} cycle(s) ended in error in 24 hours.", "runbook §1 A7");
        }

        return alarms;
    }

    /// <summary>Reads the inputs as ops_api (SELECT only) at <paramref name="now"/>.</summary>
    public static async Task<AlarmInputs> ReadAsync(NpgsqlConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"""
            WITH t AS (SELECT engaged, actor, recorded_at FROM trading.kill_switch_events ORDER BY id DESC LIMIT 1),
                 a AS (SELECT tripped, trigger_kind, at FROM ai.kill_switch_events ORDER BY id DESC LIMIT 1),
                 auto AS (SELECT count(*)::int AS n, min(at) AS first, (array_agg(trigger_kind ORDER BY at DESC))[1] AS last_kind
                          FROM ai.kill_switch_events WHERE tripped AND trigger_kind = ANY($3) AND at > $1 - $2),
                 h AS (SELECT payload ->> 'verdict' AS verdict, recorded_at FROM audit.events WHERE event = $4 ORDER BY id DESC LIMIT 1),
                 streak AS (
                   SELECT flag, (SELECT min(c.started_at) FROM trading.cycle_metrics c
                                 WHERE c.detail -> 'degraded' ? flag
                                   AND c.id > coalesce((SELECT max(x.id) FROM trading.cycle_metrics x WHERE NOT coalesce(x.detail -> 'degraded' ? flag, false)), 0)) AS since
                   FROM unnest(ARRAY['unprotected-stop', 'testnet-reset-suspected']) flag),
                 u AS (SELECT count(*)::int AS n, min(last_at) AS oldest FROM (
                         SELECT (SELECT max(e.recorded_at) FROM trading.exchange_order_events e WHERE e.order_id = o.id) AS last_at
                         FROM trading.exchange_orders o WHERE trading.order_status(o.id) IN ('Unknown', 'Submitting')) q),
                 r AS (SELECT count(*)::int AS n, min(checked_at) AS first FROM trading.reconciliation_runs WHERE NOT ok AND checked_at > $1 - $2),
                 m AS (SELECT count(*)::int AS n, min(recorded_at) AS first FROM audit.events WHERE event = 'strategy_hash_mismatch' AND recorded_at > $1 - $2),
                 e AS (SELECT count(*)::int AS n, min(started_at) AS first FROM trading.cycle_metrics WHERE outcome = 'error' AND started_at > $1 - $5)
            SELECT (SELECT engaged FROM t), (SELECT actor FROM t), (SELECT recorded_at FROM t),
                   (SELECT tripped FROM a), (SELECT trigger_kind FROM a), (SELECT at FROM a),
                   auto.n, auto.first, auto.last_kind,
                   (SELECT verdict FROM h), (SELECT recorded_at FROM h),
                   (SELECT max(started_at) FROM trading.cycle_metrics),
                   (SELECT since FROM streak WHERE flag = 'unprotected-stop'), u.oldest, u.n,
                   (SELECT since FROM streak WHERE flag = 'testnet-reset-suspected'),
                   r.n, r.first, m.n, m.first, e.n, e.first
            FROM auto, u, r, m, e
            """, connection);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(AlarmThresholds.Window);
        command.Parameters.AddWithValue(AutoTriggers);
        command.Parameters.AddWithValue(AlarmThresholds.HenvRunEvent);
        command.Parameters.AddWithValue(AlarmThresholds.CycleErrorWindow);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        T? Value<T>(int ordinal) => reader.IsDBNull(ordinal) ? default : reader.GetFieldValue<T>(ordinal);
        DateTimeOffset? Time(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
        EventCount Count(int n, int first) => new(reader.GetInt32(n), Time(first));
        var inputs = new AlarmInputs(
            Value<bool>(0), Value<string>(1), Time(2),
            Value<bool>(3), Value<string>(4), Time(5),
            0m,
            Count(6, 7), Value<string>(8),
            Value<string>(9), Time(10),
            Time(11),
            Time(12), Time(13), reader.GetInt32(14),
            Time(15),
            Count(16, 17), Count(18, 19), Count(20, 21));
        await reader.DisposeAsync();
        return inputs with { AiMonthUsedUsd = await AiSpend.CommittedSinceAsync(connection, AiSpend.MonthStart(now), ct) };
    }

    /// <summary>The <c>GET /alarms</c> document: the active alarms (critical first) and the thresholds in force.</summary>
    public static string Document(IReadOnlyList<Alarm> alarms, DateTimeOffset now) => JsonSerializer.Serialize(new
    {
        at = now,
        critical = alarms.Count(a => a.Severity == AlarmSeverity.Critical),
        warning = alarms.Count(a => a.Severity == AlarmSeverity.Warning),
        alarms = alarms.OrderBy(a => a.Severity == AlarmSeverity.Critical ? 0 : 1).ToList(),
        thresholds = new
        {
            budgetWarningShare = AlarmThresholds.BudgetWarningShare,
            budgetCriticalShare = AlarmThresholds.BudgetCriticalShare,
            henvMaxAgeDays = AlarmThresholds.HenvMaxAge.TotalDays,
            heartbeatMaxAgeMinutes = AlarmThresholds.HeartbeatMaxAge.TotalMinutes,
            unknownMaxAgeMinutes = AlarmThresholds.UnknownMaxAge.TotalMinutes,
            windowDays = AlarmThresholds.Window.TotalDays,
        },
    }, JsonSerializerOptions.Web);
}
