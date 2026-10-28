using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using PortfolioIntelligence.Ai;
using PortfolioIntelligence.Ai.Gemini;
using PortfolioIntelligence.Ai.Persistence;
using PortfolioIntelligence.Research.Sources;
using PortfolioIntelligence.SourceGateway;
using PortfolioIntelligence.SourceGateway.Text;

// M7 sources command line (docs/m7-protocol.md §10). The user's runs only; Claude Code cannot pass the network flag.
//   search --allow-network --terms "a,b" [--field abs|ti|all] [--categories q-fin.*,stat.ML] [--max 10] [--show-titles]
//                                         — arXiv API search (pre-cutoff versions only; U-M7-2).
//   fetch --allow-network --ids 2301.01234v2,... — records at most 10 versions: PDF if robots.txt allows, else the API abstract.
//   extract --allow-network [--document <id>] — FindingExtraction on recorded documents; refused while web research is closed (D20).
//   docs                                  — recorded documents (operational fields only; no network).
//   provenance-report                     — re-checks every document against the cache: content and text hashes, dates (no network).
// Output never contains document text, findings' claims or quotes; titles only with --show-titles.
const string ConfirmationWord = "EVET";
const int MaxDocumentsPerRun = 10;

var command = args.FirstOrDefault() ?? "";
var configuration = SourceConfiguration.LoadDefault();
var policy = new SourceRequestPolicy(configuration);
var time = TimeProvider.System;
var cache = new SourceCache(SourceCache.DefaultRoot, FindRepositoryRoot());

switch (command)
{
    case "search":
    {
        if (!Confirm($"arXiv API search on https://{ArxivQuery.ApiBase[8..]} (one request; at least {configuration.MinIntervalSeconds} s between requests)"))
        {
            return 1;
        }

        var (fetcher, _) = Online();
        var terms = RequireOption("--terms").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var categories = Option("--categories")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var (entries, rejected, source) = await new ArxivClient(fetcher, configuration).SearchAsync(terms, Option("--field") ?? "abs", categories,
            int.Parse(Option("--max") ?? "10", CultureInfo.InvariantCulture), CancellationToken.None);
        Console.WriteLine($"Response {source.ContentSha256[..12]} ({source.Body.Length} bytes{(source.FromCache ? ", from cache" : "")}): {entries.Count} usable record(s); " +
            $"dropped: after cutoff {rejected[ArxivRejection.AfterCutoff]}, category {rejected[ArxivRejection.CategoryNotAllowed]}, unparseable {rejected[ArxivRejection.Unparseable]}.");
        foreach (var e in entries)
        {
            Console.WriteLine($"  {e.VersionedId}  v1 {e.Published:yyyy-MM-dd}  this version {e.Updated:yyyy-MM-dd}  {string.Join(" ", e.Categories.Take(4))}" +
                (args.Contains("--show-titles") ? "  " + e.Title : ""));
        }

        return 0;
    }

    case "fetch":
    {
        var ids = RequireOption("--ids").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > MaxDocumentsPerRun || ids.Any(id => !SourceRequestPolicy.IsVersionedId(id)))
        {
            Console.Error.WriteLine($"Give 1 to {MaxDocumentsPerRun} versioned arXiv ids (e.g. 2301.01234v2); nothing was requested.");
            return 1;
        }

        if (!Confirm($"arXiv: {ids.Count} record(s) and, where robots.txt allows, their PDFs (at least {configuration.MinIntervalSeconds} s between requests)"))
        {
            return 1;
        }

        var (fetcher, robots) = Online();
        var arxiv = new ArxivClient(fetcher, configuration);
        var ingestor = new DocumentIngestor(arxiv, new NpgsqlSourceStore(RequireConnectionString()), configuration);
        var failures = 0;
        foreach (var id in ids)
        {
            try
            {
                var (entry, feed) = await arxiv.GetVersionAsync(id, CancellationToken.None);
                if (entry is null)
                {
                    Console.WriteLine($"{id}: not returned as a usable pre-cutoff record in an allowed category; skipped.");
                    failures++;
                    continue;
                }

                var ingested = await ingestor.IngestAsync(entry, feed, CancellationToken.None);
                var d = ingested.Document;
                Console.WriteLine($"{id}: document {d.Id} ({d.TextKind}) content {d.ContentSha256[..12]} text {d.TextSha256[..12]} {d.Bytes} bytes; " +
                    $"v1 {d.PublishedAt:yyyy-MM-dd}, version {d.VersionAt:yyyy-MM-dd}; hidden {d.HiddenCharacters}, removed {d.RemovedCharacters}, truncated {d.Truncated}" +
                    (ingested.PdfSkippedReason is { } reason ? $"; PDF not used: {reason}" : ""));
            }
            catch (Exception e) when (e is SourceUnavailableException or SourcePolicyViolationException or SourceTooLargeException or UnreadableDocumentException)
            {
                Console.WriteLine($"{id}: {e.GetType().Name}: {e.Message}");
                failures++;
            }
        }

        Console.WriteLine("robots.txt: " + string.Join(", ", robots.Observed.Select(o => $"{o.Key} → {(o.Value == 0 ? "unreachable" : o.Value.ToString(CultureInfo.InvariantCulture))}")));
        return failures == 0 ? 0 : 2;
    }

    case "extract":
    {
        var connection = RequireConnectionString();
        var ai = AiConfiguration.LoadDefault();
        var registry = new QualificationRegistry(new NpgsqlQualificationStore(connection));
        if (!await new WebResearchGate(ai, registry).IsOpenAsync(CancellationToken.None))
        {
            Console.Error.WriteLine(WebResearchGate.ClosedNote + "; no call was made.");
            return 3;
        }

        var store = new NpgsqlSourceStore(connection);
        var documents = (await store.DocumentsAsync(CancellationToken.None))
            .Where(d => Option("--document") is not { } only || d.Id == long.Parse(only, CultureInfo.InvariantCulture)).ToList();
        if (documents.Count == 0 || !Confirm($"FindingExtraction on {documents.Count} document(s) against Gemini (about one call each)"))
        {
            return 1;
        }

        if (ApiKey() is not { } apiKey)
        {
            return 1;
        }

        var killSwitch = new AiKillSwitch(new NpgsqlKillSwitchStore(connection), new NpgsqlAiAudit(connection), time);
        using var provider = new GeminiProvider(apiKey, ai.Prices, GeminiGuardHandler.CreateNetworkHandler());
        var guard = new AiCallGuard(provider, ai, new NpgsqlCostLedger(connection), killSwitch, time);
        var router = new ModelRouter(ai, guard, new NpgsqlDecisionTraceStore(connection), registry, time);
        var extractor = new FindingExtractor(router, store);
        var job = new JobContext($"extract-{time.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}", JobKind.ResearchCycle);
        var fetcher = new SourceFetcher(policy, cache, null, null, time);
        foreach (var document in documents)
        {
            if (await Reextract(document, fetcher) is not { } text)
            {
                Console.WriteLine($"document {document.Id}: cache entry missing, expired or changed; skipped.");
                continue;
            }

            var result = await extractor.ExtractAsync(new IngestedDocument(document, text, null), job, CancellationToken.None);
            Console.WriteLine($"document {document.Id}: {result.Task.Outcome}{(result.Task.Degraded ? " DEGRADED" : "")}; findings verified {result.Findings.Count(f => f.Verified)}, " +
                $"rejected {result.Findings.Count(f => !f.Verified)} ({string.Join(", ", result.Findings.Where(f => !f.Verified).GroupBy(f => f.RejectionReason).Select(g => $"{g.Key} {g.Count()}"))}).");
        }

        return 0;
    }

    case "docs":
    {
        foreach (var d in await new NpgsqlSourceStore(RequireConnectionString()).DocumentsAsync(CancellationToken.None))
        {
            Console.WriteLine($"{d.Id,4} {d.Kind}/{d.TextKind} {(d.ArxivId is null ? d.CanonicalUrl : $"{d.ArxivId}v{d.ArxivVersion}")} fetched {d.FetchedAt:yyyy-MM-dd HH:mm}Z " +
                $"content {d.ContentSha256[..12]} text {d.TextSha256[..12]} hidden {d.HiddenCharacters} removed {d.RemovedCharacters}{(d.Truncated ? " truncated" : "")}");
        }

        return 0;
    }

    case "provenance-report":
    {
        var documents = await new NpgsqlSourceStore(RequireConnectionString()).DocumentsAsync(CancellationToken.None);
        var fetcher = new SourceFetcher(policy, cache, null, null, time);
        var rows = new List<object>();
        var problems = 0;
        foreach (var d in documents)
        {
            var issues = new List<string>();
            if (string.IsNullOrEmpty(d.Url) || string.IsNullOrEmpty(d.CanonicalUrl) || d.ContentSha256.Length != 64 || d.TextSha256.Length != 64)
            {
                issues.Add("missing field");
            }

            if (!d.IsBeforeCutoff)
            {
                issues.Add("version on or after the cutoff");
            }

            var text = await Reextract(d, fetcher);
            if (text is null)
            {
                issues.Add("cache entry missing, expired, changed, or text hash differs");
            }

            problems += issues.Count > 0 ? 1 : 0;
            rows.Add(new
            {
                d.Id, d.Kind, d.TextKind, d.Url, d.CanonicalUrl, d.ArxivId, d.ArxivVersion, d.PublishedAt, d.VersionAt, d.FetchedAt, d.ContentSha256, d.TextSha256,
                d.Bytes, d.HiddenCharacters, d.RemovedCharacters, d.Truncated, d.ExtractorVersion, d.NormalizerVersion, Issues = issues,
            });
            Console.WriteLine($"{d.Id,4} {(issues.Count == 0 ? "ok" : string.Join("; ", issues))}");
        }

        var path = Path.Combine(cache.ReportsDirectory, $"provenance-{time.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { documents = documents.Count, problems, cacheCorrupted = cache.Corrupted, rows },
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"{documents.Count} document(s), {problems} with a problem. Report: {path}");
        return problems == 0 ? 0 : 2;
    }

    default:
        Console.Error.WriteLine("Usage: search --allow-network --terms <a,b> [--field abs] [--categories ...] [--max n] [--show-titles] | " +
            "fetch --allow-network --ids <id vN,...> | extract --allow-network [--document <id>] | docs | provenance-report");
        return 1;
}

// The same text the Extractor saw: the cached body, extracted and normalised again; its hash must equal the recorded one.
async Task<NormalizedText?> Reextract(SourceDocument document, SourceFetcher offline)
{
    try
    {
        var fetched = await offline.FetchAsync(new Uri(document.Url), SourceFetcher.ClassOf(document.Kind), CancellationToken.None);
        if (fetched.ContentSha256 != document.ContentSha256)
        {
            return null;
        }

        string raw;
        if (document.TextKind == DocumentText.Pdf)
        {
            raw = PdfTextExtractor.Extract(fetched.Body, configuration.MaxPdfPages, configuration.MaxExtractedCharacters).Text;
        }
        else if (document.TextKind == DocumentText.Html)
        {
            raw = HtmlTextExtractor.Extract(Encoding.UTF8.GetString(fetched.Body), configuration.MaxExtractedCharacters).Text;
        }
        else
        {
            var (entries, _) = ArxivFeed.Parse(fetched.Body, configuration.ArxivCategories);
            raw = entries.SingleOrDefault(e => e.Id == document.ArxivId && e.Version == document.ArxivVersion) is { } e ? e.Title + "\n\n" + e.Summary : "";
        }

        var text = TextNormalizer.Normalize(raw);
        return SourceCache.Sha256(Encoding.UTF8.GetBytes(text.Text)) == document.TextSha256 ? text : null;
    }
    catch (Exception e) when (e is SourceUnavailableException or SourcePolicyViolationException or UnreadableDocumentException)
    {
        return null;
    }
}

(SourceFetcher Fetcher, RobotsPolicy Robots) Online()
{
    var limiter = new DomainRateLimiter(TimeSpan.FromSeconds(configuration.MinIntervalSeconds), time);
    var guard = new SourceGuardHandler(policy, limiter, SourceGuardHandler.CreateNetworkHandler());
    var http = new SourceHttp(guard, time);
    var robots = new RobotsPolicy(http, configuration, time);
    return (new SourceFetcher(policy, cache, http, robots, time), robots);
}

bool Confirm(string what)
{
    if (!args.Contains("--allow-network"))
    {
        Console.Error.WriteLine("Network access requires --allow-network (M7 Gate C; the user's run).");
        return false;
    }

    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.Error.WriteLine("Network access requires an interactive terminal; refused.");
        return false;
    }

    Console.Write($"{what}. Type {ConfirmationWord} to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), ConfirmationWord, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Not confirmed; nothing was requested.");
        return false;
    }

    return true;
}

string? ApiKey()
{
    var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = [],
        EnvironmentName = Environments.Development, // loads the dotnet user-secrets shared with AiCli
        ApplicationName = typeof(Program).Assembly.GetName().Name,
    });
    var key = host.Configuration["Gemini:ApiKey"];
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("No Gemini API key: set Gemini__ApiKey in your own session or the AiCli user-secrets.");
        return null;
    }

    return key;
}

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

string RequireOption(string name) => Option(name) is { Length: > 0 } value ? value : throw new ArgumentException($"{name} is required.");

static string RequireConnectionString() => Environment.GetEnvironmentVariable("ConnectionStrings__Ops")
    ?? throw new InvalidOperationException("ConnectionStrings__Ops is not set (scripts/m7-sources.ps1 sets it).");

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PortfolioIntelligence.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run from inside the repository.");
}
