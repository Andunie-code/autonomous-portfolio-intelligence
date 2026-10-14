using System.Globalization;
using System.Text.RegularExpressions;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData.Archive;

/// <summary>Raised when a request or response violates the archive access policy. Nothing is sent or used.</summary>
public sealed class ArchivePolicyViolationException(string message) : Exception(message);

/// <summary>
/// Validates every archive request independently of the file planner (docs/m1-data-policy.md §3):
/// HTTPS to an allow-listed host only; either a monthly kline file (zip or checksum) whose candles all close at or
/// before C, or a listing query whose prefix is the kline root or a year prefix not later than
/// <see cref="MaxListingYear"/>. Anything else is rejected before it reaches the network.
/// </summary>
public sealed partial class ArchiveRequestPolicy
{
    public const string DefaultHost = "data.binance.vision";

    private static readonly HashSet<string> ListingParameters = new(StringComparer.Ordinal) { "delimiter", "prefix", "marker", "max-keys" };

    public ArchiveRequestPolicy(IReadOnlySet<string> allowedHosts, int maxListingYear)
    {
        if (allowedHosts.Count == 0)
        {
            throw new ArgumentException("At least one host must be allowed.", nameof(allowedHosts));
        }

        if (maxListingYear >= ResearchCutoff.C.Year)
        {
            throw new ArgumentOutOfRangeException(nameof(maxListingYear), "Listing years must end before the research cutoff year.");
        }

        AllowedHosts = allowedHosts;
        MaxListingYear = maxListingYear;
    }

    public IReadOnlySet<string> AllowedHosts { get; }

    public int MaxListingYear { get; }

    public static ArchiveRequestPolicy CorePhase() => new(new HashSet<string> { DefaultHost }, 2021);

    public static ArchiveRequestPolicy E1Phase() => new(new HashSet<string> { DefaultHost }, 2024);

    public void Validate(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Violation(uri, "only plain HTTPS requests on the default port are allowed");
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            throw Violation(uri, $"host '{uri.Host}' is not allow-listed");
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (string.IsNullOrEmpty(uri.Query) && path.Length > 0)
        {
            ValidateFile(uri, path);
            return;
        }

        ValidateListing(uri, path);
    }

    /// <summary>Checks a key or prefix returned by a listing response against the requested prefix.</summary>
    public void ValidateListedKey(string requestedPrefix, string returned)
    {
        if (!returned.StartsWith(requestedPrefix, StringComparison.Ordinal))
        {
            throw new ArchivePolicyViolationException($"Listing returned an entry outside the requested prefix '{requestedPrefix}'.");
        }

        var year = YearInKey().Match(returned);
        if (year.Success && int.Parse(year.Groups["year"].Value, CultureInfo.InvariantCulture) > MaxListingYear)
        {
            throw new ArchivePolicyViolationException("Listing returned a file name later than the permitted listing year.");
        }
    }

    private static void ValidateFile(Uri uri, string path)
    {
        var key = ArchiveFileKey.TryParsePath(path)
            ?? throw Violation(uri, "path is not a monthly spot kline file");
        if (key.MonthEnd > ResearchCutoff.C || !MonthlyFilePlanner.IsEntirelyBeforeCutoff(key))
        {
            throw Violation(uri, "file covers candles closing after the research cutoff");
        }
    }

    private void ValidateListing(Uri uri, string path)
    {
        if (path.Length > 0)
        {
            throw Violation(uri, "listing requests must target the archive root");
        }

        var parameters = ParseQuery(uri.Query);
        if (parameters.Keys.Any(k => !ListingParameters.Contains(k)) || !parameters.TryGetValue("prefix", out var prefix))
        {
            throw Violation(uri, "listing query has unexpected or missing parameters");
        }

        if (parameters.TryGetValue("max-keys", out var maxKeys)
            && (!int.TryParse(maxKeys, NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max is < 1 or > 1000))
        {
            throw Violation(uri, "max-keys must be between 1 and 1000");
        }

        if (prefix == ArchiveFileKey.MonthlyKlinesRoot)
        {
            if (!parameters.TryGetValue("delimiter", out var delimiter) || delimiter != "/")
            {
                throw Violation(uri, "symbol-level listing must use the '/' delimiter");
            }
        }
        else
        {
            var match = YearPrefix().Match(prefix);
            if (!match.Success || match.Groups["symbol"].Value != match.Groups["fileSymbol"].Value)
            {
                throw Violation(uri, "listing prefix must be the kline root or a symbol's 1h year prefix");
            }

            if (int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) > MaxListingYear)
            {
                throw Violation(uri, "listing year is later than permitted for this phase");
            }
        }

        if (parameters.TryGetValue("marker", out var marker) && !marker.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Violation(uri, "listing marker must lie inside the prefix");
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var name = Uri.UnescapeDataString(pieces[0]);
            if (!result.TryAdd(name, pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty))
            {
                throw new ArchivePolicyViolationException($"Duplicate query parameter '{name}'.");
            }
        }

        return result;
    }

    private static ArchivePolicyViolationException Violation(Uri uri, string reason) =>
        new($"Archive request to {uri.Host}{uri.AbsolutePath} rejected: {reason}.");

    [GeneratedRegex(@"^data/spot/monthly/klines/(?<symbol>[A-Z0-9]{2,30})/1h/(?<fileSymbol>[A-Z0-9]{2,30})-1h-(?<year>\d{4})-$")]
    private static partial Regex YearPrefix();

    [GeneratedRegex(@"-(?<year>\d{4})-\d{2}\.zip")]
    private static partial Regex YearInKey();
}
