using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace PortfolioIntelligence.HoldoutEvaluator;

/// <summary>Bearer tokens of the narrow API (ADR-0009): submit-only for POSTs, read-status for GET.</summary>
public sealed record EvaluatorTokens(string? Submit, string? ReadStatus);

/// <summary>
/// The evaluator's only interface (ADR-0009 rev. 4): POST /cohorts, POST /diagnostic-evaluations and
/// GET /cohorts/{id}/status. Responses carry status and the revealed rounded summary only (H10): no raw data,
/// no time series, no metrics of unrevealed cohorts, and no "evaluate now".
/// </summary>
public static class EvaluatorApi
{
    public static void Map(WebApplication app, RegistryService registry, DiagnosticService diagnostics, HoldoutDatabase database, EvaluatorTokens tokens)
    {
        app.MapPost("/cohorts", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!Authorized(request, tokens.Submit))
            {
                return Results.Unauthorized();
            }

            var body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync(ct);
            try
            {
                var r = await registry.RegisterAsync(body, ct);
                return Results.Json(new
                {
                    cohortId = r.CohortId,
                    status = "ProspectiveSealed",
                    registeredAt = r.RegisteredAt,
                    startsAt = r.StartsAt,
                    windowEnd = r.WindowEnd,
                    eligibleForValidation = r.ProtocolComplete,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (ManifestException e)
            {
                return Results.Json(new { error = "invalid manifest", details = e.Errors }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (RegistryRejectedException e)
            {
                return Results.Json(new { error = "rejected", reason = e.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/diagnostic-evaluations", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!Authorized(request, tokens.Submit))
            {
                return Results.Unauthorized();
            }

            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                var root = document.RootElement;
                var symbols = root.GetProperty("symbols").EnumerateArray().Select(s => s.GetString()!).ToList();
                DateTimeOffset? cutoff = root.TryGetProperty("modelKnowledgeCutoff", out var k) && k.ValueKind == JsonValueKind.String ? k.GetDateTimeOffset() : null;
                var verdict = await diagnostics.EvaluateAsync(new DiagnosticRequest(root.GetProperty("definition").GetRawText(), symbols, cutoff), ct);
                return Results.Json(new { verdict });
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or Kernel.Dsl.DslException)
            {
                return Results.Json(new { error = "invalid request" }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (RegistryRejectedException e)
            {
                return Results.Json(new { error = "rejected", reason = e.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapGet("/cohorts/{id:long}/status", async (long id, HttpRequest request, CancellationToken ct) =>
        {
            if (!Authorized(request, tokens.ReadStatus))
            {
                return Results.Unauthorized();
            }

            await using var connection = await database.OpenAsync("eval_prospective", ct);
            await using var command = new NpgsqlCommand("""
                SELECT v.member_index, v.status,
                       (SELECT r.summary::text FROM results.prospective_results r
                        WHERE r.cohort_id = v.cohort_id AND r.member_index = v.member_index ORDER BY r.run_id DESC, r.id DESC LIMIT 1)
                FROM results.validation_status v WHERE v.cohort_id = $1 ORDER BY v.member_index
                """, connection);
            command.Parameters.AddWithValue(id);
            var members = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var summary = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(2));
                members.Add(new { memberIndex = reader.GetInt32(0), status = reader.GetString(1), summary });
            }

            return members.Count == 0 ? Results.NotFound() : Results.Json(new { cohortId = id, members });
        });
    }

    private static bool Authorized(HttpRequest request, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(header[prefix.Length..]);
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(expected));
    }
}
