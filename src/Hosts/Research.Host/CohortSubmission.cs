using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PortfolioIntelligence.Kernel.Backtest;

namespace PortfolioIntelligence.Research.Host;

/// <summary>
/// The cohort registration client (ADR-0019 §5; architecture §10: Research.Host holds only the submit-only token).
/// Sends the committed manifest to the evaluator's POST /cohorts on the internal eval network and prints the
/// server-assigned fields as key=value lines for the user's activation script. It never sends registered_at or A_c
/// (the evaluator assigns them) and has no access to the holdout database. Configuration:
///   Registration:Manifest    path of the manifest inside the container (mounted read-only)
///   Registration:Url         evaluator base URL (default http://172.30.2.20:8080, its static address on eval)
///   Registration:TokenFile   submit-only token file (default /run/secrets/submit_token)
/// Exit codes: 0 registered, 2 invalid manifest (400), 3 rejected by the registry (409), 4 other failure.
/// </summary>
public static class CohortSubmission
{
    public const string DefaultUrl = "http://172.30.2.20:8080";
    public const string DefaultTokenFile = "/run/secrets/submit_token";

    public static async Task<int> RunAsync(IConfiguration configuration, TextWriter output, HttpMessageHandler? handler, CancellationToken ct)
    {
        var path = configuration["Registration:Manifest"];
        var tokenFile = configuration["Registration:TokenFile"] ?? DefaultTokenFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !File.Exists(tokenFile))
        {
            output.WriteLine("submit-cohort: Registration:Manifest and the submit token file must exist.");
            return 4;
        }

        var manifest = await File.ReadAllTextAsync(path, ct);
        string hash;
        try
        {
            // The operational reader recomputes every definition hash; a manifest Trading would refuse is not sent.
            hash = CohortManifestReader.Read(manifest).Hash;
        }
        catch (FormatException e)
        {
            output.WriteLine($"submit-cohort: manifest rejected locally: {e.Message}");
            return 2;
        }

        output.WriteLine($"manifestHash={hash}");
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(configuration["Registration:Url"] ?? DefaultUrl), "/cohorts"))
        {
            Content = new StringContent(manifest, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile, ct)).Trim());
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            output.WriteLine($"submit-cohort: evaluator not reachable: {e.Message}");
            return 4;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created:
                {
                    using var document = JsonDocument.Parse(body);
                    foreach (var name in new[] { "cohortId", "status", "registeredAt", "startsAt", "windowEnd", "eligibleForValidation" })
                    {
                        output.WriteLine($"{name}={(document.RootElement.TryGetProperty(name, out var value) ? value.ToString() : "")}");
                    }

                    return 0;
                }

                case HttpStatusCode.BadRequest:
                    output.WriteLine($"submit-cohort: invalid manifest: {body}");
                    return 2;
                case HttpStatusCode.Conflict:
                    output.WriteLine($"submit-cohort: rejected by the registry: {body}");
                    return 3;
                default:
                    output.WriteLine($"submit-cohort: HTTP {(int)response.StatusCode}");
                    return 4;
            }
        }
    }
}
