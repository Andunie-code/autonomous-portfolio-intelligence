using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace PortfolioIntelligence.HoldoutEvaluator;

/// <summary>
/// H19 (m5-protocol §6.2): H9, H10, H15, H16, V4 and V5 repeated by the real evaluator image with the real logins,
/// on the scratch database holdout_henv only (the authoritative registry is immutable and has an open-cohort limit).
/// Synthetic window data; no status relay; output is "id PASS|FAIL reason" lines only.
/// </summary>
public static class SelfTest
{
    private const string Digest = "sha256:" + "4e56" + "0000000000000000000000000000000000000000000000000000000000";
    private const string EngineSha = "henv-selftest";

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    public static async Task<int> RunAsync(HoldoutDatabase database, TextWriter output, CancellationToken ct)
    {
        if (database.DatabaseName != "holdout_henv")
        {
            output.WriteLine($"H19 FAIL wrong-database (the self-test runs only on holdout_henv, not {database.DatabaseName})");
            return 1;
        }

        var failures = 0;
        async Task Check(string id, string name, Func<Task<bool>> test)
        {
            bool ok;
            string reason;
            try
            {
                ok = await test();
                reason = ok ? "ok" : "unexpected-outcome";
            }
            catch (Exception e)
            {
                ok = false;
                reason = e.GetType().Name;
            }

            failures += ok ? 0 : 1;
            output.WriteLine($"{id} {(ok ? "PASS" : "FAIL")} {name} {reason}");
        }

        await using (var connection = await database.OpenAsync(null, ct))
        {
            await using var release = new NpgsqlCommand("INSERT INTO results.evaluator_releases (image_digest, engine_sha) VALUES ($1, $2) ON CONFLICT DO NOTHING", connection);
            release.Parameters.AddWithValue(Digest);
            release.Parameters.AddWithValue(EngineSha);
            await release.ExecuteNonQueryAsync(ct);
        }

        var now = DateTimeOffset.UtcNow;
        var source = new SyntheticWindowSource(19, drift: 0.001);
        var registry = new RegistryService(database, null);

        await Check("H15", "engine-sha-mismatch-rejected", async () =>
            await Rejected(() => registry.RegisterAsync(Manifest([Family(0)], engineSha: "not-the-release"), ct)));
        await Check("H16", "fourth-strategy-rejected", async () =>
            await Rejected(() => registry.RegisterAsync(Manifest([Family(1), Family(2), Family(3), Family(4)]), ct)));

        // Two closed attempts of one family, then the third attempt is refused (D21).
        await Check("H16", "third-family-attempt-rejected", async () =>
        {
            foreach (var registered in new[] { new DateTimeOffset(2023, 1, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 9, 1, 12, 0, 0, TimeSpan.Zero) })
            {
                await CohortSeeder.SeedAsync(database, Manifest([Family(5)], seed: registered.Month), registered, null, ct);
            }

            await new ProspectiveService(database, source, new Clock(now), null).RunDueAsync(ct);
            return await Rejected(() => registry.RegisterAsync(Manifest([Family(5)], seed: 99), ct));
        });

        await Check("H10", "api-fields-tokens-and-no-raw-data", () => ApiAsync(database, ct));

        long open = 0;
        await Check("H16", "third-open-cohort-rejected", async () =>
        {
            open = (await registry.RegisterAsync(Manifest([Family(7)]), ct)).CohortId;
            return await Rejected(() => registry.RegisterAsync(Manifest([Family(8)]), ct));
        });

        await Check("V4", "early-evaluation-rejected-by-application", async () =>
        {
            try
            {
                await new ProspectiveService(database, source, new Clock(now), null).RerunAsync(open, "rerun-data-quality", "{\"evidence\":\"henv\"}", ct);
                return false;
            }
            catch (EarlyEvaluationException)
            {
                return true;
            }
        });
        await Check("V4", "early-import-rejected-by-trigger", async () =>
            await Rejected(() => Execute(database, "eval_prospective",
                $"INSERT INTO prospective.candles (cohort_id, symbol, open_time, open, high, low, close, quote_volume) VALUES ({open}, 'AAAUSDT', now(), 1, 1, 1, 1, 1)", ct)));
        await Check("V5", "manifest-update-rejected", async () => await Rejected(() => Execute(database, null, $"UPDATE results.cohort_manifests SET l_c_months = 7 WHERE cohort_id = {open}", ct)));
        await Check("V5", "manifest-delete-rejected", async () => await Rejected(() => Execute(database, null, $"DELETE FROM results.cohort_manifests WHERE cohort_id = {open}", ct)));
        await Check("V5", "member-added-to-frozen-cohort-rejected", async () => await Rejected(() => Execute(database, "eval_registry",
            $"INSERT INTO results.cohort_members (cohort_id, member_index, strategy_name, definition_hash, structure_hash) VALUES ({open}, 2, 'x', repeat('a', 64), repeat('b', 64))", ct)));

        var diagnostics = new DiagnosticService(database, source, new Clock(now));
        await Check("H9", "second-diagnostic-of-a-family-rejected", async () =>
        {
            var request = new DiagnosticRequest(Family(9), ["AAAUSDT", "BBBUSDT"], null);
            await diagnostics.EvaluateAsync(request, ct);
            return await Rejected(() => diagnostics.EvaluateAsync(request, ct));
        });

        output.WriteLine($"H19 {(failures == 0 ? "PASS" : "FAIL")} summary {failures}-failures");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> Rejected(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (Exception e) when (e is RegistryRejectedException or ManifestException or PostgresException or EarlyEvaluationException)
        {
            return true;
        }
    }

    private static async Task Execute(HoldoutDatabase database, string? role, string sql, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(role, ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ApiAsync(HoldoutDatabase database, CancellationToken ct)
    {
        const string submit = "henv-submit-token-0001";
        const string read = "henv-read-token-0001";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        EvaluatorApi.Map(app, new RegistryService(database, null), new DiagnosticService(database, new SyntheticWindowSource(3), new SystemClock()), database, new EvaluatorTokens(submit, read));
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var manifest = Manifest([Family(6)]);
        var anonymous = await http.PostAsync("/cohorts", new StringContent(manifest, Encoding.UTF8, "application/json"), ct);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/cohorts") { Content = new StringContent(manifest, Encoding.UTF8, "application/json") };
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", submit);
        var created = await http.SendAsync(post, ct);
        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement;
        var fields = body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/cohorts/{body.GetProperty("cohortId").GetInt64()}/status");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", read);
        var status = JsonDocument.Parse(await (await http.SendAsync(get, ct)).Content.ReadAsStringAsync(ct)).RootElement;
        using var evaluate = new HttpRequestMessage(HttpMethod.Post, "/evaluate") { Content = new StringContent("{}") };
        evaluate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", submit);
        var noEvaluate = await http.SendAsync(evaluate, ct);
        await app.StopAsync(ct);
        return anonymous.StatusCode == HttpStatusCode.Unauthorized
               && created.StatusCode == HttpStatusCode.Created
               && fields.SequenceEqual(["cohortId", "eligibleForValidation", "registeredAt", "startsAt", "status", "windowEnd"])
               && status.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(["cohortId", "members"])
               && noEvaluate.StatusCode == HttpStatusCode.NotFound;
    }

    private static string Family(int i)
    {
        string[] types = ["sma", "ema", "rsi", "roc", "volume_sma", "atr", "adx", "macd", "bollinger", "donchian"];
        var type = types[i % types.Length];
        var (parameters, output, right) = type switch
        {
            "macd" => ("{ \"fast\": 5, \"slow\": 12, \"signal\": 4 }", "line", "{ \"const\": 0 }"),
            "adx" => ("{ \"period\": 7 }", "adx", "{ \"const\": 10 }"),
            "bollinger" => ("{ \"period\": 10, \"k\": 2 }", "middle", "{ \"price\": \"low\" }"),
            "donchian" => ("{ \"period\": 10 }", "middle", "{ \"price\": \"low\" }"),
            "rsi" => ("{ \"period\": 7 }", "value", "{ \"const\": 30 }"),
            "roc" => ("{ \"period\": 7 }", "value", "{ \"const\": -1 }"),
            "atr" or "volume_sma" => ("{ \"period\": 7 }", "value", "{ \"const\": 0 }"),
            _ => ("{ \"period\": 7 }", "value", "{ \"price\": \"low\", \"mult\": 0.5 }"),
        };
        return $$"""
            { "dsl": "1", "name": "henv-{{i}}", "interval": "4h",
              "indicators": [ { "id": "x", "type": "{{type}}", "params": {{parameters}} } ],
              "entry": { "op": "gt", "args": [ { "ref": "x.{{output}}" }, {{right}} ] },
              "exit": { "op": "lt", "args": [ { "price": "close" }, { "const": 0.001 } ] } }
            """;
    }

    private static string Manifest(IEnumerable<string> definitions, string engineSha = EngineSha, int seed = 7) => new JsonObject
    {
        ["manifestVersion"] = "1",
        ["protocol"] = new JsonObject
        {
            ["version"] = "henv-selftest", ["commit"] = "henv", ["alpha"] = 0.05, ["power"] = 0.80, ["deltaFloor"] = 0.05, ["deltaShrink"] = 0.50,
            ["deltaCap"] = 0.15, ["deltaMode"] = "a", ["maxDrawdown"] = 0.25, ["bootstrapReplicates"] = 500, ["bootstrapSeed"] = seed,
            ["primaryMetric"] = "annualised-log-return", ["benchmark"] = "cash",
        },
        ["strategies"] = new JsonArray(definitions.Select(d => (JsonNode)new JsonObject { ["definition"] = JsonNode.Parse(d), ["researchMetric"] = 0 }).ToArray()),
        ["pairs"] = new JsonArray(new JsonObject { ["symbol"] = "AAAUSDT", ["category"] = "core" }, new JsonObject { ["symbol"] = "BBBUSDT", ["category"] = "core" }),
        ["interval"] = "4h",
        ["dataSource"] = "synthetic",
        ["checksumPolicy"] = "sha256-required",
        ["engineSha"] = engineSha,
        ["imageDigest"] = Digest,
        ["costModel"] = "cost-model v1",
        ["allocation"] = "allocation v1",
        ["risk"] = new JsonObject
        {
            ["maxOpenPositions"] = 5, ["maxAssetWeight"] = 0.20, ["maxTotalExposure"] = 0.80, ["dailyLossLimit"] = 0.03, ["maxDrawdownHalt"] = 0.15, ["maxPositionLoss"] = 0.10,
        },
        ["initialCapital"] = 10000,
        ["power"] = new JsonObject { ["months"] = 6, ["plannedTrades"] = 1, ["underpowered"] = false },
        ["dataQuality"] = "henv",
    }.ToJsonString();
}
