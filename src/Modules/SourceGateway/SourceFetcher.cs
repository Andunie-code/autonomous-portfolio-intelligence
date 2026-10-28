namespace PortfolioIntelligence.SourceGateway;

/// <summary>A fetched (or cached) source body with its provenance fields.</summary>
public sealed record FetchedSource(Uri Uri, SourceTarget Target, byte[] Body, string? ContentType, string ContentSha256, DateTimeOffset FetchedAt, bool FromCache);

/// <summary>Why a source was not fetched (the run continues with the next source; risk-model §4).</summary>
public sealed class SourceUnavailableException(string message) : Exception(message);

/// <summary>
/// Cache first, then robots.txt, then the network (m7-protocol §2). Without a network client (offline) only fresh cache
/// entries are used; an expired entry is never used. Online, API responses are reused for a day (S-X3: the same query at
/// most once a day); everything else, and every offline read, uses the cache TTL. Only 2xx bodies of the expected content type are stored.
/// </summary>
public sealed class SourceFetcher(SourceRequestPolicy policy, SourceCache cache, SourceHttp? http, RobotsPolicy? robots, TimeProvider time)
{
    private static readonly Dictionary<ContentClass, string[]> MediaTypes = new()
    {
        [ContentClass.Atom] = ["application/atom+xml", "application/xml", "text/xml"],
        [ContentClass.Pdf] = ["application/pdf"],
        [ContentClass.Html] = ["text/html"],
        [ContentClass.Robots] = ["text/plain"],
    };

    public bool Online => http is not null && robots is not null;

    public async Task<FetchedSource> FetchAsync(Uri uri, ContentClass content, CancellationToken ct)
    {
        var target = policy.Validate(uri);
        if (ClassOf(target.Kind) != content)
        {
            throw new SourcePolicyViolationException($"{target.Kind} is not fetched as {content}.");
        }

        // Online, an API answer older than a day is fetched again (S-X3); offline re-checks read any entry within the cache TTL.
        var ttl = content == ContentClass.Atom && Online ? TimeSpan.FromDays(1) : TimeSpan.FromDays(policy.Configuration.CacheTtlDays);
        var now = time.GetUtcNow();
        if (await cache.GetAsync(uri, ttl, now, ct) is { } cached)
        {
            return new FetchedSource(uri, target, cached.Body, cached.Entry.ContentType, cached.Entry.ContentSha256, cached.Entry.FetchedAt, true);
        }

        if (!Online)
        {
            throw new SourceUnavailableException($"Offline and no fresh cache entry for {uri}; skipped.");
        }

        if (!await robots!.IsAllowedAsync(uri, target, ct))
        {
            throw new SourceUnavailableException($"robots.txt of {uri.Host} does not allow {uri.AbsolutePath} (or could not be read); skipped.");
        }

        var fetch = await http!.GetAsync(uri, content, ct);
        if (!SourceHttp.IsSuccess(fetch.Status))
        {
            throw new SourceUnavailableException($"HTTP {fetch.Status} from {uri.Host}; skipped.");
        }

        if (fetch.ContentType is null || !MediaTypes[content].Contains(fetch.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new SourcePolicyViolationException($"Unexpected content type '{fetch.ContentType}' for {content} from {uri.Host}; not stored.");
        }

        var fetchedAt = time.GetUtcNow();
        var entry = await cache.PutAsync(uri, fetch.Status, fetch.ContentType, fetch.Body, fetchedAt, ct);
        return new FetchedSource(uri, target, fetch.Body, fetch.ContentType, entry.ContentSha256, fetchedAt, false);
    }

    public static ContentClass ClassOf(SourceKind kind) => kind switch
    {
        SourceKind.ArxivApi => ContentClass.Atom,
        SourceKind.ArxivPdf => ContentClass.Pdf,
        SourceKind.ArxivAbstract or SourceKind.BinanceDocs => ContentClass.Html,
        _ => ContentClass.Robots,
    };
}
