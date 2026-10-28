using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>What an allowlisted URL is for; each kind has a fixed path and query rule in <see cref="SourceRequestPolicy"/>.</summary>
public enum SourceKind
{
    ArxivApi,
    ArxivAbstract,
    ArxivPdf,
    BinanceDocs,
    Robots,
}

/// <summary>Trust tier (architecture §8): sets priority only, never evidential weight. T2 sources are added only by the user.</summary>
public enum TrustTier
{
    T1,
    T2,
}

/// <summary>
/// How robots.txt applies to a source (U-M7-6): the documented arXiv API follows its terms of use (S-X1) instead of
/// robots.txt (S-X5); every other source follows robots.txt.
/// </summary>
public enum RobotsMode
{
    Binding,
    ApiTerms,
}

/// <summary>Content classes with their own size cap (m7-protocol A-4).</summary>
public enum ContentClass
{
    Atom,
    Pdf,
    Html,
    Robots,
}

public sealed record AllowedSource(SourceKind Kind, string Host, TrustTier Tier, RobotsMode Robots);

/// <summary>
/// The allowlist and access limits (embedded <c>Config/sources.v1.json</c>, m7-protocol §2). Only the user changes it;
/// a change is a new configuration version recorded in the CHANGELOG.
/// </summary>
public sealed record SourceConfiguration(
    string Version,
    string UserAgent,
    int MinIntervalSeconds,
    int RequestTimeoutSeconds,
    int MaxRetries,
    int MaxRetryAfterSeconds,
    IReadOnlyDictionary<ContentClass, long> MaxBytes,
    int MaxExtractedCharacters,
    int MaxPdfPages,
    int CacheTtlDays,
    int RobotsTtlHours,
    int MaxApiResults,
    int MaxApiStart,
    IReadOnlyList<string> ArxivCategories,
    IReadOnlyList<AllowedSource> Sources)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static SourceConfiguration LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PortfolioIntelligence.SourceGateway.Config.sources.v1.json")
            ?? throw new InvalidOperationException("Embedded sources.v1.json not found.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static SourceConfiguration Parse(string json)
    {
        var configuration = JsonSerializer.Deserialize<SourceConfiguration>(json, Json) ?? throw new InvalidOperationException("Empty source configuration.");
        configuration.Check();
        return configuration;
    }

    public AllowedSource? Find(SourceKind kind) => Sources.FirstOrDefault(s => s.Kind == kind);

    public bool IsHostAllowed(string host) => Sources.Any(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));

    private void Check()
    {
        // The arXiv terms (S-X1) require at least three seconds between requests; nothing may relax that.
        if (MinIntervalSeconds < 3)
        {
            throw new InvalidOperationException("minIntervalSeconds must be at least 3 (arXiv API terms, S-X1).");
        }

        if (MaxRetries is < 0 or > 2 || MaxRetryAfterSeconds is <= 0 or > 60 || RequestTimeoutSeconds is <= 0 or > 120)
        {
            throw new InvalidOperationException("Retry and timeout limits are outside m7-protocol §2.2.");
        }

        foreach (var content in Enum.GetValues<ContentClass>())
        {
            if (!MaxBytes.TryGetValue(content, out var max) || max <= 0)
            {
                throw new InvalidOperationException($"maxBytes.{content} is missing.");
            }
        }

        if (Sources.Count == 0 || Sources.Any(s => s.Kind == SourceKind.Robots || string.IsNullOrWhiteSpace(s.Host) || s.Host.Contains('/')))
        {
            throw new InvalidOperationException("Invalid allowlist entry.");
        }

        if (Sources.Any(s => s.Robots == RobotsMode.ApiTerms && s.Kind != SourceKind.ArxivApi))
        {
            // The robots.txt exemption is limited to the documented arXiv API (U-M7-6).
            throw new InvalidOperationException("Only the arXiv API may follow its terms instead of robots.txt.");
        }

        if (MaxApiResults is <= 0 or > 50 || MaxApiStart is < 0 or > 1000 || ArxivCategories.Count == 0)
        {
            throw new InvalidOperationException("arXiv query limits are outside m7-protocol §2.1.");
        }
    }
}
