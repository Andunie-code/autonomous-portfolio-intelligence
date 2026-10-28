using System.Globalization;
using System.Text.RegularExpressions;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>Raised when a request or response violates the source access policy. Nothing is sent, stored or used; never retried.</summary>
public sealed class SourcePolicyViolationException(string message) : Exception(message);

/// <summary>An allowlisted request target.</summary>
public sealed record SourceTarget(SourceKind Kind, AllowedSource Source);

/// <summary>
/// Validates every source request on its own (m7-protocol §2.1–§2.2): GET over plain HTTPS on the default port, no
/// user info or fragment, an allowlisted host, and the fixed path and query rule of the source kind. arXiv identifiers
/// must carry an explicit version (so no redirect is ever needed and the content is pinned). The arXiv API query must
/// carry the pre-cutoff submission filter (U-M7-2). Anything else is rejected before it reaches the network.
/// </summary>
public sealed partial class SourceRequestPolicy(SourceConfiguration configuration)
{
    /// <summary>Server-side submission filter that every API query carries (the binding check is on the client, §2.5).</summary>
    public const string SubmittedBeforeCutoff = "submittedDate:[000001010000 TO 202512312359]";

    private static readonly HashSet<string> ApiParameters = new(StringComparer.Ordinal) { "search_query", "id_list", "start", "max_results", "sortBy", "sortOrder" };

    public SourceConfiguration Configuration { get; } = configuration;

    public SourceTarget Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Violation(uri, "only plain HTTPS requests on the default port are allowed");
        }

        var host = uri.Host.ToLowerInvariant();
        if (!Configuration.IsHostAllowed(host))
        {
            throw Violation(uri, "host is not on the allowlist");
        }

        var path = uri.AbsolutePath;
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal) || path.Contains('%'))
        {
            throw Violation(uri, "path traversal or encoded characters are not allowed");
        }

        if (path == "/robots.txt" && string.IsNullOrEmpty(uri.Query))
        {
            return new SourceTarget(SourceKind.Robots, Configuration.Sources.First(s => s.Host == host));
        }

        foreach (var source in Configuration.Sources.Where(s => s.Host == host))
        {
            switch (source.Kind)
            {
                case SourceKind.ArxivApi when path == "/api/query":
                    ValidateApiQuery(uri);
                    return new SourceTarget(source.Kind, source);
                case SourceKind.ArxivAbstract when string.IsNullOrEmpty(uri.Query) && path.StartsWith("/abs/", StringComparison.Ordinal) && IsVersionedId(path[5..]):
                case SourceKind.ArxivPdf when string.IsNullOrEmpty(uri.Query) && path.StartsWith("/pdf/", StringComparison.Ordinal) && IsVersionedId(path[5..]):
                case SourceKind.BinanceDocs when string.IsNullOrEmpty(uri.Query) && DocsPath().IsMatch(path):
                    return new SourceTarget(source.Kind, source);
            }
        }

        throw Violation(uri, "path or query is not allowed for this host");
    }

    /// <summary>A versioned arXiv identifier: new style <c>2301.01234v2</c> or old style <c>q-fin/0601001v1</c>.</summary>
    public static bool IsVersionedId(string id) => VersionedId().IsMatch(id);

    private void ValidateApiQuery(Uri uri)
    {
        var parameters = ParseQuery(uri);
        if (parameters.Count == 0 || parameters.Keys.Any(k => !ApiParameters.Contains(k)))
        {
            throw Violation(uri, "only the documented API parameters are allowed");
        }

        if (!parameters.TryGetValue("search_query", out var search) || !search.Contains(SubmittedBeforeCutoff, StringComparison.Ordinal)
            || !SearchQuery().IsMatch(search))
        {
            throw Violation(uri, "the API query must be built by ArxivQuery and carry the pre-cutoff submission filter");
        }

        if (parameters.TryGetValue("id_list", out var ids) && ids.Split(',').Any(id => !IsVersionedId(id)))
        {
            throw Violation(uri, "id_list may contain only versioned arXiv identifiers");
        }

        if (!Within(parameters, "max_results", 1, Configuration.MaxApiResults) || !Within(parameters, "start", 0, Configuration.MaxApiStart))
        {
            throw Violation(uri, "start or max_results is outside the limits");
        }

        if (parameters.TryGetValue("sortBy", out var sortBy) && sortBy is not ("relevance" or "lastUpdatedDate" or "submittedDate"))
        {
            throw Violation(uri, "unknown sortBy");
        }

        if (parameters.TryGetValue("sortOrder", out var sortOrder) && sortOrder is not ("ascending" or "descending"))
        {
            throw Violation(uri, "unknown sortOrder");
        }
    }

    private static bool Within(Dictionary<string, string> parameters, string name, int min, int max) =>
        !parameters.TryGetValue(name, out var text)
        || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max);

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(index < 0 ? pair : pair[..index]);
            var value = index < 0 ? "" : Uri.UnescapeDataString(pair[(index + 1)..].Replace('+', ' '));
            if (!result.TryAdd(name, value))
            {
                throw Violation(uri, $"duplicate query parameter '{name}'");
            }
        }

        return result;
    }

    private static SourcePolicyViolationException Violation(Uri uri, string reason) => new($"Source request rejected ({reason}): {uri}");

    [GeneratedRegex(@"^(\d{4}\.\d{4,5}|[a-z\-]+(\.[A-Z]{2})?/\d{7})v[1-9]\d{0,2}$")]
    private static partial Regex VersionedId();

    [GeneratedRegex(@"^[A-Za-z0-9 :.\-()\[\]""*]{1,600}$")]
    private static partial Regex SearchQuery();

    [GeneratedRegex(@"^/docs/[a-z0-9\-/]{1,300}$")]
    private static partial Regex DocsPath();
}
