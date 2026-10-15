using Npgsql;
using PortfolioIntelligence.HoldoutEvaluator.Tests.Support;

namespace PortfolioIntelligence.HoldoutEvaluator.Tests;

/// <summary>Prospective and diagnostic evaluation on synthetic data (architecture §6.5; V1–V4, V6, V7, V11, H9, H11).</summary>
public class EvaluationTests(EvaluatorDatabase fixture) : IClassFixture<EvaluatorDatabase>
{
    private static readonly FixedClock After = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task Early_evaluation_is_rejected_by_the_application_clock_and_by_the_database_trigger()
    {
        var db = await fixture.NewHoldoutAsync();
        var r = await new RegistryService(db, null).RegisterAsync(Manifests.Build([Manifests.Trend]), default);

        // Application layer: injected clock before window end + buffer.
        var early = new FixedClock(r.WindowEnd.AddDays(1));
        var service = new ProspectiveService(db, new SyntheticWindowSource(1), early, null);
        Assert.Empty(await service.RunDueAsync(default));
        await Assert.ThrowsAsync<EarlyEvaluationException>(() => service.RerunAsync(r.CohortId, "rerun-data-quality", "{\"report\":\"x\"}", default));

        // Database layer: the application clock is fooled into the future, the trigger still uses now().
        var fooled = new ProspectiveService(db, new SyntheticWindowSource(1), new FixedClock(r.WindowEnd.AddDays(10)), null);
        var e = await Assert.ThrowsAsync<PostgresException>(() => fooled.RunDueAsync(default));
        Assert.Contains("has not closed", e.MessageText);

        foreach (var sql in new[]
        {
            $"INSERT INTO prospective.candles VALUES ({r.CohortId}, 'AAAUSDT', now(), 1, 1, 1, 1, 1)",
            $"INSERT INTO results.prospective_runs (cohort_id, reason, manifest_hash, image_digest, data_digest, result_hash) SELECT {r.CohortId}, 'initial', manifest_hash, image_digest, '{new string('a', 64)}', '{new string('b', 64)}' FROM results.cohort_manifests",
        })
        {
            var trigger = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, sql, "eval_prospective"));
            Assert.Contains("has not closed", trigger.MessageText);
        }
    }

    [Fact]
    public async Task Planted_edge_validates_only_in_a_verified_environment()
    {
        var unverified = await fixture.NewHoldoutAsync();
        var id = await Seeding.SeedAsync(unverified, Manifests.Build([Manifests.Trend]));
        await new ProspectiveService(unverified, new SyntheticWindowSource(3, drift: 0.003), After, new StatusRelay(unverified)).RunDueAsync(default);
        Assert.Equal(("Invalid", "unverified environment"), await StatusAsync(unverified, id));

        var verified = await fixture.NewHoldoutAsync();
        await EvaluatorDatabase.ExecuteAsync(verified.ConnectionString, "INSERT INTO results.environment_flags (verified, actor) VALUES (true, 'test-user')");
        var validated = await Seeding.SeedAsync(verified, Manifests.Build([Manifests.Trend]));
        await new ProspectiveService(verified, new SyntheticWindowSource(3, drift: 0.003), After, new StatusRelay(verified)).RunDueAsync(default);

        Assert.Equal(("Validated", (string?)null), await StatusAsync(verified, validated));
        // The ops database is shared by every test database here, so cohort ids repeat across tests.
        Assert.True(await EvaluatorDatabase.ScalarAsync<long>(fixture.OpsConnectionString,
            $"SELECT count(*) FROM research.cohort_status WHERE cohort_id = {validated} AND status = 'revealed'") >= 1);
    }

    [Theory]
    [InlineData(-0.003, false, 1, "Failed")]
    [InlineData(0.003, true, 1, "Inconclusive")]
    [InlineData(0.003, false, 100_000, "Inconclusive")]
    public async Task Decision_order_fails_losers_and_keeps_underpowered_or_thin_cohorts_inconclusive(double drift, bool underpowered, int plannedTrades, string expected)
    {
        var db = await fixture.NewHoldoutAsync();
        await EvaluatorDatabase.ExecuteAsync(db.ConnectionString, "INSERT INTO results.environment_flags (verified, actor) VALUES (true, 'test-user')");
        var id = await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend], underpowered: underpowered, plannedTrades: plannedTrades));

        await new ProspectiveService(db, new SyntheticWindowSource(3, drift: drift), After, null).RunDueAsync(default);

        Assert.Equal(expected, (await StatusAsync(db, id)).Status);
    }

    [Fact]
    public async Task Incomplete_protocol_is_at_best_inconclusive()
    {
        var db = await fixture.NewHoldoutAsync();
        await EvaluatorDatabase.ExecuteAsync(db.ConnectionString, "INSERT INTO results.environment_flags (verified, actor) VALUES (true, 'test-user')");
        var id = await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend], edit: m => ((System.Text.Json.Nodes.JsonObject)m["protocol"]!).Remove("maxDrawdown")));

        await new ProspectiveService(db, new SyntheticWindowSource(3, drift: 0.003), After, null).RunDueAsync(default);

        Assert.Equal(("Inconclusive", "protocol incomplete"), await StatusAsync(db, id));
    }

    [Fact]
    public async Task Validated_cannot_be_written_for_underpowered_cohorts_or_by_any_other_role()
    {
        var db = await fixture.NewHoldoutAsync();
        await EvaluatorDatabase.ExecuteAsync(db.ConnectionString, "INSERT INTO results.environment_flags (verified, actor) VALUES (true, 'test-user')");
        var id = await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend], underpowered: true));
        await new ProspectiveService(db, new SyntheticWindowSource(1), After, null).RunDueAsync(default);
        var insert = $"INSERT INTO results.prospective_results (run_id, cohort_id, member_index, status, summary) SELECT run_id, {id}, 1, 'Validated', '{{}}' FROM results.prospective_runs LIMIT 1";

        var underpowered = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, insert, "eval_prospective"));
        Assert.Contains("underpowered", underpowered.MessageText);
        foreach (var role in new[] { "eval_registry", "eval_diagnostic", "research_app", "trading_app" })
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, insert, role));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        var view = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString,
            $"INSERT INTO results.validation_status (cohort_id, member_index, strategy_name, definition_hash, status) VALUES ({id}, 1, 'x', 'y', 'Validated')", "eval_prospective"));
        // Either the missing grant or the non-updatable view stops it, whichever PostgreSQL checks first.
        Assert.Contains(view.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.ObjectNotInPrerequisiteState });

        var diagnostic = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, $$"""
            INSERT INTO results.diagnostic_results (structure_hash, definition_hash, window_start, window_end, verdict, knowledge_overlap, metrics, result_hash)
            VALUES ('{{new string('a', 64)}}', '{{new string('a', 64)}}', timestamptz '2026-01-01', timestamptz '2026-03-01', 'Validated', false, '{}', '{{new string('a', 64)}}')
            """, "eval_diagnostic"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, diagnostic.SqlState);

        var relay = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(fixture.OpsConnectionString,
            "INSERT INTO research.cohort_status (cohort_id, status, summary) VALUES (1, 'Validated', '{}')", "status_relay"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, relay.SqlState);
        var research = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(fixture.OpsConnectionString,
            "INSERT INTO research.cohort_status (cohort_id, status) VALUES (1, 'sealed')", "research_app"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, research.SqlState);
    }

    [Fact]
    public async Task Validation_status_is_derived_only_from_prospective_results()
    {
        var db = await fixture.NewHoldoutAsync();
        var definition = await EvaluatorDatabase.ScalarAsync<string>(db.ConnectionString, "SELECT pg_get_viewdef('results.validation_status'::regclass)");

        Assert.Contains("prospective_results", definition);
        Assert.Contains("cohort_members", definition);
        Assert.DoesNotContain("research.", definition);
        Assert.DoesNotContain("paper", definition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("testnet", definition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnostic", definition);
    }

    [Theory]
    [InlineData("eval_diagnostic", "SELECT 1 FROM prospective.candles")]
    [InlineData("eval_diagnostic", "SELECT 1 FROM results.prospective_results")]
    [InlineData("eval_diagnostic", "SELECT 1 FROM results.cohort_manifests")]
    [InlineData("eval_prospective", "SELECT 1 FROM diagnostic.candles")]
    [InlineData("eval_prospective", "SELECT 1 FROM results.diagnostic_results")]
    [InlineData("eval_prospective", "INSERT INTO results.diagnostic_results (structure_hash) VALUES ('x')")]
    [InlineData("eval_registry", "SELECT 1 FROM prospective.candles")]
    [InlineData("eval_registry", "SELECT 1 FROM diagnostic.candles")]
    [InlineData("eval_registry", "SELECT 1 FROM results.prospective_results")]
    [InlineData("research_app", "SELECT 1 FROM results.cohort_manifests")]
    [InlineData("research_app", "SELECT 1 FROM prospective.candles")]
    [InlineData("research_app", "SELECT 1 FROM diagnostic.candles")]
    [InlineData("trading_app", "SELECT 1 FROM results.prospective_results")]
    [InlineData("trading_app", "SELECT 1 FROM diagnostic.candles")]
    public async Task Roles_cannot_cross_read_or_write_holdout_schemas(string role, string sql)
    {
        var db = await fixture.NewHoldoutAsync();

        var e = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, sql, role));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState);
    }

    [Fact]
    public async Task Same_inputs_give_the_same_result_hash()
    {
        var hashes = new List<string>();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var db = await fixture.NewHoldoutAsync();
            await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend, Manifests.Family(1)]));
            await new ProspectiveService(db, new SyntheticWindowSource(5, drift: 0.001), After, null).RunDueAsync(default);
            hashes.Add(await EvaluatorDatabase.ScalarAsync<string>(db.ConnectionString, "SELECT result_hash FROM results.prospective_runs"));
        }

        Assert.Equal(hashes[0], hashes[1]);
    }

    [Fact]
    public async Task Invalid_re_evaluation_needs_evidence_reuses_the_same_data_is_limited_and_audited()
    {
        var db = await fixture.NewHoldoutAsync();
        var id = await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend]));
        var service = new ProspectiveService(db, new SyntheticWindowSource(2), After, null);
        await service.RunDueAsync(default);

        await Assert.ThrowsAsync<RegistryRejectedException>(() => service.RerunAsync(id, "rerun-data-quality", "{}", default));
        await service.RerunAsync(id, "rerun-data-quality", "{\"importerReport\":\"gap\"}", default);
        await service.RerunAsync(id, "rerun-environment", "{\"verification\":\"pending\"}", default);
        var limit = await Assert.ThrowsAsync<RegistryRejectedException>(() => service.RerunAsync(id, "rerun-data-quality", "{\"importerReport\":\"gap\"}", default));
        Assert.Contains("limit", limit.Message);

        var otherData = new ProspectiveService(db, new SyntheticWindowSource(99), After, null);
        var different = await Assert.ThrowsAsync<RegistryRejectedException>(() => otherData.RerunAsync(id, "rerun-nondeterminism", "{\"hashes\":[\"a\",\"b\"]}", default));
        Assert.Contains("same cohort, window and data", different.Message);

        Assert.Equal(3L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM results.prospective_runs"));
        Assert.Equal(3L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM results.audit_log WHERE event = 'prospective-run'"));
    }

    [Fact]
    public async Task Audit_records_manifest_run_result_and_disclosure_with_hashes()
    {
        var db = await fixture.NewHoldoutAsync();
        await Seeding.SeedAsync(db, Manifests.Build([Manifests.Trend]));
        await new ProspectiveService(db, new SyntheticWindowSource(1), After, null).RunDueAsync(default);

        foreach (var @event in new[] { "seed-cohort", "manifest-registered", "prospective-run", "prospective-result", "disclosure" })
        {
            Assert.Equal(1L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, $"SELECT count(*) FROM results.audit_log WHERE event = '{@event}'"));
        }

        Assert.Equal(0L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString,
            "SELECT count(*) FROM results.audit_log WHERE payload_hash <> encode(sha256(convert_to(payload::text, 'UTF8')), 'hex')"));
        var e = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(db.ConnectionString, "DELETE FROM results.audit_log"));
        Assert.Contains("append-only", e.MessageText);
    }

    [Fact]
    public async Task Diagnostic_quota_is_one_per_family_and_ten_per_period_and_rejections_are_audited()
    {
        var db = await fixture.NewHoldoutAsync();
        var service = new DiagnosticService(db, new SyntheticWindowSource(4), new FixedClock(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)));
        var symbols = new[] { "AAAUSDT", "BBBUSDT" };

        for (var i = 0; i < 10; i++)
        {
            var verdict = await service.EvaluateAsync(new DiagnosticRequest(Manifests.Family(i), symbols, null), default);
            Assert.Contains(verdict, new[] { "NotRejected", "Rejected" });
        }

        var sameFamily = await Assert.ThrowsAsync<RegistryRejectedException>(() =>
            service.EvaluateAsync(new DiagnosticRequest(Manifests.Family(0).Replace("\"period\": 7", "\"period\": 9"), symbols, null), default));
        Assert.Contains("family already evaluated", sameFamily.Message);
        var eleventh = await Assert.ThrowsAsync<RegistryRejectedException>(() => service.EvaluateAsync(new DiagnosticRequest(Manifests.Family(10), symbols, null), default));
        Assert.Contains("10 evaluations", eleventh.Message);
        Assert.Equal(2L, await EvaluatorDatabase.ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM results.audit_log WHERE event = 'diagnostic-rejected'"));
    }

    [Fact]
    public async Task Diagnostic_and_manifest_inserts_are_not_blocked_by_the_early_evaluation_trigger()
    {
        var db = await fixture.NewHoldoutAsync();
        await new RegistryService(db, null).RegisterAsync(Manifests.Build([Manifests.Trend]), default);
        var service = new DiagnosticService(db, new SyntheticWindowSource(4), new FixedClock(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)));

        var verdict = await service.EvaluateAsync(new DiagnosticRequest(Manifests.Trend, ["AAAUSDT"], new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)), default);

        Assert.Contains(verdict, new[] { "NotRejected", "Rejected" });
        Assert.True(await EvaluatorDatabase.ScalarAsync<bool>(db.ConnectionString, "SELECT knowledge_overlap FROM results.diagnostic_results"));
    }

    private static async Task<(string Status, string? Detail)> StatusAsync(HoldoutDatabase db, long cohortId)
    {
        await using var connection = await db.OpenAsync(null, default);
        await using var command = new NpgsqlCommand($"SELECT status, detail FROM results.validation_status WHERE cohort_id = {cohortId} AND member_index = 1", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
