using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Trading.Host;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// D30 and V10 (m6-protocol §6; D3, D30, U-M6-3): the calendar-day and processed-bar counters with their bounds, the
/// 7-day critical error window, and the user's PaperTrading → TestnetActive transition: 401 without the token, 400
/// without confirm, 409 when the state, Testnet or D30 does not allow it, success with the D30 evidence and audit;
/// the database trigger refuses every other writer and evidence. Requires Docker.
/// </summary>
public class PromotionTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private const string Token = "promotion-test-token-789";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 8, 0, 15, TimeSpan.Zero);

    [Theory]
    [InlineData(9.99, 60, 0, "days")]
    [InlineData(10.0, 59, 0, "bars")]
    [InlineData(10.0, 60, 1, "critical-errors")]
    [InlineData(10.0, 60, 0, null)]
    [InlineData(30, 400, 0, null)]
    public void D30_needs_10_days_60_bars_and_no_critical_error(double days, int bars, int errors, string? missing)
    {
        // D30 rev. (U, 2026-10-30; m6-protocol v1.2 §14): 10 calendar days, the bar and error conditions unchanged.
        Assert.Equal(10, Promotion.MinimumDays);
        Assert.Equal(60, Promotion.MinimumBars);
        Assert.Equal(TimeSpan.FromDays(7), Promotion.ErrorWindow);

        var result = Promotion.Missing(Now, Now.AddDays(-days), bars, errors);

        Assert.Equal(missing is null ? [] : new[] { missing }, result);
        Assert.Equal(["days", "bars"], Promotion.Missing(Now, null, 0, 0));
    }

    /// <summary>Kohort 0 in paper since <paramref name="paperDays"/> days, with a paper run of the given age and processed bars.</summary>
    private async Task<OpsDatabase.OpsDb> PaperAsync(int paperDays, int runDays)
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async();
        var start = Now.AddDays(-runDays);
        var tradingStart = PaperTrader.NextBoundary(start);
        await db.AddCandlesAsync(manifest.Pairs.SelectMany((p, i) => LiveSynthetic.Walk(p.Symbol, tradingStart - PaperTrader.Warmup, (int)((Now - tradingStart + PaperTrader.Warmup).TotalHours / 4), 40 + i)));
        var trader = new PaperTrader(new PaperStore(db.As("trading_app")), new LiveCandleSource(new MarketData.Live.LiveCandleStore(db.As("trading_app"))), Kernel.Backtest.CostModel.V1);
        await trader.ProcessAsync(start, default);
        await trader.ProcessAsync(Now, default);
        // Fixture only (owner): the PaperTrading event is moved <paramref name="paperDays"/> days into the past; D30 counts from it.
        await db.ExecuteAsync($"ALTER TABLE research.lifecycle_events DISABLE TRIGGER append_only; UPDATE research.lifecycle_events SET recorded_at = now() - interval '{paperDays} days' WHERE to_state = 'PaperTrading'; ALTER TABLE research.lifecycle_events ENABLE TRIGGER append_only");
        return db;
    }

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(OpsDatabase.OpsDb db, bool testnet)
    {
        var secrets = Directory.CreateTempSubdirectory("pi-m6-");
        if (testnet)
        {
            await File.WriteAllTextAsync(Path.Combine(secrets.FullName, TradingHost.TestnetKeyFile), "fake-key-for-tests-only-0123456789");
            await File.WriteAllTextAsync(Path.Combine(secrets.FullName, TradingHost.TestnetSecretFile), "fake-secret-for-tests-only-0123456789");
        }

        var app = TradingHost.Build([
            $"--ConnectionStrings:Trading={db.As("trading_app")}",
            $"--ConnectionStrings:OpsApi={db.As("ops_api")}",
            $"--OpsApi:Token={Token}",
            "--Trading:Worker=disabled",
            $"--Trading:Testnet={(testnet ? "enabled" : "disabled")}",
            $"--Trading:SecretsDirectory={secrets.FullName}",
            "--urls=http://127.0.0.1:0",
        ]);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string path, object body, string? token = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http.SendAsync(request);
    }

    [Fact]
    public async Task The_transition_needs_the_token_confirm_paper_state_testnet_and_d30()
    {
        var db = await PaperAsync(paperDays: 20, runDays: 20);
        var (app, http) = await StartAsync(db, testnet: true);
        await using var _ = app;
        const string Path = "/strategies/ema-trend/testnet-activate";

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, Path, new { reason = "x", confirm = true }, token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, Path, new { reason = "x", confirm = true }, token: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(http, Path, new { reason = "x", confirm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(http, "/strategies/unknown/testnet-activate", new { reason = "x", confirm = true })).StatusCode);

        var promotion = JsonDocument.Parse(await (await Get(http, "/promotion")).Content.ReadAsStringAsync()).RootElement;
        var ema = promotion.GetProperty("strategies").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "ema-trend");
        Assert.True(ema.GetProperty("eligible").GetBoolean());
        Assert.True(ema.GetProperty("experimental").GetBoolean());
        Assert.True(ema.GetProperty("bars").GetInt32() >= 60);

        var ok = await Post(http, Path, new { reason = "user decision, Gate C", confirm = true });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("TestnetActive", await db.ScalarAsync<string>("SELECT to_state FROM research.lifecycle_events ORDER BY id DESC LIMIT 1"));
        var evidence = JsonDocument.Parse(await db.ScalarAsync<string>("SELECT evidence::text FROM research.lifecycle_events ORDER BY id DESC LIMIT 1")).RootElement;
        Assert.True(evidence.GetProperty("confirm").GetBoolean());
        Assert.True(evidence.GetProperty("d30").GetProperty("bars").GetInt32() >= 60);
        Assert.True(evidence.GetProperty("experimental").GetBoolean());
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'strategy_testnet_activated'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.ops_actions WHERE action = 'testnet-activate'"));

        // Already TestnetActive (not PaperTrading): 409.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(http, Path, new { reason = "again", confirm = true })).StatusCode);
    }

    [Fact]
    public async Task D30_not_met_testnet_off_or_a_non_paper_state_is_a_conflict()
    {
        var young = await PaperAsync(paperDays: 5, runDays: 20);
        var (app, http) = await StartAsync(young, testnet: true);
        await using (app)
        {
            var refused = await Post(http, "/strategies/ema-trend/testnet-activate", new { reason = "x", confirm = true });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("D30", await refused.Content.ReadAsStringAsync());
        }

        var old = await PaperAsync(paperDays: 20, runDays: 20);
        var (offApp, offHttp) = await StartAsync(old, testnet: false);
        await using (offApp)
        {
            var refused = await Post(offHttp, "/strategies/ema-trend/testnet-activate", new { reason = "x", confirm = true });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("Testnet is not enabled", await refused.Content.ReadAsStringAsync());
            Assert.False(JsonDocument.Parse(await (await Get(offHttp, "/testnet")).Content.ReadAsStringAsync()).RootElement.GetProperty("enabled").GetBoolean());
        }

        var frozen = await fixture.NewAsync();
        await frozen.SeedKohort0Async(paper: false);
        var (frozenApp, frozenHttp) = await StartAsync(frozen, testnet: true);
        await using (frozenApp)
        {
            var refused = await Post(frozenHttp, "/strategies/ema-trend/testnet-activate", new { reason = "x", confirm = true });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("Frozen", await refused.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Bars_processed_in_one_catch_up_count_toward_d30()
    {
        // The whole 11-day run is processed in a single call after start (one catch-up after an "outage"): every bar counts.
        var db = await PaperAsync(paperDays: 11, runDays: 11);
        await using var connection = new NpgsqlConnection(db.As("ops_api"));
        await connection.OpenAsync();
        var ema = (await Promotion.ReadAsync(connection, Now, "ema-trend", default)).Single();
        var expected = await db.ScalarAsync<long>("SELECT count(*) FROM trading.steps s JOIN trading.paper_runs p ON p.id = s.run_id WHERE p.venue = 'paper' AND s.bar_time >= p.trading_start");

        Assert.Equal(expected, ema.Bars);
        Assert.True(ema.Bars >= Promotion.MinimumBars);
        Assert.DoesNotContain("bars", ema.Missing);
    }

    [Fact]
    public async Task The_critical_error_window_is_seven_days_and_its_bound_is_exclusive()
    {
        var db = await PaperAsync(paperDays: 11, runDays: 11);
        await using var connection = new NpgsqlConnection(db.As("ops_api"));
        await connection.OpenAsync();
        Task<PromotionStatus> Read() => Promotion.ReadAsync(connection, Now, "ema-trend", default).ContinueWith(t => t.Result.Single());
        Assert.Equal(0, (await Read()).CriticalErrors7d);

        // A failed reconciliation exactly 7 days (and 1 s more) before now is outside the window; 1 s inside counts.
        var run = await db.ScalarAsync<long>("SELECT max(id) FROM trading.paper_runs WHERE venue = 'paper'");
        const string Failed = "INSERT INTO trading.reconciliation_runs (checked_at, run_id, ok, detail) VALUES ($1, $2, false, '{}')";
        await db.ExecuteAsync(Failed, null, Now - Promotion.ErrorWindow - TimeSpan.FromSeconds(1), run);
        await db.ExecuteAsync(Failed, null, Now - Promotion.ErrorWindow, run);
        Assert.Equal(0, (await Read()).CriticalErrors7d);

        await db.ExecuteAsync(Failed, null, Now - Promotion.ErrorWindow + TimeSpan.FromSeconds(1), run);
        var status = await Read();
        Assert.Equal(1, status.CriticalErrors7d);
        Assert.Contains("critical-errors", status.Missing);
    }

    [Fact]
    public async Task Only_the_ops_api_role_with_d30_evidence_and_confirm_writes_testnet_active()
    {
        var db = await fixture.NewAsync();
        await db.SeedKohort0Async();
        const string Insert = "INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES (1, 'PaperTrading', 'TestnetActive', 'x', $1::jsonb)";

        var research = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Insert, "research_app", "{\"d30\": {}, \"confirm\": true}"));
        Assert.Contains("only by the user", research.MessageText);
        var trading = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Insert, "trading_app", "{\"d30\": {}, \"confirm\": true}"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, trading.SqlState);
        foreach (var evidence in new[] { "{}", "{\"d30\": {}}", "{\"d30\": {}, \"confirm\": false}", "{\"confirm\": true}", "{\"d30\": {}, \"confirm\": \"true\"}" })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(Insert, "ops_api", evidence));
            Assert.Contains("D30 evidence and confirm", e.MessageText);
        }

        await db.ExecuteAsync(Insert, "ops_api", "{\"d30\": {}, \"confirm\": true}");
        // TestnetActive → Disabled stays free (operational safety, D23); Frozen → TestnetActive never exists.
        await db.ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES (1, 'TestnetActive', 'Disabled', 'user', '{}')", "ops_api");
        var frozen = await Assert.ThrowsAsync<PostgresException>(() =>
            db.ExecuteAsync("INSERT INTO research.lifecycle_events (frozen_id, from_state, to_state, actor, evidence) VALUES (2, 'Frozen', 'TestnetActive', 'x', '{\"d30\": {}, \"confirm\": true}')", "ops_api"));
        Assert.Contains("not the current state", frozen.MessageText); // member 2 is PaperTrading, and the pair is not a transition
    }

    [Fact]
    public async Task Rebaseline_needs_confirm_and_the_kill_switch_wakes_the_exchange_maintenance()
    {
        var db = await fixture.NewAsync();
        await db.SeedKohort0Async();
        var (app, http) = await StartAsync(db, testnet: false);
        await using var _ = app;

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(http, "/testnet/rebaseline", new { reason = "reset", confirm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(http, "/testnet/rebaseline", new { reason = "Testnet reset", confirm = true })).StatusCode);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.ops_actions WHERE action = 'testnet-rebaseline'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'testnet_rebaseline_requested'"));

        var signal = app.Services.GetRequiredService<MaintenanceSignal>();
        Assert.Equal(0, signal.Requests);
        Assert.Equal(HttpStatusCode.OK, (await Post(http, "/kill-switch", new { engaged = true, reason = "drill" })).StatusCode);
        Assert.Equal(1, signal.Requests);
        Assert.Equal(HttpStatusCode.OK, (await Post(http, "/kill-switch", new { engaged = false, reason = "drill over" })).StatusCode);
        Assert.Equal(1, signal.Requests); // releasing needs no maintenance
    }

    private static Task<HttpResponseMessage> Get(HttpClient http, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http.SendAsync(request);
    }
}
