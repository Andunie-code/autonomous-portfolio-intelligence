using Npgsql;

namespace PortfolioIntelligence.Execution;

/// <summary>D30 progress of one frozen strategy (m6-protocol §6.1).</summary>
public sealed record PromotionStatus(
    long FrozenId,
    long CohortId,
    int MemberIndex,
    string Name,
    string State,
    DateTimeOffset? PaperSince,
    double Days,
    int Bars,
    int CriticalErrors7d,
    double OutageRatio,
    bool Eligible,
    IReadOnlyList<string> Missing,
    bool Experimental);

/// <summary>
/// The minimum paper period before PaperTrading → TestnetActive (D30, U-M6-3): at least 10 calendar days (D30 rev., U 2026-10-30; m6-protocol v1.2 §14) since the
/// PaperTrading lifecycle event, at least 60 processed 4h bars of the cohort's paper run (bars processed after an
/// outage count), and no critical error in the last 7 days. Critical errors: a failed reconciliation of the run
/// (ledger or state hash: invariant violations), a duplicate fill, a hash mismatch of the strategy, HTTP 418.
/// </summary>
public static class Promotion
{
    public const int MinimumDays = 10;
    public const int MinimumBars = 60;
    public static readonly TimeSpan ErrorWindow = TimeSpan.FromDays(7);

    /// <summary>The D30 conditions not met, as reason codes (empty when the strategy is eligible).</summary>
    public static IReadOnlyList<string> Missing(DateTimeOffset now, DateTimeOffset? paperSince, int bars, int criticalErrors7d)
    {
        var missing = new List<string>();
        if (paperSince is null || now - paperSince.Value < TimeSpan.FromDays(MinimumDays))
        {
            missing.Add("days");
        }

        if (bars < MinimumBars)
        {
            missing.Add("bars");
        }

        if (criticalErrors7d > 0)
        {
            missing.Add("critical-errors");
        }

        return missing;
    }

    /// <summary>D30 progress of every frozen strategy (or one by name), read as ops_api.</summary>
    public static async Task<IReadOnlyList<PromotionStatus>> ReadAsync(NpgsqlConnection connection, DateTimeOffset now, string? name, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            WITH f AS (
              SELECT f.id, f.cohort_id, f.member_index, f.name,
                     coalesce((SELECT l.to_state FROM research.lifecycle_events l WHERE l.frozen_id = f.id ORDER BY l.id DESC LIMIT 1), 'Candidate') AS state,
                     (SELECT max(l.recorded_at) FROM research.lifecycle_events l WHERE l.frozen_id = f.id AND l.to_state = 'PaperTrading') AS paper_since
              FROM research.frozen_strategies f WHERE $2::text IS NULL OR f.name = $2),
            r AS (
              SELECT DISTINCT ON (s.cohort_id) s.cohort_id, p.id AS run_id, p.trading_start
              FROM trading.paper_runs p CROSS JOIN LATERAL (SELECT DISTINCT (x ->> 'cohortId')::bigint AS cohort_id FROM jsonb_array_elements(p.config -> 'sleeves') x) s
              WHERE p.venue = 'paper' ORDER BY s.cohort_id, p.id DESC)
            SELECT f.id, f.cohort_id, f.member_index, f.name, f.state, f.paper_since,
                   (SELECT count(*) FROM trading.steps st WHERE st.run_id = r.run_id AND st.bar_time >= r.trading_start)::int AS bars,
                   ((SELECT count(*) FROM trading.reconciliation_runs x WHERE x.run_id = r.run_id AND NOT x.ok AND x.checked_at > $1 - $3)
                    + (SELECT count(*) FROM (SELECT 1 FROM trading.fills d WHERE d.run_id = r.run_id AND d.fill_time > $1 - $3
                                             GROUP BY d.sleeve, d.symbol, d.fill_time, d.side HAVING count(*) > 1) dup)
                    + (SELECT count(*) FROM audit.events a WHERE a.event = 'strategy_hash_mismatch' AND a.payload ->> 'name' = f.name AND a.recorded_at > $1 - $3)
                    + (SELECT count(*) FROM trading.cycle_metrics c WHERE c.outcome = 'ip-banned' AND c.started_at > $1 - $3))::int AS errors,
                   (SELECT count(DISTINCT date_trunc('hour', c.started_at)) FROM trading.cycle_metrics c WHERE f.paper_since IS NOT NULL AND c.started_at >= f.paper_since)::int AS hours,
                   (SELECT s.summary ->> 'outcome' FROM research.cohort_status s WHERE s.cohort_id = f.cohort_id AND s.status = 'revealed' ORDER BY s.id DESC LIMIT 1) AS revealed
            FROM f LEFT JOIN r ON r.cohort_id = f.cohort_id
            ORDER BY f.cohort_id, f.member_index
            """, connection);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue((object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue(ErrorWindow);
        var result = new List<PromotionStatus>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var since = reader.IsDBNull(5) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(5);
            var bars = reader.GetInt32(6);
            var errors = reader.GetInt32(7);
            var elapsedHours = since is { } s ? Math.Max(1, Math.Ceiling((now - s).TotalHours)) : 0;
            var outage = since is null ? 0 : Math.Round(Math.Clamp(1 - reader.GetInt32(8) / elapsedHours, 0, 1), 4);
            var missing = Missing(now, since, bars, errors);
            result.Add(new PromotionStatus(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), since,
                since is { } t ? Math.Round((now - t).TotalDays, 2) : 0, bars, errors, outage, missing.Count == 0, missing,
                // Validation is derived only from the evaluator's results (ADR-0012); ops sees a revealed outcome at most.
                Experimental: reader.IsDBNull(9) || reader.GetString(9) != "Validated"));
        }

        return result;
    }
}
