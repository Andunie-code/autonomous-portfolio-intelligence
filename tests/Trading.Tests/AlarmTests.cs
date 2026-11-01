using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Trading.Host;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// M10 alarms (ADR-0024; catalogue and thresholds m9-m10-protocol §3.2, B-1…B-5): every alarm is raised and cleared
/// at its bound by the pure evaluator, and the inputs are read as ops_api (SELECT only) from synthetic rows. Nothing is
/// stored or sent. The database tests require Docker.
/// </summary>
public class AlarmTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 15, TimeSpan.Zero);
    private static readonly AiBudget Budget = new(10m, new Dictionary<string, decimal> { ["Allocation"] = 0.10m });

    /// <summary>A healthy state: no alarm.</summary>
    private static readonly AlarmInputs Clear = new(
        TradingKillSwitch: false, TradingKillSwitchActor: null, TradingKillSwitchAt: null,
        AiKillSwitch: false, AiKillSwitchTrigger: null, AiKillSwitchAt: null,
        AiMonthUsedUsd: 0.5m,
        AiAutoTrips: EventCount.None, AiAutoTripLastKind: null,
        HenvVerdict: "PASS", HenvAt: Now.AddDays(-3),
        LastCycleAt: Now.AddMinutes(-10),
        UnprotectedSince: null, OldestUnknownSince: null, UnknownOrders: 0,
        ResetSuspectedSince: null,
        ReconciliationFailures: EventCount.None, HashMismatches: EventCount.None, CycleErrors: EventCount.None);

    private static IReadOnlyList<Alarm> Evaluate(AlarmInputs inputs) => AlarmEvaluator.Evaluate(inputs, Budget, Now);

    private static Alarm? Find(AlarmInputs inputs, string code) => Evaluate(inputs).SingleOrDefault(a => a.Code == code);

    [Fact]
    public void A_healthy_state_raises_no_alarm_and_the_thresholds_are_the_approved_ones()
    {
        Assert.Empty(Evaluate(Clear));
        Assert.Equal(0.80m, AlarmThresholds.BudgetWarningShare);
        Assert.Equal(1.00m, AlarmThresholds.BudgetCriticalShare);
        Assert.Equal(TimeSpan.FromDays(31), AlarmThresholds.HenvMaxAge);
        Assert.Equal(TimeSpan.FromMinutes(75), AlarmThresholds.HeartbeatMaxAge);
        Assert.Equal(TimeSpan.FromMinutes(60), AlarmThresholds.UnknownMaxAge);
        Assert.Equal(Promotion.ErrorWindow, AlarmThresholds.Window);
    }

    [Fact]
    public void A1_and_A2_follow_the_kill_switches()
    {
        var trading = Find(Clear with { TradingKillSwitch = true, TradingKillSwitchActor = "system", TradingKillSwitchAt = Now.AddHours(-1) }, "trading-kill-switch");
        Assert.Equal(AlarmSeverity.Critical, trading!.Severity);
        Assert.Equal(Now.AddHours(-1), trading.Since);
        Assert.Contains("system", trading.Detail);

        var ai = Find(Clear with { AiKillSwitch = true, AiKillSwitchTrigger = "Manual", AiKillSwitchAt = Now }, "ai-kill-switch");
        Assert.Equal(AlarmSeverity.Critical, ai!.Severity);
        Assert.Contains("Manual", ai.Detail);
        Assert.Null(Find(Clear with { AiKillSwitch = false, AiKillSwitchTrigger = "ManualClear" }, "ai-kill-switch"));
    }

    [Theory]
    [InlineData("7.99", null)]
    [InlineData("8.00", AlarmSeverity.Warning)]
    [InlineData("9.99", AlarmSeverity.Warning)]
    [InlineData("10.00", AlarmSeverity.Critical)]
    [InlineData("12.50", AlarmSeverity.Critical)]
    public void A3_budget_warns_at_80_percent_and_is_critical_at_the_cap(string used, string? severity)
    {
        var alarm = Find(Clear with { AiMonthUsedUsd = decimal.Parse(used, System.Globalization.CultureInfo.InvariantCulture) }, "ai-budget");
        Assert.Equal(severity, alarm?.Severity);
        Assert.Empty(AlarmEvaluator.Evaluate(Clear with { AiMonthUsedUsd = 50m }, AiBudget.None, Now)); // no cap configured: no budget alarm
    }

    [Fact]
    public void A4_counts_automatic_trips_only()
    {
        var alarm = Find(Clear with { AiAutoTrips = new EventCount(2, Now.AddDays(-2)), AiAutoTripLastKind = "SpendVelocity" }, "ai-auto-trip");
        Assert.Equal(AlarmSeverity.Warning, alarm!.Severity);
        Assert.Contains("SpendVelocity", alarm.Detail);
        Assert.Null(Find(Clear, "ai-auto-trip"));
    }

    [Fact]
    public void A5_the_monthly_henv_run_is_due_after_31_days_and_a_failed_run_is_critical()
    {
        var day31 = AlarmThresholds.HenvMaxAge;
        Assert.Null(Find(Clear with { HenvAt = Now - day31 + TimeSpan.FromSeconds(1) }, "henv-stale"));
        Assert.Null(Find(Clear with { HenvAt = Now - day31 }, "henv-stale"));
        var due = Find(Clear with { HenvAt = Now - day31 - TimeSpan.FromSeconds(1) }, "henv-stale");
        Assert.Equal(AlarmSeverity.Warning, due!.Severity);
        Assert.Equal(Now - TimeSpan.FromSeconds(1), due.Since);
        Assert.Equal(AlarmSeverity.Warning, Find(Clear with { HenvVerdict = null, HenvAt = null }, "henv-stale")!.Severity);
        Assert.Equal(AlarmSeverity.Critical, Find(Clear with { HenvVerdict = "FAIL", HenvAt = Now.AddDays(-1) }, "henv-stale")!.Severity);
    }

    [Fact]
    public void A6_heartbeat_is_critical_after_75_minutes_without_a_cycle()
    {
        Assert.Null(Find(Clear with { LastCycleAt = Now - AlarmThresholds.HeartbeatMaxAge }, "heartbeat"));
        Assert.Equal(AlarmSeverity.Critical, Find(Clear with { LastCycleAt = Now - AlarmThresholds.HeartbeatMaxAge - TimeSpan.FromSeconds(1) }, "heartbeat")!.Severity);
        Assert.Equal(AlarmSeverity.Critical, Find(Clear with { LastCycleAt = null }, "heartbeat")!.Severity);
    }

    [Fact]
    public void A7_testnet_and_integrity_alarms()
    {
        Assert.Equal(AlarmSeverity.Critical, Find(Clear with { UnprotectedSince = Now.AddHours(-2) }, "unprotected-position")!.Severity);
        Assert.Null(Find(Clear with { OldestUnknownSince = Now - AlarmThresholds.UnknownMaxAge, UnknownOrders = 1 }, "order-unknown"));
        var unknown = Find(Clear with { OldestUnknownSince = Now - AlarmThresholds.UnknownMaxAge - TimeSpan.FromSeconds(1), UnknownOrders = 2 }, "order-unknown");
        Assert.Equal(AlarmSeverity.Critical, unknown!.Severity);
        Assert.StartsWith("2 ", unknown.Detail);
        Assert.Equal(AlarmSeverity.Critical, Find(Clear with { ResetSuspectedSince = Now.AddHours(-3) }, "testnet-reset-suspected")!.Severity);
        Assert.Equal(AlarmSeverity.Critical, Find(Clear with { ReconciliationFailures = new EventCount(1, Now.AddDays(-1)) }, "reconciliation")!.Severity);
        Assert.Equal(AlarmSeverity.Critical, Find(Clear with { HashMismatches = new EventCount(1, Now.AddDays(-1)) }, "strategy-hash-mismatch")!.Severity);
        Assert.Equal(AlarmSeverity.Warning, Find(Clear with { CycleErrors = new EventCount(3, Now.AddHours(-5)) }, "cycle-error")!.Severity);
    }

    [Fact]
    public void The_document_lists_critical_alarms_first_and_names_the_thresholds()
    {
        var alarms = Evaluate(Clear with { CycleErrors = new EventCount(1, Now), TradingKillSwitch = true, AiMonthUsedUsd = 8.5m });
        var document = JsonDocument.Parse(AlarmEvaluator.Document(alarms, Now)).RootElement;
        Assert.Equal(1, document.GetProperty("critical").GetInt32());
        Assert.Equal(2, document.GetProperty("warning").GetInt32());
        Assert.Equal("critical", document.GetProperty("alarms")[0].GetProperty("severity").GetString());
        Assert.Equal(31, document.GetProperty("thresholds").GetProperty("henvMaxAgeDays").GetDouble());
    }

    private static async Task<AlarmInputs> ReadAsync(OpsDatabase.OpsDb db, DateTimeOffset now)
    {
        await using var connection = new NpgsqlConnection(db.As("ops_api"));
        await connection.OpenAsync();
        return await AlarmEvaluator.ReadAsync(connection, now, default);
    }

    private static void Close(DateTimeOffset expected, DateTimeOffset? actual) =>
        Assert.True(actual is { } a && Math.Abs((a - expected).TotalMilliseconds) < 1, $"{actual:O} is not {expected:O}");

    private static Task Cycle(OpsDatabase.OpsDb db, DateTimeOffset at, string outcome, params string[] degraded) =>
        db.ExecuteAsync("""
            INSERT INTO trading.cycle_metrics (scheduled_for, started_at, finished_at, bars_processed, catch_up, stale, outcome, detail)
            VALUES ($1, $1, $1, 0, false, false, $2, jsonb_build_object('degraded', to_jsonb($3::text[])))
            """, null, at, outcome, degraded);

    [Fact]
    public async Task Inputs_are_read_as_ops_api_from_the_ops_records()
    {
        var db = await fixture.NewAsync();
        var now = DateTimeOffset.UtcNow;
        var empty = await ReadAsync(db, now);
        Assert.False(empty.TradingKillSwitch);
        Assert.Null(empty.LastCycleAt);
        Assert.Null(empty.HenvAt);
        Assert.Equal(0m, empty.AiMonthUsedUsd);
        Assert.Contains(AlarmEvaluator.Evaluate(empty, Budget, now), a => a.Code == "heartbeat");

        // Kill switches: the latest row decides; a manual AI trip is not an automatic one, a trip older than 7 days does not count.
        await db.ExecuteAsync("INSERT INTO trading.kill_switch_events (engaged, actor, reason) VALUES (true, 'user', 'drill')", "ops_api");
        await db.ExecuteAsync("""
            INSERT INTO ai.kill_switch_events (at, tripped, actor, trigger_kind, reason) VALUES
              ($1, true, 'system', 'SpendVelocity', 'velocity'), ($2, true, 'system', 'CircuitBreaker', 'old'), ($3, true, 'user:x', 'Manual', 'drill')
            """, null, now.AddDays(-2), now.AddDays(-8), now.AddMinutes(-1));
        // H-ENV run record (written by m5-henv.ps1), cycles with a degraded streak, an error and a hash mismatch.
        await db.ExecuteAsync("INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ('user', 'henv_run_recorded', '{\"verdict\": \"PASS\", \"failures\": 0}', $1)", null, new string('a', 64));
        await Cycle(db, now.AddHours(-3), "degraded", "unprotected-stop");
        await Cycle(db, now.AddHours(-2), "degraded", "unprotected-stop", "testnet-reset-suspected");
        await Cycle(db, now.AddHours(-1), "error", "cycle-error", "unprotected-stop", "testnet-reset-suspected");
        await db.ExecuteAsync("INSERT INTO audit.events (actor, event, payload, payload_hash) VALUES ('trading', 'strategy_hash_mismatch', '{\"name\": \"x\"}', $1)", null, new string('b', 64));
        // AI spend this month: a settled call counts its settlement, an open reservation its reservation; last month's spend does not count.
        await db.ExecuteAsync("""
            INSERT INTO ai.cost_ledger (at, call_id, job_id, job_kind, task_class, entry_kind, amount_usd, provider, model, price_version) VALUES
              ($1, 'aaaaaaaa-0000-0000-0000-000000000001', 'j1', 'Allocation', 'AllocationProposal', 'Reservation', 0.5, 'gemini', 'm', 'p'),
              ($1, 'aaaaaaaa-0000-0000-0000-000000000001', 'j1', 'Allocation', 'AllocationProposal', 'Settlement', 0.25, 'gemini', 'm', 'p'),
              ($1, 'aaaaaaaa-0000-0000-0000-000000000002', 'j2', 'Qualification', 'HypothesisGeneration', 'Reservation', 0.125, 'gemini', 'm', 'p'),
              ($2, 'aaaaaaaa-0000-0000-0000-000000000003', 'j3', 'Qualification', 'FindingExtraction', 'Reservation', 5, 'gemini', 'm', 'p')
            """, null, now, AiSpend.MonthStart(now).AddDays(-3));

        var inputs = await ReadAsync(db, now);
        Assert.True(inputs.TradingKillSwitch);
        Assert.Equal("user", inputs.TradingKillSwitchActor);
        Assert.True(inputs.AiKillSwitch);
        Assert.Equal("Manual", inputs.AiKillSwitchTrigger);
        Assert.Equal(1, inputs.AiAutoTrips.Count);
        Assert.Equal("SpendVelocity", inputs.AiAutoTripLastKind);
        Assert.Equal("PASS", inputs.HenvVerdict);
        Assert.NotNull(inputs.HenvAt);
        Close(now.AddHours(-1), inputs.LastCycleAt);
        Close(now.AddHours(-3), inputs.UnprotectedSince);
        Close(now.AddHours(-2), inputs.ResetSuspectedSince);
        Assert.Equal(1, inputs.CycleErrors.Count);
        Assert.Equal(1, inputs.HashMismatches.Count);
        Assert.Equal(0.375m, inputs.AiMonthUsedUsd);

        // A later cycle without the flags ends both streaks; releasing the kill switch clears A1.
        await Cycle(db, now.AddMinutes(-5), "ok");
        await db.ExecuteAsync("INSERT INTO trading.kill_switch_events (engaged, actor, reason) VALUES (false, 'user', 'drill over')", "ops_api");
        var after = await ReadAsync(db, now);
        Assert.Null(after.UnprotectedSince);
        Assert.Null(after.ResetSuspectedSince);
        Assert.False(after.TradingKillSwitch);
        var codes = AlarmEvaluator.Evaluate(after, Budget, now).Select(a => a.Code).ToList();
        Assert.Equal(["ai-kill-switch", "ai-auto-trip", "strategy-hash-mismatch", "cycle-error"], codes);
    }

    [Fact]
    public async Task The_alarms_route_needs_the_token_and_shows_no_secret()
    {
        const string Token = "alarm-test-token-321";
        var db = await fixture.NewAsync();
        var app = TradingHost.Build([
            $"--ConnectionStrings:Trading={db.As("trading_app")}",
            $"--ConnectionStrings:OpsApi={db.As("ops_api")}",
            $"--OpsApi:Token={Token}",
            "--Trading:Worker=disabled",
            "--urls=http://127.0.0.1:0",
        ]);
        await app.StartAsync();
        await using var _ = app;
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/alarms")).StatusCode);
        var request = new HttpRequestMessage(HttpMethod.Get, "/alarms");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var document = JsonDocument.Parse(body).RootElement;
        // A fresh environment: no cycle and no H-ENV run yet.
        Assert.Contains(document.GetProperty("alarms").EnumerateArray(), a => a.GetProperty("code").GetString() == "heartbeat");
        Assert.Contains(document.GetProperty("alarms").EnumerateArray(), a => a.GetProperty("code").GetString() == "henv-stale");
        Assert.DoesNotContain(Token, body);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10m, TradingHost.Budget(Ai.AiConfiguration.LoadDefault()).MonthlyCapUsd);
    }
}
