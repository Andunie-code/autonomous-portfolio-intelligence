using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PortfolioIntelligence.Execution;
using PortfolioIntelligence.Execution.Radar;

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

        // Fixed-length digests: the comparison time reveals neither the content nor the length of the token (ADR-0020 §3).
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..])), SHA256.HashData(Encoding.UTF8.GetBytes(Value)));
    }
}

/// <summary>
/// Ops API (m5-protocol §3; H23, H24) and the read-only M9a dashboard (ADR-0020): loopback only (the container port
/// is published on 127.0.0.1). Every route except the health probes and the three static dashboard files needs the
/// bearer token; the dashboard files carry no data. There is deliberately no route for manifests, results,
/// validation status or cohort registration, and the dashboard adds no write route; <see cref="Routes"/> is pinned
/// by a test. Requests whose Host is not a loopback name are refused (421) against DNS rebinding.
/// M6 (ADR-0021 §7) adds two read routes (/testnet, /promotion) and two confirmed user actions: the
/// PaperTrading → TestnetActive transition (V10) and the Testnet rebaseline after an exchange reset.
/// M8 (ADR-0023) adds the allocator's read route (/allocator) and its confirmed mode transition (/allocator/mode).
/// M9–M10 (ADR-0024) add two read routes: the chain of a Testnet exchange order (/provenance/order/{id}) and the
/// computed alarms (/alarms). No write route.
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
        "GET /overview",
        "GET /sleeves",
        "GET /fills",
        "GET /decisions",
        "GET /provenance/{fillId}",
        "GET /provenance/order/{id}",
        "GET /radar",
        "GET /system",
        "GET /ai/costs",
        "GET /ai/traces",
        "GET /dashboard",
        "GET /dashboard/app.js",
        "GET /dashboard/app.css",
        "GET /testnet",
        "GET /promotion",
        "GET /allocator",
        "GET /alarms",
        "POST /kill-switch",
        "POST /strategies/{name}/disable",
        "POST /positions/close",
        "POST /positions/close-all",
        "POST /risk-config",
        "POST /strategies/{name}/testnet-activate",
        "POST /testnet/rebaseline",
        "POST /allocator/mode",
    ];

    /// <summary>Applied to every response (ADR-0020 §3); the dashboard has no inline script or style.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public sealed record KillSwitchRequest(bool Engaged, string Reason);

    public sealed record ReasonRequest(string Reason);

    public sealed record CloseRequest(int Sleeve, string Symbol, string Reason, long? RunId = null);

    public sealed record RiskConfigRequest(RiskConfigDocument Config, string Reason, bool Confirm);

    public sealed record ConfirmedRequest(string Reason, bool Confirm);

    public sealed record AllocatorModeRequest(string Mode, string? ProtocolHash, long? QualificationId, string Reason, bool Confirm);

    public static void Map(WebApplication app)
    {
        var allowedHosts = (app.Configuration["OpsApi:AllowedHosts"] ?? "127.0.0.1;localhost;[::1]")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.CacheControl = "no-store";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XFrameOptions = "DENY";
            if (!context.Request.Path.StartsWithSegments("/health") && !allowedHosts.Contains(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            await next();
        });

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", (TradingHealth health) =>
        {
            var s = health.Current;
            var ready = s.LastCycle is not null && s.Outcome is "ok";
            return Results.Json(new { status = ready ? "ready" : "degraded", s.LastCycle, s.Outcome, s.Degraded }, statusCode: ready ? 200 : 503);
        });

        Get(app, "/status", (s, _, ct) => s.GetRequiredService<OpsService>().StatusAsync(ct));
        Get(app, "/positions", (s, _, ct) => s.GetRequiredService<OpsService>().PositionsAsync(ct));
        Get(app, "/pnl", (s, request, ct) => s.GetRequiredService<OpsService>().PnlAsync(request.Query["by"].ToString() is { Length: > 0 } by ? by : "strategy", ct));
        Get(app, "/risk-config", (s, _, ct) => s.GetRequiredService<OpsService>().RiskConfigAsync(ct));
        Get(app, "/correlation", (s, _, ct) => s.GetRequiredService<OpsService>().CorrelationAsync(ct));
        Get(app, "/lifecycle", (s, _, ct) => s.GetRequiredService<OpsService>().LifecycleAsync(ct));

        // M9a dashboard data (ADR-0020 §4): read-only.
        Get(app, "/overview", (s, _, ct) => s.GetRequiredService<DashboardQueries>().OverviewAsync(ct));
        Get(app, "/sleeves", (s, _, ct) => s.GetRequiredService<DashboardQueries>().SleevesAsync(ct));
        Get(app, "/fills", (s, request, ct) => s.GetRequiredService<DashboardQueries>().FillsAsync(Limit(request), ct));
        Get(app, "/decisions", (s, request, ct) => s.GetRequiredService<DashboardQueries>().DecisionsAsync(Limit(request), ct));
        Get(app, "/provenance/{fillId}", async (s, request, ct) =>
            long.TryParse((string?)request.RouteValues["fillId"], out var id)
                ? await s.GetRequiredService<DashboardQueries>().ProvenanceAsync(id, ct) ?? throw new OpsRequestException($"no fill {id}")
                : throw new OpsRequestException("fillId must be a number"));
        Get(app, "/provenance/order/{id}", async (s, request, ct) =>
            (string?)request.RouteValues["id"] is { Length: > 0 and <= 36 } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? await s.GetRequiredService<DashboardQueries>().OrderProvenanceAsync(id, ct) ?? throw new OpsRequestException($"no exchange order {id}")
                : throw new OpsRequestException("id must be a clientOrderId or an exchange order id"));
        Get(app, "/radar", (s, _, ct) => s.GetRequiredService<RadarService>().ReportAsync(s.GetRequiredService<TimeProvider>().GetUtcNow(), ct));
        Get(app, "/system", async (s, _, ct) =>
        {
            var health = s.GetRequiredService<TradingHealth>().Current;
            var json = JsonNode(await s.GetRequiredService<DashboardQueries>().SystemAsync(ct));
            json["host"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { health.LastCycle, health.Outcome, health.Degraded }, JsonSerializerOptions.Web));
            return json.ToJsonString();
        });
        Get(app, "/ai/costs", (s, _, ct) => s.GetRequiredService<DashboardQueries>().AiCostsAsync(s.GetRequiredService<AiBudget>(), s.GetRequiredService<TimeProvider>().GetUtcNow(), ct));
        Get(app, "/ai/traces", (s, request, ct) => s.GetRequiredService<DashboardQueries>().AiTracesAsync(Limit(request), ct));

        // M6 (m6-protocol §7): Testnet orders and protective stops, and the D30 progress.
        Get(app, "/testnet", (s, _, ct) => s.GetRequiredService<DashboardQueries>().TestnetAsync(s.GetRequiredService<TestnetRuntime>().Executor is not null, ct));
        Get(app, "/promotion", (s, _, ct) => s.GetRequiredService<DashboardQueries>().PromotionAsync(s.GetRequiredService<TimeProvider>().GetUtcNow(), ct));

        // M8 (m8-protocol §4): the allocator's mode, decisions and shadow values.
        Get(app, "/allocator", (s, request, ct) => s.GetRequiredService<DashboardQueries>().AllocatorAsync(Limit(request), ct));

        // M10 (ADR-0024): the computed alarms; nothing is stored or sent.
        Get(app, "/alarms", (s, _, ct) => s.GetRequiredService<DashboardQueries>().AlarmsAsync(s.GetRequiredService<AiBudget>(), s.GetRequiredService<TimeProvider>().GetUtcNow(), ct));

        // The dashboard's static files: no data and no token (the page asks for the token and sends it as a header).
        Asset(app, "/dashboard", "index.html", "text/html; charset=utf-8");
        Asset(app, "/dashboard/app.js", "app.js", "text/javascript; charset=utf-8");
        Asset(app, "/dashboard/app.css", "app.css", "text/css; charset=utf-8");

        Post<KillSwitchRequest>(app, "/kill-switch", async (ops, r, request, ct) =>
        {
            await ops.SetKillSwitchAsync(r.Engaged, r.Reason, ct);
            if (r.Engaged)
            {
                // Open entry orders on the exchange are cancelled now, not at the next hourly cycle (m6-protocol §5.5).
                request.HttpContext.RequestServices.GetRequiredService<MaintenanceSignal>().Request();
            }
        });
        Post<ReasonRequest>(app, "/strategies/{name}/disable", (ops, r, request, ct) => ops.DisableStrategyAsync((string)request.RouteValues["name"]!, r.Reason, ct));
        Post<CloseRequest>(app, "/positions/close", (ops, r, _, ct) => ops.ClosePositionsAsync(r.RunId, r.Sleeve, r.Symbol, r.Reason, ct));
        Post<ReasonRequest>(app, "/positions/close-all", (ops, r, _, ct) => ops.ClosePositionsAsync(null, null, null, r.Reason, ct));
        Post<RiskConfigRequest>(app, "/risk-config", async (ops, r, _, ct) =>
        {
            var current = await ops.CurrentRiskAsync(ct) ?? throw new OpsRequestException("no paper run yet");
            await ops.AddRiskConfigAsync(r.Config, current, r.Reason, r.Confirm, ct);
        });
        Post<ConfirmedRequest>(app, "/strategies/{name}/testnet-activate", (ops, r, request, ct) =>
            ops.ActivateTestnetAsync((string)request.RouteValues["name"]!, r.Reason, r.Confirm, ct));
        Post<ConfirmedRequest>(app, "/testnet/rebaseline", (ops, r, _, ct) => ops.RebaselineTestnetAsync(r.Reason, r.Confirm, ct));
        Post<AllocatorModeRequest>(app, "/allocator/mode", (ops, r, _, ct) => ops.SetAllocatorModeAsync(r.Mode, r.ProtocolHash, r.QualificationId, r.Reason, r.Confirm, ct));
    }

    private static System.Text.Json.Nodes.JsonObject JsonNode(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

    private static int? Limit(HttpRequest request) => int.TryParse(request.Query["limit"].ToString(), out var limit) ? limit : null;

    private static void Asset(WebApplication app, string path, string file, string contentType)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"PortfolioIntelligence.Trading.Host.Dashboard.{file}")
            ?? throw new InvalidOperationException($"Dashboard resource {file} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        app.MapGet(path, () => Results.Text(content, contentType));
    }

    private static void Get(WebApplication app, string path, Func<IServiceProvider, HttpRequest, CancellationToken, Task<string>> handler) =>
        app.MapGet(path, async (HttpRequest request, OpsToken token, IServiceProvider services, CancellationToken ct) =>
        {
            if (!token.Authorizes(request))
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Content(await handler(services, request, ct), "application/json");
            }
            catch (OpsRequestException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            catch (PostgresException e) when (DashboardQueries.IsSchemaBehind(e))
            {
                return Results.Json(new { error = DashboardQueries.SchemaBehindMessage }, statusCode: StatusCodes.Status503ServiceUnavailable);
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
            catch (OpsConflictException e)
            {
                return Results.Conflict(new { error = e.Message });
            }
        });
}
