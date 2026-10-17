using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PortfolioIntelligence.Ai.Persistence;
using PortfolioIntelligence.Ai.Tests.Support;
using PortfolioIntelligence.Infrastructure;
using PortfolioIntelligence.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>Migration 0006 (ai schema): append-only tables, the kill switch rules in the database, atomic reservations (requires Docker).</summary>
public class AiDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private string Connection => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance)
            .MigrateAsync(Connection, SqlMigrator.LoadEmbedded(typeof(InfrastructureAssembly).Assembly), CancellationToken.None);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Kill_switch_persists_is_append_only_and_is_cleared_only_by_a_user()
    {
        var killSwitch = new AiKillSwitch(new NpgsqlKillSwitchStore(Connection), new NpgsqlAiAudit(Connection), TimeProvider.System);

        await killSwitch.TripAsync(KillSwitchTrigger.SpendVelocity, AiKillSwitch.SystemActor, "velocity", default);
        Assert.True(await new AiKillSwitch(new NpgsqlKillSwitchStore(Connection), new NpgsqlAiAudit(Connection), TimeProvider.System).IsTrippedAsync(default));

        // The database refuses an automatic clear even if the application rule were bypassed.
        var bypass = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "INSERT INTO ai.kill_switch_events (at, tripped, actor, trigger_kind, reason) VALUES (now(), false, 'system', 'ManualClear', 'auto')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bypass.SqlState);
        var update = await Assert.ThrowsAsync<PostgresException>(() => Execute("UPDATE ai.kill_switch_events SET tripped = false"));
        Assert.Contains("append-only", update.MessageText);

        await killSwitch.ClearAsync("user:ilhan", "checked", default);
        Assert.False(await killSwitch.IsTrippedAsync(default));
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM audit.events WHERE event LIKE 'ai-kill-switch-%'"));
    }

    [Fact]
    public async Task Reservations_are_atomic_against_the_monthly_cap()
    {
        var ledger = new NpgsqlCostLedger(Connection);
        var now = DateTimeOffset.UtcNow;
        var cap = new UsdAmount(1m);

        // 20 concurrent reservations of 0.1 USD against a 1 USD cap: exactly 10 succeed.
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => ledger.TryReserveAsync(
            new ReservationRequest(Guid.NewGuid(), new JobContext($"job-{i}", JobKind.Qualification), TaskClass.Summary, "gemini", "m", new UsdAmount(0.1m), "p"),
            cap, cap, cap, now, default)));

        Assert.Equal(10, results.Count(r => r == ReservationStatus.Reserved));
        Assert.Equal(10, results.Count(r => r == ReservationStatus.MonthlyCapExceeded));
        Assert.Equal(new UsdAmount(1m), (await ledger.SummaryAsync(now, "job-0", TimeSpan.FromHours(1), default)).MonthToDate);
    }

    [Fact]
    public async Task Settlement_replaces_the_reservation_in_committed_spend_and_rows_are_append_only()
    {
        var ledger = new NpgsqlCostLedger(Connection);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var job = new JobContext("job-s", JobKind.ResearchCycle);
        var big = new UsdAmount(100m);
        await ledger.TryReserveAsync(new ReservationRequest(id, job, null, "gemini", "m", new UsdAmount(0.5m), "p"), big, big, big, now, default);

        await ledger.SettleAsync(id, new UsdAmount(0.2m), new AiUsage(100, 20), now, default);

        var summary = await ledger.SummaryAsync(now, job.JobId, TimeSpan.FromHours(1), default);
        Assert.Equal(new UsdAmount(0.2m), summary.Job);
        await Assert.ThrowsAsync<PostgresException>(() => ledger.SettleAsync(id, new UsdAmount(0.1m), new AiUsage(0, 0), now, default));
        await Assert.ThrowsAsync<PostgresException>(() => Execute("DELETE FROM ai.cost_ledger"));
        var entries = await ledger.EntriesAsync(now.AddMinutes(-1), now.AddMinutes(1), default);
        Assert.Equal([LedgerEntryKind.Reservation, LedgerEntryKind.Settlement], entries.Select(e => e.Kind));
        Assert.Equal(20, entries[1].OutputTokens);
    }

    [Fact]
    public async Task Router_writes_traces_and_ledger_rows_through_the_database_stores()
    {
        var configuration = TestConfig.Configuration();
        var provider = new FakeAiProvider(configuration.Prices).Returns(RouterRig.Answer(), model: TestConfig.OtherModel);
        var killSwitch = new AiKillSwitch(new NpgsqlKillSwitchStore(Connection), new NpgsqlAiAudit(Connection), TimeProvider.System);
        var guard = new AiCallGuard(provider, configuration, new NpgsqlCostLedger(Connection), killSwitch, TimeProvider.System);
        var router = new ModelRouter(configuration, guard, new NpgsqlDecisionTraceStore(Connection), new QualificationRegistry(new NpgsqlQualificationStore(Connection)), TimeProvider.System);

        var result = await router.ExecuteAsync(TaskClass.Summary, TestConfig.Input(), TestConfig.Research, default);

        Assert.True(result.Degraded);
        Assert.True(await Scalar<bool>($"SELECT degraded FROM ai.decision_traces WHERE task_id = '{result.TaskId}'"));
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM ai.cost_ledger"));
    }

    [Fact]
    public async Task Qualification_records_are_append_only_and_a_safety_violation_cannot_be_recorded_as_a_pass()
    {
        var store = new NpgsqlQualificationStore(Connection);
        var protocol = new QualificationProtocol(new string('a', 64), "abc1234", TaskClass.HypothesisGeneration, "gemini", "m", "v1",
            new string('d', 64), new string('b', 64), new string('c', 64), 0.05, 0.1, 50, 3m);
        await store.RegisterProtocolAsync(protocol, default);
        Assert.Equal(protocol, await store.FindProtocolAsync(protocol.ProtocolHash, default));

        var id = await store.AddResultAsync(new TaskQualification(0, protocol.ProtocolHash, DateTimeOffset.UtcNow, QualificationVerdict.Inconclusive, 3, 0, 3, 0, 1, 0, UsdAmount.Zero, "budget"), default);
        Assert.Equal(QualificationVerdict.Inconclusive, (await store.FindResultAsync(id, default))!.Verdict);

        await Assert.ThrowsAsync<PostgresException>(() => store.AddResultAsync(
            new TaskQualification(0, protocol.ProtocolHash, DateTimeOffset.UtcNow, QualificationVerdict.Passed, 50, 40, 50, 1, 0.0001, 1, UsdAmount.Zero, ""), default));
        await Assert.ThrowsAsync<PostgresException>(() => Execute("UPDATE ai.task_qualifications SET verdict = 'Passed'"));
        await Assert.ThrowsAsync<PostgresException>(() => store.RegisterProtocolAsync(protocol with { PilotSetHash = protocol.QualificationSetHash }, default));
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
