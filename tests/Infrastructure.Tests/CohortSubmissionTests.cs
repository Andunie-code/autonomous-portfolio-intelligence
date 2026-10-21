using System.Net;
using Microsoft.Extensions.Configuration;
using PortfolioIntelligence.Research.Host;

namespace PortfolioIntelligence.Infrastructure.Tests;

/// <summary>
/// The Research.Host cohort registration client (ADR-0019 §5) against a fake evaluator: it sends the manifest with
/// the submit-only bearer token, never a server-assigned field, prints the server's fields, maps 400/409 to exit
/// codes, and refuses a manifest whose definition hashes do not verify before any request.
/// </summary>
public class CohortSubmissionTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pi-submit-");

    public void Dispose() => _directory.Delete(recursive: true);

    private static string Kohort0()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "cohorts", "cohort-0.json");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException("docs/cohorts/cohort-0.json");
    }

    private (IConfiguration Configuration, string Token) Setup(string manifest)
    {
        var manifestPath = Path.Combine(_directory.FullName, "cohort.json");
        var tokenPath = Path.Combine(_directory.FullName, "token");
        var token = Guid.NewGuid().ToString("N");
        File.WriteAllText(manifestPath, manifest);
        File.WriteAllText(tokenPath, token + "\n");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Registration:Manifest"] = manifestPath,
            ["Registration:TokenFile"] = tokenPath,
            ["Registration:Url"] = "http://evaluator.test:8080",
        }).Build();
        return (configuration, token);
    }

    [Fact]
    public async Task A_registered_manifest_prints_the_server_assigned_fields()
    {
        var manifest = Kohort0();
        var (configuration, token) = Setup(manifest);
        var handler = new FakeEvaluator(HttpStatusCode.Created,
            """{"cohortId":2,"status":"ProspectiveSealed","registeredAt":"2026-10-22T10:00:00Z","startsAt":"2026-10-24T00:00:00Z","windowEnd":"2027-10-24T00:00:00Z","eligibleForValidation":true}""");
        var output = new StringWriter();

        Assert.Equal(0, await CohortSubmission.RunAsync(configuration, output, handler, default));

        Assert.Equal("http://evaluator.test:8080/cohorts", handler.Uri);
        Assert.Equal($"Bearer {token}", handler.Authorization);
        Assert.Equal(manifest, handler.Body);
        Assert.DoesNotContain("registeredAt", handler.Body);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("cohortId=2", lines);
        Assert.Contains("startsAt=2026-10-24T00:00:00Z", lines);
        Assert.Contains(lines, l => l.StartsWith("manifestHash=", StringComparison.Ordinal) && l.Length == "manifestHash=".Length + 64);
        Assert.DoesNotContain(token, output.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 2)]
    [InlineData(HttpStatusCode.Conflict, 3)]
    [InlineData(HttpStatusCode.Unauthorized, 4)]
    public async Task Rejections_map_to_exit_codes(HttpStatusCode status, int exit)
    {
        var (configuration, _) = Setup(Kohort0());
        Assert.Equal(exit, await CohortSubmission.RunAsync(configuration, new StringWriter(), new FakeEvaluator(status, """{"error":"x"}"""), default));
    }

    [Fact]
    public async Task A_manifest_whose_definition_hash_does_not_verify_is_not_sent()
    {
        var (configuration, _) = Setup(Kohort0().Replace("9885d24c", "0885d24c", StringComparison.Ordinal));
        var handler = new FakeEvaluator(HttpStatusCode.Created, "{}");

        Assert.Equal(2, await CohortSubmission.RunAsync(configuration, new StringWriter(), handler, default));
        Assert.Null(handler.Uri);
    }

    private sealed class FakeEvaluator(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public string? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }
}
