using Npgsql;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// Ops database grants with real logins (m5-protocol §4.2; DB parts of H6b, H21, H23, H25, H26; ADR-0012 lifecycle).
/// The real-environment versions (network, pg_hba) run in H-ENV.
/// </summary>
public class RoleTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private static async Task Denied(OpsDatabase.OpsDb db, string role, string sql)
    {
        var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql, role));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState);
    }

    [Fact]
    public async Task Research_cannot_read_live_or_trading_data_and_sees_only_revealed_cohorts()
    {
        var db = await fixture.NewAsync();
        await db.ExecuteAsync("INSERT INTO market.live_candles VALUES ('BTCUSDT', '4h', timestamptz '2026-03-01 00:00+00', 1, 2, 0.5, 1.5, 1, 1, 1)", "trading_app");
        foreach (var table in new[] { "market.live_candles", "market.live_candle_anomalies", "trading.fills", "trading.steps", "trading.engine_events", "trading.balance_snapshots", "trading.correlation_snapshots", "trading.paper_runs" })
        {
            await Denied(db, "research_app", $"SELECT * FROM {table}");
        }

        await db.ExecuteAsync("SELECT * FROM research.candles", "research_app");
        await db.ExecuteAsync("SELECT * FROM market.candles", "research_app");

        // Paper fills for cohorts 1 and 2; only cohort 2 is revealed (by status_relay).
        await db.ExecuteAsync("INSERT INTO trading.paper_runs (trading_start, warmup_start, config, config_hash) VALUES (now(), now() - interval '1 day', '{}', repeat('a', 64))", "trading_app");
        foreach (var cohort in new[] { 1, 2 })
        {
            await db.ExecuteAsync($"""
                INSERT INTO trading.fills (run_id, bar_time, sleeve, strategy, cohort_id, symbol, side, fill_time, price, quantity, fee, reason)
                VALUES (1, now(), 0, 's', {cohort}, 'BTCUSDT', 'buy', now(), 1, 1, 0, 'Entry')
                """, "trading_app");
        }

        await db.ExecuteAsync("INSERT INTO research.cohort_status (cohort_id, status) VALUES (1, 'sealed')", "status_relay");
        await db.ExecuteAsync("INSERT INTO research.cohort_status (cohort_id, status, summary) VALUES (2, 'revealed', '{\"trades\": 1}')", "status_relay");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM research.revealed_paper_summary", "research_app"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT cohort_id FROM research.revealed_paper_summary", "research_app"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT cohort_id FROM research.revealed_cohorts", "research_app"));
    }

    [Fact]
    public async Task The_dashboard_grants_of_ops_api_are_read_only_and_research_gains_nothing()
    {
        // Migration 0008 (ADR-0020 §4): ops_api reads the AI ledger, traces and kill switch, the audit trail, the
        // strategy registry and the frozen-strategy provenance view; it writes none of them.
        var db = await fixture.NewAsync();
        foreach (var table in new[] { "ai.cost_ledger", "ai.decision_traces", "ai.kill_switch_events", "audit.events", "research.strategy_versions", "research.frozen_provenance" })
        {
            await db.ExecuteAsync($"SELECT * FROM {table}", "ops_api");
        }

        await Denied(db, "ops_api", "INSERT INTO ai.kill_switch_events (at, tripped, actor, trigger_kind, reason) VALUES (now(), true, 'x', 'Manual', 'x')");
        await Denied(db, "ops_api", "INSERT INTO research.strategy_versions (name, definition_hash, definition) VALUES ('x', repeat('a', 64), '{}')");
        await Denied(db, "ops_api", "SELECT * FROM research.experiments");
        await Denied(db, "ops_api", "SELECT * FROM ai.qualification_protocols");
        await Denied(db, "research_app", "SELECT * FROM research.frozen_provenance");
        await Denied(db, "research_app", "SELECT * FROM trading.ops_actions");
        await Denied(db, "trading_app", "SELECT * FROM audit.events");
    }

    [Fact]
    public async Task Only_research_reads_and_appends_web_sources_and_the_cutoff_check_holds_for_its_login()
    {
        // Migration 0010 (ADR-0022 §7): sources.* belongs to Research.Host; trading and the dashboard see nothing of it.
        var db = await fixture.NewAsync();
        const string document = """
            INSERT INTO sources.source_documents (url, canonical_url, kind, tier, text_kind, arxiv_id, arxiv_version, published_at, version_at, fetched_at,
                content_sha256, text_sha256, bytes, content_type, extractor_version, normalizer_version, hidden_characters, removed_characters, truncated)
            VALUES ('https://arxiv.org/pdf/2301.01234v1', 'https://arxiv.org/abs/2301.01234v1', 'ArxivPdf', 'T1', 'Pdf', '2301.01234', 1,
                timestamptz '2023-01-02 00:00+00', timestamptz '{0}', now(), repeat('a', 64), repeat('b', 64), 10, 'application/pdf', 'x', 'y', 0, 0, false)
            """;
        await db.ExecuteAsync(string.Format(document, "2023-01-02 00:00+00"), "research_app");
        await db.ExecuteAsync("INSERT INTO sources.research_requests (topic, categories, status) VALUES ('momentum', '{q-fin}', 'recorded')", "research_app");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM sources.source_documents", "research_app"));
        var late = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(string.Format(document, "2026-01-01 00:00+00"), "research_app"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, late.SqlState);
        await Denied(db, "research_app", "UPDATE sources.source_documents SET truncated = true");
        foreach (var role in new[] { "trading_app", "ops_api", "market_import", "status_relay" })
        {
            await Denied(db, role, "SELECT * FROM sources.source_documents");
            await Denied(db, role, "SELECT * FROM sources.findings");
        }
    }

    [Fact]
    public async Task Status_relay_only_inserts_cohort_status_and_research_cannot_write_it()
    {
        var db = await fixture.NewAsync();
        await Denied(db, "research_app", "INSERT INTO research.cohort_status (cohort_id, status) VALUES (1, 'sealed')");
        foreach (var sql in new[]
                 {
                     "SELECT * FROM research.cohort_status",
                     "SELECT * FROM market.candles",
                     "SELECT * FROM trading.fills",
                     "INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ('x', 'x', '{}', repeat('a', 64))",
                     "INSERT INTO trading.kill_switch_events (engaged, actor, reason) VALUES (true, 'x', 'x')",
                 })
        {
            await Denied(db, "status_relay", sql);
        }

        await db.ExecuteAsync("INSERT INTO research.cohort_status (cohort_id, status) VALUES (1, 'sealed')", "status_relay");
    }

    [Fact]
    public async Task Risk_config_and_operational_actions_are_written_only_by_the_ops_api_role()
    {
        var db = await fixture.NewAsync();
        const string Risk = """
            INSERT INTO trading.risk_config_versions (actor, reason, config, config_hash)
            VALUES ('x', 'x', '{"experimentalPairsMaxExposure": 0.5}', repeat('a', 64))
            """;
        foreach (var role in new[] { "trading_app", "research_app", "market_import", "status_relay" })
        {
            await Denied(db, role, Risk);
            await Denied(db, role, "INSERT INTO trading.ops_actions (actor, action, reason) VALUES ('x', 'close-all', 'x')");
        }

        await db.ExecuteAsync(Risk, "ops_api");
        await db.ExecuteAsync("INSERT INTO trading.ops_actions (actor, action, reason) VALUES ('user', 'close-all', 'x')", "ops_api");
        var outOfRange = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Risk.Replace("0.5", "1.5"), "ops_api"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, outOfRange.SqlState);
        await Denied(db, "ops_api", "INSERT INTO market.live_candles VALUES ('BTCUSDT', '4h', timestamptz '2026-03-01 00:00+00', 1, 2, 0.5, 1.5, 1, 1, 1)");
        await Denied(db, "ops_api", "INSERT INTO research.cohort_status (cohort_id, status) VALUES (1, 'sealed')");
        await Denied(db, "ops_api", "INSERT INTO research.strategy_versions (name, definition_hash, definition) VALUES ('x', repeat('b', 64), '{}')");
    }

    [Fact]
    public async Task Connect_is_not_granted_to_public_and_every_m5_table_is_append_only()
    {
        var db = await fixture.NewAsync();
        var acl = await db.ScalarAsync<string>("SELECT datacl::text FROM pg_database WHERE datname = current_database()");
        Assert.DoesNotMatch(@"(^|[{,])=[A-Za-z]*c", acl);
        foreach (var role in OpsDatabase.Roles)
        {
            Assert.Contains(role + "=c", acl);
        }

        await db.ExecuteAsync("INSERT INTO trading.cycle_metrics (scheduled_for, started_at, finished_at, bars_processed, catch_up, stale, outcome, detail) VALUES (now(), now(), now(), 0, false, false, 'ok', '{}')", "trading_app");
        foreach (var sql in new[] { "UPDATE trading.cycle_metrics SET outcome = 'x'", "DELETE FROM trading.cycle_metrics" })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql));
            Assert.Contains("append-only", e.MessageText);
        }

        var appendOnly = await db.ScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE t.tgname = 'append_only' AND n.nspname IN ('market', 'research', 'trading')
            """);
        Assert.Equal(25L, appendOnly); // M5: 16 tables; M6 (migration 0009): run closures, the outbox's orders, events and fills, rule snapshots, baselines; M8 (0011): allocator mode events, proposals, shadow steps
    }

    [Fact]
    public async Task Lifecycle_allows_only_the_operational_transitions_and_needs_parity_evidence_for_paper()
    {
        var db = await fixture.NewAsync();
        await db.SeedKohort0Async(paper: false);
        const string Insert = "INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES (1, $1, $2, 'test', $3::jsonb)";

        var noEvidence = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Insert, "research_app", "Frozen", "PaperTrading", "{}"));
        Assert.Contains("parity replay evidence", noEvidence.MessageText);
        var testnet = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Insert, "research_app", "Frozen", "TestnetActive", "{}"));
        Assert.Contains("not allowed", testnet.MessageText);
        var stale = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Insert, "research_app", "Candidate", "Frozen", "{}"));
        Assert.Contains("not the current state", stale.MessageText);
        await Denied(db, "trading_app", "INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES (1, 'Frozen', 'Disabled', 'x', '{}')");

        await db.ExecuteAsync(Insert, "research_app", "Frozen", "PaperTrading", "{\"parityReportHash\": \"abc\"}");
        await db.ExecuteAsync(Insert, "ops_api", "PaperTrading", "Disabled", "{}");
    }
}
