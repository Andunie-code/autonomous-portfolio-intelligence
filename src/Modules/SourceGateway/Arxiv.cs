using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>
/// Builds arXiv API queries from structured input only (m7-protocol §2.5): up to six terms of letters, digits, spaces and
/// hyphens in a <c>ti</c>, <c>abs</c> or <c>all</c> field, allowlisted categories joined by OR, and always the pre-cutoff
/// submission filter. Free text never reaches the query.
/// </summary>
public static partial class ArxivQuery
{
    public const string ApiBase = "https://export.arxiv.org/api/query";

    /// <summary>The q-fin subject classes; <c>q-fin.*</c> in the allowlist expands to these (S-X3 has no wildcard).</summary>
    public static readonly IReadOnlyList<string> QuantitativeFinance = ["q-fin.CP", "q-fin.EC", "q-fin.GN", "q-fin.MF", "q-fin.PM", "q-fin.PR", "q-fin.RM", "q-fin.ST", "q-fin.TR"];

    public static string SearchQuery(IReadOnlyList<string> terms, string field, IReadOnlyList<string> categories, SourceConfiguration configuration)
    {
        if (field is not ("ti" or "abs" or "all"))
        {
            throw new ArgumentException("Field must be ti, abs or all.", nameof(field));
        }

        if (terms.Count is 0 or > 6 || terms.Any(t => !Term().IsMatch(t)))
        {
            throw new ArgumentException("One to six terms of letters, digits, spaces and hyphens (≤ 40 characters each).", nameof(terms));
        }

        var allowed = Expand(configuration.ArxivCategories);
        var chosen = categories.Count == 0 ? allowed : Expand(categories);
        if (chosen.Any(c => !allowed.Contains(c, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Category is not on the allowlist.", nameof(categories));
        }

        var termPart = string.Join(" AND ", terms.Select(t => $"{field}:\"{t.Trim()}\""));
        var categoryPart = string.Join(" OR ", chosen.Select(c => "cat:" + c));
        return $"({termPart}) AND ({categoryPart}) AND {SourceRequestPolicy.SubmittedBeforeCutoff}";
    }

    public static Uri Search(string searchQuery, int start, int maxResults) =>
        new($"{ApiBase}?search_query={Encode(searchQuery)}&start={start.ToString(CultureInfo.InvariantCulture)}" +
            $"&max_results={maxResults.ToString(CultureInfo.InvariantCulture)}&sortBy=submittedDate&sortOrder=descending");

    /// <summary>One versioned record, still through the server-side submission filter.</summary>
    public static Uri Version(string versionedId, SourceConfiguration configuration)
    {
        if (!SourceRequestPolicy.IsVersionedId(versionedId))
        {
            throw new ArgumentException("A versioned arXiv identifier is required (e.g. 2301.01234v2).", nameof(versionedId));
        }

        var categories = string.Join(" OR ", Expand(configuration.ArxivCategories).Select(c => "cat:" + c));
        var search = $"({categories}) AND {SourceRequestPolicy.SubmittedBeforeCutoff}";
        return new Uri($"{ApiBase}?search_query={Encode(search)}&id_list={versionedId}&max_results=1");
    }

    /// <summary>
    /// The API manual's encoding (S-X3): spaces as <c>+</c>, field colons literal, parentheses <c>%28</c>/<c>%29</c>, quotes
    /// <c>%22</c>, brackets <c>%5B</c>/<c>%5D</c>. The request policy decodes it back to the same query.
    /// </summary>
    public static string Encode(string query) =>
        Uri.EscapeDataString(query).Replace("%20", "+", StringComparison.Ordinal).Replace("%3A", ":", StringComparison.Ordinal);

    public static IReadOnlyList<string> Expand(IEnumerable<string> categories) =>
        categories.SelectMany(c => c == "q-fin.*" ? QuantitativeFinance : [c]).Distinct(StringComparer.Ordinal).ToList();

    public static bool IsAllowedCategory(string category, IReadOnlyList<string> allowlist) =>
        Expand(allowlist).Contains(category, StringComparer.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 \-]{0,39}$")]
    private static partial Regex Term();
}

/// <summary>One arXiv record: the retrieved version and its dates (S-X4: <c>published</c> = v1, <c>updated</c> = this version).</summary>
public sealed record ArxivEntry(
    string Id,
    int Version,
    DateTimeOffset Published,
    DateTimeOffset Updated,
    string Title,
    string Summary,
    IReadOnlyList<string> Categories)
{
    public string VersionedId => $"{Id}v{Version}";

    public Uri AbstractUri => new($"https://arxiv.org/abs/{VersionedId}");

    public Uri PdfUri => new($"https://arxiv.org/pdf/{VersionedId}");

    /// <summary>U-M7-2: both the first version and this version were submitted before the research cutoff.</summary>
    public bool IsBeforeCutoff => Published < ResearchCutoff.C && Updated < ResearchCutoff.C;
}

/// <summary>Why a record of a feed was dropped (counted in the provenance report).</summary>
public enum ArxivRejection
{
    Unparseable,
    AfterCutoff,
    CategoryNotAllowed,
}

/// <summary>
/// Parses an arXiv Atom feed with DTD processing prohibited and no resolver (m7-protocol §3). Only known fields are read.
/// Records submitted on or after the cutoff and records without an allowlisted category are dropped and counted.
/// </summary>
public static partial class ArxivFeed
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public static (IReadOnlyList<ArxivEntry> Entries, IReadOnlyDictionary<ArxivRejection, int> Rejected) Parse(byte[] body, IReadOnlyList<string> allowedCategories)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 };
        XDocument document;
        try
        {
            using var stream = new MemoryStream(body);
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new SourcePolicyViolationException("arXiv response is not a valid Atom document: " + e.Message);
        }

        if (document.Root?.Name != Atom + "feed")
        {
            throw new SourcePolicyViolationException("arXiv response is not an Atom feed.");
        }

        var entries = new List<ArxivEntry>();
        var rejected = Enum.GetValues<ArxivRejection>().ToDictionary(r => r, _ => 0);
        foreach (var element in document.Root.Elements(Atom + "entry"))
        {
            var entry = ParseEntry(element);
            if (entry is null)
            {
                rejected[ArxivRejection.Unparseable]++;
            }
            else if (!entry.IsBeforeCutoff)
            {
                rejected[ArxivRejection.AfterCutoff]++;
            }
            else if (!entry.Categories.Any(c => ArxivQuery.IsAllowedCategory(c, allowedCategories)))
            {
                rejected[ArxivRejection.CategoryNotAllowed]++;
            }
            else
            {
                entries.Add(entry);
            }
        }

        return (entries, rejected);
    }

    private static ArxivEntry? ParseEntry(XElement element)
    {
        var id = (string?)element.Element(Atom + "id");
        var match = id is null ? Match.Empty : AbsId().Match(id);
        if (!match.Success
            || !DateTimeOffset.TryParse((string?)element.Element(Atom + "published"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published)
            || !DateTimeOffset.TryParse((string?)element.Element(Atom + "updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated))
        {
            return null;
        }

        var categories = element.Elements(Atom + "category").Select(c => (string?)c.Attribute("term")).OfType<string>().ToList();
        return new ArxivEntry(match.Groups["id"].Value, int.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture),
            published.ToUniversalTime(), updated.ToUniversalTime(), Clean((string?)element.Element(Atom + "title")),
            Clean((string?)element.Element(Atom + "summary")), categories);
    }

    private static string Clean(string? text) => Whitespace().Replace(text ?? "", " ").Trim();

    [GeneratedRegex(@"^https?://arxiv\.org/abs/(?<id>\d{4}\.\d{4,5}|[a-z\-]+(\.[A-Z]{2})?/\d{7})v(?<v>[1-9]\d{0,2})$")]
    private static partial Regex AbsId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Search, one versioned record, and its PDF, all through <see cref="SourceFetcher"/>.</summary>
public sealed class ArxivClient(SourceFetcher fetcher, SourceConfiguration configuration)
{
    public async Task<(IReadOnlyList<ArxivEntry> Entries, IReadOnlyDictionary<ArxivRejection, int> Rejected, FetchedSource Source)> SearchAsync(
        IReadOnlyList<string> terms, string field, IReadOnlyList<string> categories, int maxResults, CancellationToken ct)
    {
        var uri = ArxivQuery.Search(ArxivQuery.SearchQuery(terms, field, categories, configuration), 0, Math.Clamp(maxResults, 1, configuration.MaxApiResults));
        var source = await fetcher.FetchAsync(uri, ContentClass.Atom, ct);
        var (entries, rejected) = ArxivFeed.Parse(source.Body, configuration.ArxivCategories);
        return (entries, rejected, source);
    }

    public async Task<(ArxivEntry? Entry, FetchedSource Source)> GetVersionAsync(string versionedId, CancellationToken ct)
    {
        var source = await fetcher.FetchAsync(ArxivQuery.Version(versionedId, configuration), ContentClass.Atom, ct);
        var (entries, _) = ArxivFeed.Parse(source.Body, configuration.ArxivCategories);
        // The API must return exactly the requested version; anything else is not used.
        return (entries.SingleOrDefault(e => e.VersionedId == versionedId), source);
    }

    /// <summary>The PDF of exactly this version, only for a record that passed the cutoff and category checks.</summary>
    public Task<FetchedSource> GetPdfAsync(ArxivEntry entry, CancellationToken ct)
    {
        if (!entry.IsBeforeCutoff)
        {
            throw new SourcePolicyViolationException($"{entry.VersionedId} was submitted on or after the research cutoff; not fetched (U-M7-2).");
        }

        return fetcher.FetchAsync(entry.PdfUri, ContentClass.Pdf, ct);
    }
}
