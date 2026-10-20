using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Kernel.Backtest;
using PortfolioIntelligence.MarketData.Live;
using PortfolioIntelligence.Trading.Host;
using PortfolioIntelligence.Trading.Tests.Support;

namespace PortfolioIntelligence.Trading.Tests;

/// <summary>Ops API (m5-protocol §3; H23, H24): routes, token, RiskConfig versions, PnL of open cohorts.</summary>
public class OpsApiTests(OpsDatabase fixture) : IClassFixture<OpsDatabase>
{
    private const string Token = "ops-test-token-123";

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(OpsDatabase.OpsDb db)
    {
        var app = TradingHost.Build([
            $"--ConnectionStrings:Trading={db.As("trading_app")}",
            $"--ConnectionStrings:OpsApi={db.As("ops_api")}",
            $"--OpsApi:Token={Token}",
            "--Trading:Worker=disabled",
            "--urls=http://127.0.0.1:0",
        ]);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string path, object? body, string? token = Token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return http.SendAsync(request);
    }

    [Fact]
    public async Task Routes_are_pinned_and_none_touches_manifests_results_or_validation()
    {
        var db = await fixture.NewAsync();
        var (app, http) = await StartAsync(db);
        await using var _ = app;

        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal).ToList();

        Assert.Equal(OpsApi.Routes.Order(StringComparer.Ordinal), routes);
        Assert.DoesNotContain(routes, r => r.Contains("cohort", StringComparison.OrdinalIgnoreCase) || r.Contains("manifest", StringComparison.OrdinalIgnoreCase)
                                           || r.Contains("result", StringComparison.OrdinalIgnoreCase) || r.Contains("validat", StringComparison.OrdinalIgnoreCase));
        foreach (var path in new[] { "/status", "/positions", "/pnl", "/risk-config" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Get, path, null, token: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Get, path, null, token: "wrong")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Post, "/kill-switch", new { engaged = true, reason = "x" }, token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(http, HttpMethod.Post, "/cohorts", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Risk_config_versions_need_confirmation_to_loosen_and_open_cohort_pnl_is_visible()
    {
        var db = await fixture.NewAsync();
        var manifest = await db.SeedKohort0Async();
        var now = new DateTimeOffset(2026, 3, 1, 1, 0, 0, TimeSpan.Zero);
        var start = PaperTrader.NextBoundary(now) - PaperTrader.Warmup;
        await db.AddCandlesAsync(manifest.Pairs.SelectMany((p, i) => LiveSynthetic.Walk(p.Symbol, start, 300, 900 + i)));
        await new PaperTrader(new PaperStore(db.As("trading_app")), new LiveCandleSource(new LiveCandleStore(db.As("trading_app"))), CostModel.V1).ProcessAsync(now.AddDays(1), default);
        var (app, http) = await StartAsync(db);
        await using var _ = app;

        var current = PaperTrader.InitialRisk(manifest);
        var looser = current with { ExperimentalPairsMaxExposure = 0.20m };
        var refused = await Send(http, HttpMethod.Post, "/risk-config", new { config = looser, reason = "test", confirm = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("confirm", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "/risk-config", new { config = current with { ExperimentalPairsMaxExposure = 0.05m }, reason = "tighter", confirm = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "/risk-config", new { config = looser, reason = "user decision", confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(http, HttpMethod.Post, "/risk-config", new { config = current with { MaxAssetWeight = 2m }, reason = "x", confirm = true })).StatusCode);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM trading.risk_config_versions"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM audit.events WHERE event = 'risk_config_version_added'"));
        var latest = JsonDocument.Parse(await (await Send(http, HttpMethod.Get, "/risk-config", null)).Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal(0.20m, latest.GetProperty("config").GetProperty("experimentalPairsMaxExposure").GetDecimal());

        var pnl = JsonDocument.Parse(await (await Send(http, HttpMethod.Get, "/pnl?by=cohort", null)).Content.ReadAsStringAsync()).RootElement;
        var row = Assert.Single(pnl.EnumerateArray());
        Assert.Equal(1, row.GetProperty("key").GetInt64());
        Assert.Equal(10_000m, row.GetProperty("capital").GetDecimal());
        Assert.Equal(2, JsonDocument.Parse(await (await Send(http, HttpMethod.Get, "/pnl?by=strategy", null)).Content.ReadAsStringAsync()).RootElement.GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "/kill-switch", new { engaged = true, reason = "drill" })).StatusCode);
        Assert.True(await new PaperStore(db.As("trading_app")).KillSwitchEngagedAsync(default));
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "/kill-switch", new { engaged = false, reason = "drill over" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(http, HttpMethod.Post, "/positions/close-all", new { reason = "" })).StatusCode);
    }
}
