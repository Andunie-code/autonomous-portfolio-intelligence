using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.MarketData.Live;
using PortfolioIntelligence.Trading.Host;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// The allocator in the trading runtime (m8-protocol §4, ADR-0023) with a scripted advisor and synthetic candles: the
/// mode rules (application and database), roles, the Ops API, shadow mode leaving every real step unchanged and
/// rebuilding its shadow engine without a model call, and active mode applying overrides only to Validated sleeves
/// and replaying them. Requires Docker.
/// </summary>
public class AllocatorTradingTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private const string Token = "allocator-test-token-321";
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset Mid = Start.AddDays(15);
    private static readonly DateTimeOffset End = Mid.AddDays(1);

    /// <summary>Returns the overrides <paramref name="decide"/> gives and records every request.</summary>
    private sealed class ScriptedAdvisor(Func<AllocationRequest, AllocationDecision> decide) : IAllocationAdvisor
    {
        public List<AllocationRequest> Requests { get; } = [];

        public Task<AllocationDecision> AdviseAsync(AllocationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(decide(request));
        }
    }

    /// <summary>Vetoes every candidate and, as an attack, also "vetoes" every holding (which the engine must ignore).</summary>
    private static AllocationDecision VetoAll(AllocationRequest r) => r.Candidates.Count == 0
        ? AllocationDecision.Fallback(AllocationOutcome.NoCandidates)
        : new AllocationDecision(AllocationOutcome.Proposal,
            [.. r.Candidates.Select(c => new AllocationOverride(c.Sleeve, c.Symbol, 0m, true)), .. r.Holdings.Select(h => new AllocationOverride(h.Sleeve, h.Symbol, 0m, true))],
            r.Candidates.Select(c => new AllocationItem(c.Sleeve, c.Symbol, c.Weight, 0m, 0m, true, null)).ToList(), []);

    private static PaperTrader Trader(OpsDatabase.OpsDb db, IAllocationAdvisor? advisor) =>
        new(new PaperStore(db.As("trading_app")), new LiveCandleSource(new LiveCandleStore(db.As("trading_app"))), CostModel.V1,
            allocator: advisor, allocatorStore: advisor is null ? null : new AllocatorStore(db.As("trading_app")));

    private async Task<OpsDatabase.OpsDb> SeedAsync(bool twoCohorts)
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async();
        if (twoCohorts)
        {
            await db.SeedCohortAsync(TestCohorts.Kohort1Json(), cohortId: 2);
        }

        var from = PaperTrader.NextBoundary(Start) - TimeSpan.FromDays(60);
        await db.AddCandlesAsync(manifest.Pairs.SelectMany((p, i) => LiveSynthetic.Walk(p.Symbol, from, (int)((End - from).TotalHours / 4), 70 + i)));
        return db;
    }

    private static async Task<string> RegisterProtocolAsync(OpsDatabase.OpsDb db, char fill, int days)
    {
        var hash = new string(fill, 64);
        await db.ExecuteAsync("""
            INSERT INTO ai.qualification_protocols (protocol_hash, document_commit, task_class, provider, model, prompt_version, schema_hash,
                pilot_set_hash, qualification_set_hash, alpha, minimum_effect, sample_per_arm, budget_usd, design, p0)
            VALUES ($1, 'commit', 'AllocationProposal', 'gemini', 'm', 'allocation-prompt v1', repeat('1', 64), repeat('2', 64), repeat('3', 64),
                0.05, 0.03, $2, 1.00, 'NonInferiority', 0)
            """, "research_app", hash, days);
        return hash;
    }

    private static Task<long> PassedAsync(OpsDatabase.OpsDb db, string protocolHash) => db.ScalarAsync<long>($"""
        INSERT INTO ai.task_qualifications (at, protocol_hash, verdict, trials_llm, successes_llm, trials_baseline, successes_baseline, p_value, safety_violations, cost_usd, notes)
        VALUES (now(), '{protocolHash}', 'Passed', 30, 30, 0, 0, 'NaN', 0, 0.5, '') RETURNING id
        """, "research_app");

    private static Task<string> StepsAsync(OpsDatabase.OpsDb db, long? runId = null) => db.ScalarAsync<string>($"""
        SELECT coalesce(string_agg(run_id || '|' || bar_time::text || '|' || state_hash || '|' || l1_hash || '|' || l2_hash, ',' ORDER BY run_id, bar_time), '')
        FROM trading.steps {(runId is null ? "" : $"WHERE run_id = {runId}")}
        """);

    /// <summary>Hourly-like cycles every 4h from <paramref name="from"/> to <paramref name="to"/>: bars are processed live, not as stale catch-up.</summary>
    private static async Task CyclesAsync(PaperTrader trader, DateTimeOffset from, DateTimeOffset to)
    {
        for (var now = from; now <= to; now += TimeSpan.FromHours(4))
        {
            await trader.ProcessAsync(now, default);
        }
    }

    private static async Task Raises(OpsDatabase.OpsDb db, string role, string sql, string message)
    {
        var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql, role));
        Assert.Contains(message, e.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mode_rules_hold_in_the_application_and_the_database_and_research_sees_nothing()
    {
        var db = await fixture.NewAsync();
        var ops = new OpsService(db.As("ops_api"));
        var shadow = await RegisterProtocolAsync(db, 'a', 14);
        var window = await RegisterProtocolAsync(db, 'b', 28);

        // Application layer: 400 without confirm or with an unknown mode, 409 without a protocol or a Passed qualification.
        await Assert.ThrowsAsync<OpsRequestException>(() => ops.SetAllocatorModeAsync("Shadow", shadow, null, "pilot", false, default));
        await Assert.ThrowsAsync<OpsRequestException>(() => ops.SetAllocatorModeAsync("Bold", shadow, null, "pilot", true, default));
        await Assert.ThrowsAsync<OpsRequestException>(() => ops.SetAllocatorModeAsync("Shadow", shadow, null, " ", true, default));
        await Assert.ThrowsAsync<OpsConflictException>(() => ops.SetAllocatorModeAsync("Shadow", new string('f', 64), null, "pilot", true, default));
        await Assert.ThrowsAsync<OpsConflictException>(() => ops.SetAllocatorModeAsync("Active", shadow, null, "go", true, default)); // not from Disabled

        Assert.Equal(AllocatorMode.Shadow, (await ops.SetAllocatorModeAsync("Shadow", shadow, null, "pilot (C2)", true, default)).Mode);
        Assert.Equal(AllocatorMode.Shadow, (await new AllocatorStore(db.As("trading_app")).StateAsync(default)).Mode);
        await Assert.ThrowsAsync<OpsConflictException>(() => ops.SetAllocatorModeAsync("Active", null, null, "go", true, default));
        var failedPilot = await PassedAsync(db, shadow); // a Passed result of a 14-day protocol is not enough (window < 28 days)
        await Assert.ThrowsAsync<OpsConflictException>(() => ops.SetAllocatorModeAsync("Active", null, failedPilot, "go", true, default));
        Assert.Equal(AllocatorMode.Shadow, (await ops.SetAllocatorModeAsync("Shadow", window, null, "qualification window (C3)", true, default)).Mode);
        var passed = await PassedAsync(db, window);
        Assert.Equal(AllocatorMode.Active, (await ops.SetAllocatorModeAsync("Active", window, passed, "user decision", true, default)).Mode);
        Assert.Equal(AllocatorMode.Disabled, (await ops.SetAllocatorModeAsync("Disabled", null, null, "operational safety", false, default)).Mode); // always free

        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.allocator_mode_events"));
        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.ops_actions WHERE action = 'allocator-mode'"));
        Assert.Equal(4L, await db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'allocator_mode_changed'"));

        // Database layer: only the user's ops_api session, the current mode, confirm and the qualification records.
        const string Insert = "INSERT INTO trading.allocator_mode_events (from_mode, to_mode, protocol_hash, qualification_id, actor, reason, evidence) VALUES ";
        await Raises(db, "ops_api", Insert + "('Disabled', 'Shadow', repeat('a', 64), null, 'user', 'x', '{}')", "confirm");
        await Raises(db, "ops_api", Insert + "('Shadow', 'Disabled', null, null, 'user', 'x', '{}')", "not the current mode");
        await Raises(db, "ops_api", Insert + "('Disabled', 'Shadow', repeat('e', 64), null, 'user', 'x', '{\"confirm\": true}')", "registered AllocationProposal protocol");
        await Raises(db, "ops_api", Insert + "('Disabled', 'Active', null, null, 'user', 'x', '{\"confirm\": true}')", "only from Shadow");
        var denied = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Insert + "('Disabled', 'Disabled', null, null, 'x', 'x', '{}')", "trading_app"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await Raises(db, null!, "UPDATE trading.allocator_mode_events SET reason = 'x'", "append-only");

        foreach (var table in new[] { "trading.allocator_mode_events", "trading.allocator_proposals", "trading.allocator_shadow_steps" })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"SELECT * FROM {table}", "research_app"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState);
        }

        var proposals = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
            "INSERT INTO trading.allocator_proposals (run_id, bar_time, role, outcome, items, safety) VALUES (1, now(), 'shadow', 'x', '[]', '[]')", "ops_api"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, proposals.SqlState);
    }

    [Fact]
    public async Task The_ops_api_mode_route_needs_the_token_and_confirm_and_the_read_route_shows_the_mode()
    {
        var db = await fixture.NewAsync();
        var protocol = await RegisterProtocolAsync(db, 'c', 14);
        var app = TradingHost.Build([
            $"--ConnectionStrings:Trading={db.As("trading_app")}",
            $"--ConnectionStrings:OpsApi={db.As("ops_api")}",
            $"--OpsApi:Token={Token}",
            "--Trading:Worker=disabled",
            "--urls=http://127.0.0.1:0",
        ]);
        await app.StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        Task<HttpResponseMessage> Post(object body, string? token = Token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/allocator/mode") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return http.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(new { mode = "Shadow", protocolHash = protocol, reason = "x", confirm = true }, token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { mode = "Shadow", protocolHash = protocol, reason = "x", confirm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(new { mode = "Shadow", protocolHash = new string('9', 64), reason = "x", confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(new { mode = "Shadow", protocolHash = protocol, reason = "pilot", confirm = true })).StatusCode);

        var read = new HttpRequestMessage(HttpMethod.Get, "/allocator");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var json = JsonDocument.Parse(await (await http.SendAsync(read)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Shadow", json.GetProperty("mode").GetString());
        Assert.Equal(AllocatorBand.Version, json.GetProperty("band").GetProperty("version").GetString());
        Assert.Equal(1, json.GetProperty("modes").GetArrayLength());
    }

    [Fact]
    public async Task Shadow_mode_never_changes_a_real_step_and_its_engine_rebuilds_without_a_model_call()
    {
        var shadowDb = await SeedAsync(twoCohorts: false);
        var plainDb = await SeedAsync(twoCohorts: false);
        var protocol = await RegisterProtocolAsync(shadowDb, 'a', 14);
        await new OpsService(shadowDb.As("ops_api")).SetAllocatorModeAsync("Shadow", protocol, null, "pilot", true, default);
        var advisor = new ScriptedAdvisor(VetoAll);
        var shadowTrader = Trader(shadowDb, advisor);
        var plainTrader = Trader(plainDb, null);
        await CyclesAsync(shadowTrader, Start, Mid);
        await CyclesAsync(plainTrader, Start, Mid);

        // Real orders, ledger and both parity layers are those of T11, step by step.
        Assert.Equal(await StepsAsync(plainDb), await StepsAsync(shadowDb));
        Assert.NotEmpty(await StepsAsync(shadowDb));
        Assert.Equal(await plainDb.ScalarAsync<long>("SELECT count(*) FROM trading.fills"), await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.fills"));
        Assert.Equal(0L, await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.steps WHERE context ? 'allocation'"));

        // Every real step has its shadow step and decision; the vetoes made the shadow differ from T11.
        var steps = await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.steps");
        Assert.Equal(steps, await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.allocator_shadow_steps"));
        Assert.Equal(steps, await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.allocator_proposals WHERE role = 'shadow'"));
        Assert.Contains(advisor.Requests, r => r.Candidates.Count > 0);
        Assert.All(advisor.Requests, r => Assert.Equal(AllocatorMode.Shadow, r.Mode));
        Assert.All(advisor.Requests, r => Assert.Equal(protocol, r.ProtocolHash));
        Assert.True(await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.allocator_shadow_steps WHERE shadow_equity <> real_equity") > 0);
        Assert.Equal(0L, await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.reconciliation_runs WHERE NOT ok"));

        // Restart: a new process rebuilds the shadow engine from the stored overrides; the model sees only new bars.
        var restarted = new ScriptedAdvisor(VetoAll);
        await Trader(shadowDb, restarted).ProcessAsync(End, default);
        var newSteps = await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.steps") - steps;
        Assert.True(newSteps > 0);
        Assert.Equal(newSteps, restarted.Requests.Count);
        Assert.Equal(0L, await shadowDb.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'allocator_shadow_mismatch'"));
        Assert.Equal(steps + newSteps, await shadowDb.ScalarAsync<long>("SELECT count(*) FROM trading.allocator_shadow_steps"));
        Assert.Equal(1, restarted.Requests.Count(r => r.Live)); // only the latest closed bar is live; catch-up bars are not
    }

    [Fact]
    public async Task Active_mode_adjusts_only_validated_sleeves_and_the_overrides_replay()
    {
        var db = await SeedAsync(twoCohorts: true);
        var ops = new OpsService(db.As("ops_api"));
        var window = await RegisterProtocolAsync(db, 'b', 28);
        await ops.SetAllocatorModeAsync("Shadow", window, null, "qualification window", true, default);
        await ops.SetAllocatorModeAsync("Active", window, await PassedAsync(db, window), "user decision", true, default);
        await db.ExecuteAsync("INSERT INTO research.cohort_status (cohort_id, status, summary) VALUES (2, 'revealed', '{\"outcome\": \"Validated\"}')", "status_relay");
        await db.ExecuteAsync("INSERT INTO research.cohort_status (cohort_id, status, summary) VALUES (1, 'revealed', '{\"outcome\": \"Inconclusive\"}')", "status_relay");
        var advisor = new ScriptedAdvisor(VetoAll);
        var trader = Trader(db, advisor);
        await CyclesAsync(trader, Start, Mid);

        var validatedRun = await db.ScalarAsync<long>("SELECT id FROM trading.paper_runs WHERE config -> 'sleeves' -> 0 ->> 'cohortId' = '2'");
        var otherRun = await db.ScalarAsync<long>("SELECT id FROM trading.paper_runs WHERE config -> 'sleeves' -> 0 ->> 'cohortId' = '1'");

        // Only the Validated cohort's run is ever asked and adjusted; it opens nothing (every entry vetoed).
        Assert.NotEmpty(advisor.Requests);
        Assert.All(advisor.Requests, r => Assert.Equal($"run-{validatedRun}", r.RunKey));
        Assert.All(advisor.Requests, r => Assert.Equal(AllocatorMode.Active, r.Mode));
        Assert.True(await db.ScalarAsync<long>($"SELECT count(*) FROM trading.steps WHERE run_id = {validatedRun} AND context ? 'allocation'") > 0);
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM trading.steps WHERE run_id = {otherRun} AND context ? 'allocation'"));
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM trading.fills WHERE run_id = {validatedRun}"));
        Assert.True(await db.ScalarAsync<long>($"SELECT count(*) FROM trading.fills WHERE run_id = {otherRun}") > 0);
        Assert.True(await db.ScalarAsync<long>($"SELECT count(*) FROM trading.allocator_proposals WHERE run_id = {otherRun} AND role = 'active' AND outcome = 'not-validated'") > 0);
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM trading.allocator_proposals WHERE run_id = {otherRun} AND outcome = 'proposal'"));
        Assert.True(await db.ScalarAsync<long>($"SELECT count(*) FROM trading.engine_events WHERE run_id = {validatedRun} AND detail = 'allocator-veto'") > 0);

        // Restart without any allocator: the recorded overrides rebuild the same states (no reconciliation failure).
        var before = await StepsAsync(db);
        await Trader(db, null).ProcessAsync(Mid, default);
        Assert.Equal(before, await StepsAsync(db));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.reconciliation_runs WHERE NOT ok"));
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM trading.reconciliation_runs WHERE ok") > 0);
    }
}
