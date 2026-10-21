using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PortfolioIntelligence.Execution;

namespace PortfolioIntelligence.Trading.Host;

/// <summary>Ops API bearer token; empty means every protected request is refused.</summary>
public sealed record OpsToken(string? Value)
{
    public static OpsToken Load(IConfiguration configuration)
    {
        if (configuration["OpsApi:TokenFile"] is { Length: > 0 } file && File.Exists(file))
        {
            return new OpsToken(File.ReadAllText(file).Trim());
        }

        return new OpsToken(configuration["OpsApi:Token"]);
    }

    public bool Authorizes(HttpRequest request)
    {
        if (string.IsNullOrEmpty(Value) || request.Headers.Authorization.ToString() is not { } header || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header["Bearer ".Length..]), Encoding.UTF8.GetBytes(Value));
    }
}

/// <summary>
/// Ops API (m5-protocol §3; H23, H24): loopback only (the container port is published on 127.0.0.1). Every route
/// except the health probes needs the bearer token. There is deliberately no route for manifests, results,
/// validation status or cohort registration; <see cref="Routes"/> is pinned by a test.
/// </summary>
public static class OpsApi
{
    public static readonly IReadOnlyList<string> Routes =
    [
        "GET /health/live",
        "GET /health/ready",
        "GET /status",
        "GET /positions",
        "GET /pnl",
        "GET /risk-config",
        "GET /correlation",
        "GET /lifecycle",
        "POST /kill-switch",
        "POST /strategies/{name}/disable",
        "POST /positions/close",
        "POST /positions/close-all",
        "POST /risk-config",
    ];

    public sealed record KillSwitchRequest(bool Engaged, string Reason);

    public sealed record ReasonRequest(string Reason);

    public sealed record CloseRequest(int Sleeve, string Symbol, string Reason, long? RunId = null);

    public sealed record RiskConfigRequest(RiskConfigDocument Config, string Reason, bool Confirm);

    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", (TradingHealth health) =>
        {
            var s = health.Current;
            var ready = s.LastCycle is not null && s.Outcome is "ok";
            return Results.Json(new { status = ready ? "ready" : "degraded", s.LastCycle, s.Outcome, s.Degraded }, statusCode: ready ? 200 : 503);
        });

        Get(app, "/status", (ops, _, ct) => ops.StatusAsync(ct));
        Get(app, "/positions", (ops, _, ct) => ops.PositionsAsync(ct));
        Get(app, "/pnl", (ops, request, ct) => ops.PnlAsync(request.Query["by"].ToString() is { Length: > 0 } by ? by : "strategy", ct));
        Get(app, "/risk-config", (ops, _, ct) => ops.RiskConfigAsync(ct));
        Get(app, "/correlation", (ops, _, ct) => ops.CorrelationAsync(ct));
        Get(app, "/lifecycle", (ops, _, ct) => ops.LifecycleAsync(ct));

        Post<KillSwitchRequest>(app, "/kill-switch", (ops, r, _, ct) => ops.SetKillSwitchAsync(r.Engaged, r.Reason, ct));
        Post<ReasonRequest>(app, "/strategies/{name}/disable", (ops, r, request, ct) => ops.DisableStrategyAsync((string)request.RouteValues["name"]!, r.Reason, ct));
        Post<CloseRequest>(app, "/positions/close", (ops, r, _, ct) => ops.ClosePositionsAsync(r.RunId, r.Sleeve, r.Symbol, r.Reason, ct));
        Post<ReasonRequest>(app, "/positions/close-all", (ops, r, _, ct) => ops.ClosePositionsAsync(null, null, null, r.Reason, ct));
        Post<RiskConfigRequest>(app, "/risk-config", async (ops, r, _, ct) =>
        {
            var current = await ops.CurrentRiskAsync(ct) ?? throw new OpsRequestException("no paper run yet");
            await ops.AddRiskConfigAsync(r.Config, current, r.Reason, r.Confirm, ct);
        });
    }

    private static void Get(WebApplication app, string path, Func<OpsService, HttpRequest, CancellationToken, Task<string>> handler) =>
        app.MapGet(path, async (HttpRequest request, OpsToken token, IServiceProvider services, CancellationToken ct) =>
        {
            if (!token.Authorizes(request))
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Content(await handler(services.GetRequiredService<OpsService>(), request, ct), "application/json");
            }
            catch (OpsRequestException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });

    private static void Post<T>(WebApplication app, string path, Func<OpsService, T, HttpRequest, CancellationToken, Task> handler) =>
        app.MapPost(path, async (HttpRequest request, OpsToken token, IServiceProvider services, CancellationToken ct) =>
        {
            if (!token.Authorizes(request))
            {
                return Results.Unauthorized();
            }

            T? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<T>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "malformed JSON" });
            }

            if (body is null)
            {
                return Results.BadRequest(new { error = "body required" });
            }

            try
            {
                await handler(services.GetRequiredService<OpsService>(), body, request, ct);
                return Results.Ok(new { status = "accepted" });
            }
            catch (OpsRequestException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });
}
