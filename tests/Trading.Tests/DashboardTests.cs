using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.MarketData.Live;
using PortfolioIntelligence.Trading.Host;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>
/// M9a dashboard (ADR-0020) end to end on synthetic data: the static page needs no token and carries no data,
/// every data route needs the token, security headers and the loopback Host rule apply, and the provenance chain
/// leads from a fill to the frozen strategy, its manifest, the research experiments and the dataset snapshot.
/// Requires Docker.
/// </summary>
public class DashboardTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private const string Token = "dashboard-test-token-456";
    private static readonly string[] DataRoutes = ["/overview", "/sleeves", "/fills", "/decisions", "/provenance/1", "/radar", "/system", "/ai/costs", "/ai/traces"];

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(OpsDatabase.OpsDb db)
    {
        var app = TradingHost.Build([
            $"--ConnectionStrings:Trading={db.As("trading_app")}",
            $"--ConnectionStrings:OpsApi={db.As("ops_api")}",
            $"--OpsApi:Token={Token}",
            "--Trading:Worker=disabled",
            "--Radar:WatchSymbols=WIFUSDT, bad symbol,ORDIUSDT",
            "--urls=http://127.0.0.1:0",
        ]);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static Task<HttpResponseMessage> Get(HttpClient http, string path, string? token = Token, string? host = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return http.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpClient http, string path)
    {
        var response = await Get(http, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<OpsDatabase.OpsDb> PaperAsync()
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async();
        var now = new DateTimeOffset(2026, 4, 1, 1, 0, 0, TimeSpan.Zero);
        var start = PaperTrader.NextBoundary(now) - PaperTrader.Warmup;
        await db.AddCandlesAsync(manifest.Pairs.SelectMany((p, i) => LiveSynthetic.Walk(p.Symbol, start, 45 * 6 + 30 * 6, 300 + i)));
        // A research-window experiment of the first member, as M2/M4 recorded them (provenance view).
        await db.ExecuteAsync("""
            INSERT INTO research.experiments (strategy_name, strategy_version, strategy_hash, variant, protocol_version, protocol_commit,
                dataset_snapshot_hash, engine_git_sha, result_hash, metrics)
            VALUES ('ema-trend', 'dsl-1', $1, 'policy', 'm2-protocol v1', '296757f', 'a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82', 'x', $2, '{}')
            """, null, manifest.Members[0].DefinitionHash, new string('c', 64));
        var trader = new PaperTrader(new PaperStore(db.As("trading_app")), new LiveCandleSource(new LiveCandleStore(db.As("trading_app"))), CostModel.V1);
        for (var t = now; t <= now.AddDays(28); t = t.AddHours(8))
        {
            await trader.ProcessAsync(t, default);
        }

        return db;
    }

    [Fact]
    public async Task The_page_is_public_and_empty_and_every_data_route_needs_the_token()
    {
        var db = await fixture.NewAsync();
        var (app, http) = await StartAsync(db);
        await using var _ = app;

        foreach (var (path, type) in new[] { ("/dashboard", "text/html"), ("/dashboard/app.js", "text/javascript"), ("/dashboard/app.css", "text/css") })
        {
            var response = await Get(http, path, token: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(OpsApi.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Token, body);
            Assert.DoesNotContain(".innerHTML", body); // every value is rendered with textContent
            Assert.DoesNotContain("<script>", body); // no inline script (CSP script-src 'self')
            Assert.DoesNotContain("http://", body.Replace("http://www.w3.org", "", StringComparison.Ordinal)); // no external resource
        }

        foreach (var path in DataRoutes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Get(http, path, token: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Get(http, path, token: "wrong")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Get(http, path, token: Token + "x")).StatusCode);
        }

        // DNS rebinding: a non-loopback Host is refused before routing, even with the token; health stays reachable.
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await Get(http, "/overview", host: "evil.example:55490")).StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await Get(http, "/dashboard", token: null, host: "evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(http, "/health/live", token: null, host: "evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(http, "/overview", host: "localhost:55490")).StatusCode);
    }

    [Fact]
    public async Task A_run_before_its_first_candle_holds_its_capital_in_cash()
    {
        var db = await fixture.NewAsync();
        await db.SeedKohort0Async();
        var config = new PaperRunConfig("m", [new SleeveConfig(1, 0, "ema-trend", "x", 5_000m), new SleeveConfig(1, 1, "supertrend", "y", 5_000m)],
            ["BTCUSDT"], [], "4h", null, "v1", RiskConfigDocument.From(RiskConfig.Policy));
        // A run that was just opened: no step, so no balance snapshot yet.
        await db.ExecuteAsync("INSERT INTO trading.paper_runs (trading_start, warmup_start, config, config_hash) VALUES ('2026-10-23', '2026-09-01', $1::jsonb, $2)",
            null, config.Json(), config.Hash());
        var (app, http) = await StartAsync(db);
        await using var _ = app;

        var overview = await Json(http, "/overview");
        var run = Assert.Single(overview.GetProperty("runs").EnumerateArray());
        Assert.True(run.GetProperty("awaitingFirstBar").GetBoolean());
        Assert.Equal(10_000m, run.GetProperty("cash").GetDecimal());
        Assert.Equal(10_000m, overview.GetProperty("total").GetProperty("cash").GetDecimal());
        Assert.Equal(overview.GetProperty("total").GetProperty("equity").GetDecimal(), overview.GetProperty("total").GetProperty("cash").GetDecimal());
        Assert.Equal(0, (await Json(http, "/system")).GetProperty("unavailable").GetArrayLength());
    }

    [Fact]
    public async Task Without_the_dashboard_grants_the_system_view_degrades_and_the_ai_routes_say_why()
    {
        var db = await fixture.NewAsync();
        await db.ExecuteAsync("REVOKE SELECT ON ai.kill_switch_events, ai.cost_ledger, audit.events FROM ops_api", null);
        var (app, http) = await StartAsync(db);
        await using var _ = app;

        var system = await Json(http, "/system");
        Assert.Equal(["aiKillSwitch", "audit"], system.GetProperty("unavailable").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("m5-migrate", system.GetProperty("unavailableReason").GetString());
        var costs = await Get(http, "/ai/costs");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, costs.StatusCode);
        Assert.Contains("migration 0008", await costs.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dashboard_routes_show_the_paper_state_and_the_chain_from_a_fill_to_the_dataset()
    {
        var db = await PaperAsync();
        var (app, http) = await StartAsync(db);
        await using var _ = app;

        var overview = await Json(http, "/overview");
        Assert.Equal(10_000m, overview.GetProperty("total").GetProperty("capital").GetDecimal());
        var run = Assert.Single(overview.GetProperty("runs").EnumerateArray());
        Assert.Equal(run.GetProperty("equity").GetDecimal() - 10_000m, run.GetProperty("pnl").GetDecimal());
        Assert.Equal(run.GetProperty("pnl").GetDecimal(), run.GetProperty("realizedPnl").GetDecimal() + run.GetProperty("unrealizedPnl").GetDecimal(), 6);
        Assert.Equal(0.60m, overview.GetProperty("limits").GetProperty("clusterCapMaxExposure").GetDecimal());
        Assert.Equal(0.54m, overview.GetProperty("limits").GetProperty("clusterWarningShare").GetDecimal());

        var sleeves = await Json(http, "/sleeves");
        Assert.Equal(["ema-trend", "supertrend"], sleeves.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.All(sleeves.EnumerateArray(), s => Assert.Equal("PaperTrading", s.GetProperty("state").GetString()));
        foreach (var position in sleeves.EnumerateArray().SelectMany(s => s.GetProperty("positions").EnumerateArray()))
        {
            Assert.True(position.GetProperty("stop").GetDecimal() >= position.GetProperty("hardStop").GetDecimal() - 0.0001m);
            Assert.Contains(position.GetProperty("stopSource").GetString(), new[] { "hard-stop-d25", "strategy" });
        }

        var fills = await Json(http, "/fills?limit=500");
        Assert.True(fills.GetArrayLength() is > 0 and <= DashboardQueries.MaxRows);
        var decisions = await Json(http, "/decisions?limit=20");
        Assert.InRange(decisions.GetArrayLength(), 1, 20);
        Assert.All(decisions.EnumerateArray(), e => Assert.Contains(e.GetProperty("kind").GetString(), new[] { "EntrySignal", "ExitSignal", "Approved", "Clipped", "Rejected", "Canceled" }));

        var buy = fills.EnumerateArray().Last(f => f.GetProperty("side").GetString() == "buy" && f.GetProperty("strategy").GetString() == "ema-trend");
        var chain = await Json(http, $"/provenance/{buy.GetProperty("id").GetInt64()}");
        Assert.Equal(buy.GetProperty("id").GetInt64(), chain.GetProperty("fill").GetProperty("id").GetInt64());
        Assert.Contains(chain.GetProperty("decision").GetProperty("kind").GetString(), new[] { "Approved", "Clipped" });
        Assert.Equal("EntrySignal", chain.GetProperty("signal").GetProperty("kind").GetString());
        Assert.True(chain.GetProperty("strategy").GetProperty("verified").GetBoolean());
        Assert.Equal("9885d24c09a7162752c659aabb01529e5ebfd8c7891e581ca7992a58be7291f8", chain.GetProperty("strategy").GetProperty("definitionHash").GetString());
        Assert.Equal("f6473462423577ec361df4ef17b1186ea618517a834430f6993c2b43ed943df3", chain.GetProperty("strategy").GetProperty("structureHash").GetString());
        Assert.Equal(run.GetProperty("manifestHash").GetString(), chain.GetProperty("cohort").GetProperty("manifestHash").GetString());
        Assert.Equal(new string('c', 64), Assert.Single(chain.GetProperty("experiments").EnumerateArray()).GetProperty("result_hash").GetString());
        Assert.Equal(["a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82"], chain.GetProperty("datasetSnapshots").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(http, "/provenance/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(http, "/provenance/abc")).StatusCode);

        var radar = await Json(http, "/radar");
        Assert.Equal(20, radar.GetProperty("symbols").GetArrayLength()); // watch symbols without candles are not shown
        Assert.Equal(2, radar.GetProperty("strategies").GetArrayLength());
        Assert.Contains(radar.GetProperty("regime").GetProperty("state").GetString(), new[] { "Bull", "Bear", "Unknown" });
        Assert.Equal(await (await Get(http, "/radar")).Content.ReadAsStringAsync(), radar.GetRawText());

        var system = await Json(http, "/system");
        Assert.False(system.GetProperty("tradingKillSwitch").ValueKind == JsonValueKind.Object && system.GetProperty("tradingKillSwitch").GetProperty("engaged").GetBoolean());
        Assert.Contains(system.GetProperty("audit").EnumerateArray(), a => a.GetProperty("event").GetString() == "paper_run_created");
        Assert.All(system.GetProperty("audit").EnumerateArray(), a => Assert.False(a.TryGetProperty("payload", out var _payload)));
        Assert.StartsWith("not readable from ops", system.GetProperty("holdoutEnvironmentVerified").GetString());
        Assert.Equal(0, (await Json(http, "/ai/costs")).GetArrayLength());
        Assert.Equal(0, (await Json(http, "/ai/traces")).GetArrayLength());

        // No secret in any response: the token, connection strings and passwords never appear.
        foreach (var path in DataRoutes.Where(p => p != "/provenance/1").Append($"/provenance/{buy.GetProperty("id").GetInt64()}"))
        {
            var body = await (await Get(http, path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(Token, body);
            Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Username=", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Watch_symbols_are_upper_case_usdt_symbols_only()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Radar:WatchSymbols"] = "WIFUSDT, ordiusdt,BTC/USDT,GMTUSDT,WIFUSDT,../x,BONKUSDT" }).Build();
        Assert.Equal(["BONKUSDT", "GMTUSDT", "WIFUSDT"], TradingHost.WatchSymbols(configuration));
    }
}
