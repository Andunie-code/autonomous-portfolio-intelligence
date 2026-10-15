using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using PortfolioIntelligence.HoldoutEvaluator.Tests.Support;

namespace PortfolioIntelligence.HoldoutEvaluator.Tests;

/// <summary>H10: the narrow API returns only allowed, rounded fields; tokens are enforced; there is no raw-data or "evaluate now" endpoint.</summary>
public class ApiTests(EvaluatorDatabase fixture) : IClassFixture<EvaluatorDatabase>
{
    private const string Submit = "submit-test-token";
    private const string Read = "read-test-token";

    [Fact]
    public async Task Api_exposes_status_and_rounded_summaries_only()
    {
        var db = await fixture.NewHoldoutAsync();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var source = new SyntheticWindowSource(3, drift: 0.002);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        EvaluatorApi.Map(app, new RegistryService(db, null), new DiagnosticService(db, source, clock), db, new EvaluatorTokens(Submit, Read));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var manifest = Manifests.Build([Manifests.Trend]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, "/cohorts", manifest, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, "/cohorts", manifest, Read)).StatusCode);

        var created = await Post(http, "/cohorts", manifest, Submit);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["cohortId", "eligibleForValidation", "registeredAt", "startsAt", "status", "windowEnd"], Names(body));
        var id = body.GetProperty("cohortId").GetInt64();

        var sealedStatus = JsonDocument.Parse(await Get(http, $"/cohorts/{id}/status", Read)).RootElement;
        Assert.Equal(["cohortId", "members"], Names(sealedStatus));
        var member = sealedStatus.GetProperty("members")[0];
        Assert.Equal("ProspectiveSealed", member.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, member.GetProperty("summary").ValueKind);

        var seeded = await Seeding.SeedAsync(db, Manifests.Build([Manifests.Family(1)]));
        await new ProspectiveService(db, source, clock, null).RunDueAsync(default);
        var revealed = JsonDocument.Parse(await Get(http, $"/cohorts/{seeded}/status", Read)).RootElement.GetProperty("members")[0];
        var summary = revealed.GetProperty("summary");
        Assert.Equal(["maxDrawdownPct", "primaryMetricPct", "status", "trades"], Names(summary));
        foreach (var field in new[] { "maxDrawdownPct", "primaryMetricPct" })
        {
            var value = summary.GetProperty(field).GetDouble();
            Assert.Equal(Math.Round(value, 1), value);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/cohorts/{id}/status")).StatusCode);
        foreach (var path in new[] { $"/cohorts/{id}/candles", $"/cohorts/{id}/equity", "/evaluate", $"/cohorts/{id}/evaluate" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Post(http, path, "{}", Submit)).StatusCode);
            using var get = new HttpRequestMessage(HttpMethod.Get, path);
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Read);
            Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(get)).StatusCode);
        }

        var diagnostic = await Post(http, "/diagnostic-evaluations",
            $$"""{ "definition": {{Manifests.Family(2)}}, "symbols": ["AAAUSDT"] }""", Submit);
        var verdict = JsonDocument.Parse(await diagnostic.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["verdict"], Names(verdict));

        await app.StopAsync();
    }

    private static async Task<HttpResponseMessage> Post(HttpClient http, string path, string json, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http.SendAsync(request);
    }

    private static async Task<string> Get(HttpClient http, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
}
