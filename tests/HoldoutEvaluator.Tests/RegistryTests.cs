using System.Text.Json.Nodes;
using Npgsql;
using PortfolioIntelligence.HoldoutEvaluator.Tests.Support;

namespace PortfolioIntelligence.HoldoutEvaluator.Tests;

/// <summary>Cohort registry: server-assigned times, immutability, limits and family identity (architecture §6.3; V5, V12, V13, H15–H18).</summary>
public class RegistryTests(EvaluatorDatabase fixture) : IClassFixture<EvaluatorDatabase>
{
    [Fact]
    public async Task Registration_assigns_registered_at_A_c_and_the_window_end_on_the_server()
    {
        var db = await fixture.NewHoldoutAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var r = await new RegistryService(db, new StatusRelay(db)).RegisterAsync(Manifests.Build([Manifests.Trend], months: 7), default);

        Assert.InRange(r.RegisteredAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
        var expected = new DateTimeOffset(r.RegisteredAt.AddHours(24).UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
        Assert.Equal(expected, r.StartsAt);
        Assert.True(r.StartsAt > DateTimeOffset.UtcNow);
        Assert.Equal(r.StartsAt.AddMonths(7), r.WindowEnd);
        Assert.Equal([1], r.Attempts);
        Assert.True(r.ProtocolComplete);
        Assert.Equal(1L, await EvaluatorDatabase.ScalarAsync<long>(fixture.OpsConnectionString,
            $"SELECT count(*) FROM research.cohort_status WHERE cohort_id = {r.CohortId} AND status = 'sealed'"));
    }

    [Fact]
    public async Task First_midnight_after_registered_plus_24h_is_strict()
    {
        var db = await fixture.NewHoldoutAsync();
        var connection = ConnectionOf(db);

        Assert.Equal(new DateTime(2026, 3, 3), (await EvaluatorDatabase.ScalarAsync<DateTime>(connection,
            "SELECT results.cohort_start(timestamptz '2026-03-01 00:00:00+00')")).Date);
        Assert.Equal(new DateTime(2026, 3, 3), (await EvaluatorDatabase.ScalarAsync<DateTime>(connection,
            "SELECT results.cohort_start(timestamptz '2026-03-01 23:59:59+00')")).Date);
    }

    [Fact]
    public async Task Client_supplied_times_are_rejected_in_the_manifest_and_in_the_database()
    {
        var db = await fixture.NewHoldoutAsync();
        var registry = new RegistryService(db, null);

        var e = await Assert.ThrowsAsync<ManifestException>(() => registry.RegisterAsync(Manifests.Build([Manifests.Trend], edit: m => m["aC"] = "2020-01-01T00:00:00Z"), default));
        Assert.Contains(e.Errors, m => m.Contains("assigned by the server"));

        var direct = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(ConnectionOf(db), $"""
            INSERT INTO results.cohort_manifests (registered_at, l_c_months, manifest, manifest_hash, protocol_version, protocol_complete, underpowered, engine_sha, image_digest)
            VALUES (timestamptz '2020-01-01', 6, '{"{}"}', '{new string('a', 64)}', 'v1', true, false, '{EvaluatorDatabase.EngineSha}', '{EvaluatorDatabase.Digest}')
            """));
        Assert.Contains("assigned by the server", direct.MessageText);

        var seedBypass = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(ConnectionOf(db), $"""
            INSERT INTO results.cohort_manifests (kind, registered_at, l_c_months, manifest, manifest_hash, protocol_version, protocol_complete, underpowered, engine_sha, image_digest)
            VALUES ('seed', timestamptz '2020-01-01', 6, '{"{}"}', '{new string('b', 64)}', 'v1', true, false, '{EvaluatorDatabase.EngineSha}', '{EvaluatorDatabase.Digest}')
            """));
        Assert.Contains("seed_cohort", seedBypass.MessageText);
    }

    [Fact]
    public async Task Engine_sha_that_does_not_match_the_release_digest_is_rejected()
    {
        var db = await fixture.NewHoldoutAsync();

        var e = await Assert.ThrowsAsync<RegistryRejectedException>(() =>
            new RegistryService(db, null).RegisterAsync(Manifests.Build([Manifests.Trend], engineSha: "ffffffffffffffffffffffffffffffffffffffff"), default));

        Assert.Contains("does not match the evaluator release", e.Message);
    }

    [Fact]
    public async Task Frozen_cohorts_cannot_be_changed_deleted_or_extended()
    {
        var db = await fixture.NewHoldoutAsync();
        var r = await new RegistryService(db, null).RegisterAsync(Manifests.Build([Manifests.Trend]), default);
        var connection = ConnectionOf(db);

        foreach (var sql in new[]
        {
            $"UPDATE results.cohort_manifests SET l_c_months = 12 WHERE cohort_id = {r.CohortId}",
            $"DELETE FROM results.cohort_manifests WHERE cohort_id = {r.CohortId}",
            $"DELETE FROM results.cohort_members WHERE cohort_id = {r.CohortId}",
        })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(connection, sql));
            Assert.Contains("append-only", e.MessageText);
        }

        var late = await Assert.ThrowsAsync<PostgresException>(() => EvaluatorDatabase.ExecuteAsync(connection, $"""
            INSERT INTO results.cohort_members (cohort_id, member_index, strategy_name, definition_hash, structure_hash)
            VALUES ({r.CohortId}, 2, 'late', '{new string('c', 64)}', '{new string('d', 64)}')
            """, "eval_registry"));
        Assert.Contains("frozen", late.MessageText);
    }

    [Fact]
    public async Task A_modified_strategy_is_a_new_cohort_with_a_future_start_in_the_same_family()
    {
        var db = await fixture.NewHoldoutAsync();
        var registry = new RegistryService(db, null);
        var first = await registry.RegisterAsync(Manifests.Build([Manifests.Trend]), default);

        var tuned = await registry.RegisterAsync(Manifests.Build([Manifests.Trend.Replace("\"period\": 30", "\"period\": 40")]), default);

        Assert.NotEqual(first.CohortId, tuned.CohortId);
        Assert.True(tuned.StartsAt > DateTimeOffset.UtcNow);
        Assert.Equal([2], tuned.Attempts); // parameter change: same family, second attempt (H17)
    }

    [Fact]
    public async Task Fourth_strategy_and_third_open_cohort_are_rejected()
    {
        var db = await fixture.NewHoldoutAsync();
        var registry = new RegistryService(db, null);

        var four = await Assert.ThrowsAsync<ManifestException>(() =>
            registry.RegisterAsync(Manifests.Build(Enumerable.Range(0, 4).Select(Manifests.Family)), default));
        Assert.Contains(four.Errors, m => m.Contains("1–3"));

        await registry.RegisterAsync(Manifests.Build([Manifests.Family(0), Manifests.Family(1), Manifests.Family(2)]), default);
        await registry.RegisterAsync(Manifests.Build([Manifests.Family(3)]), default);
        var third = await Assert.ThrowsAsync<RegistryRejectedException>(() => registry.RegisterAsync(Manifests.Build([Manifests.Family(4)]), default));

        Assert.Contains("open cohort limit", third.Message);
        Assert.True(await EvaluatorDatabase.ScalarAsync<long>(ConnectionOf(db), "SELECT count(*) FROM results.audit_log WHERE event = 'manifest-rejected'") >= 1);
    }

    [Fact]
    public async Task Members_beyond_three_are_rejected_even_when_the_registry_service_is_bypassed()
    {
        var db = await fixture.NewHoldoutAsync();
        var manifest = ManifestParser.Parse(Manifests.Build([Manifests.Family(0), Manifests.Family(1), Manifests.Family(2)]));
        await using var npgsql = await db.OpenAsync("eval_registry", default);
        await using var transaction = await npgsql.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO results.cohort_manifests (l_c_months, manifest, manifest_hash, protocol_version, protocol_complete, underpowered, engine_sha, image_digest)
            VALUES (6, '{"{}"}', '{manifest.Hash}', 'v1', true, false, '{EvaluatorDatabase.EngineSha}', '{EvaluatorDatabase.Digest}') RETURNING cohort_id
            """, npgsql, transaction);
        var id = (long)(await insert.ExecuteScalarAsync())!;
        await RegistryService.InsertMembersAsync(npgsql, transaction, id, manifest, default);

        await using var fourth = new NpgsqlCommand($"""
            INSERT INTO results.cohort_members (cohort_id, member_index, strategy_name, definition_hash, structure_hash)
            VALUES ({id}, 3, 'x', '{new string('e', 64)}', '{new string('f', 64)}')
            """, npgsql, transaction);
        var e = await Assert.ThrowsAsync<PostgresException>(() => fourth.ExecuteNonQueryAsync());
        Assert.Contains("at most 3", e.MessageText);
    }

    [Fact]
    public async Task Third_prospective_attempt_of_a_family_is_rejected_and_client_family_ids_are_ignored()
    {
        var db = await fixture.NewHoldoutAsync();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        // Two closed seed cohorts of the same family (parameter variants) use both attempts.
        await Seeding.SeedClosedAsync(db, Manifests.Build([Manifests.Trend]), clock);
        await Seeding.SeedClosedAsync(db, Manifests.Build([Manifests.Trend.Replace("\"period\": 5", "\"period\": 6")]), clock);

        var claimed = Manifests.Build([Manifests.Trend.Replace("\"period\": 5", "\"period\": 7")], edit: m =>
        {
            m["strategies"]![0]!["familyId"] = 999;
        });
        var e = await Assert.ThrowsAsync<RegistryRejectedException>(() => new RegistryService(db, null).RegisterAsync(claimed, default));

        Assert.Contains("family quota", e.Message);
    }

    [Fact]
    public async Task Incomplete_protocol_is_registered_but_not_eligible_for_validation()
    {
        var db = await fixture.NewHoldoutAsync();

        var r = await new RegistryService(db, null).RegisterAsync(Manifests.Build([Manifests.Trend], edit: m => ((JsonObject)m["protocol"]!).Remove("alpha")), default);

        Assert.False(r.ProtocolComplete);
        Assert.False(await EvaluatorDatabase.ScalarAsync<bool>(ConnectionOf(db), $"SELECT protocol_complete FROM results.cohort_manifests WHERE cohort_id = {r.CohortId}"));
    }

    internal static string ConnectionOf(HoldoutDatabase db) => db.ConnectionString;
}
