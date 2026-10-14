using System.Net;
using System.Text;
using System.Xml.Linq;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData.Archive;

public sealed record ArchiveFile(ArchiveFileKey Key, byte[] Zip, string Sha256);

/// <summary>
/// Reads the archive through <see cref="ArchiveGuardHandler"/> only. Listing uses the S3 ListObjects (v1) XML
/// format with marker pagination (assumed; to be confirmed on the first Gate 2 run). Raw listing responses are
/// never written to disk or logs; only the filtered entries are returned.
/// </summary>
public sealed class ArchiveClient
{
    private const int MaxPages = 1000;

    private readonly HttpClient _http;
    private readonly ArchiveRequestPolicy _policy;
    private readonly Uri _baseUri;

    public ArchiveClient(ArchiveGuardHandler guard, string host = ArchiveRequestPolicy.DefaultHost)
    {
        _http = new HttpClient(guard, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        _policy = guard.Policy;
        _baseUri = new Uri($"https://{host}/");
    }

    /// <summary>Downloads a monthly file and its checksum. Returns null when the zip does not exist (HTTP 404).</summary>
    public async Task<ArchiveFile?> DownloadAsync(ArchiveFileKey key, CancellationToken cancellationToken)
    {
        var zip = await GetBytesAsync(key.Path, cancellationToken);
        if (zip is null)
        {
            return null;
        }

        var checksum = await GetBytesAsync(key.ChecksumPath, cancellationToken);
        var sha = ChecksumVerifier.Verify(key, zip, checksum is null ? null : Encoding.ASCII.GetString(checksum));
        return new ArchiveFile(key, zip, sha);
    }

    /// <summary>Symbol directories directly below the monthly kline root (names only).</summary>
    public async Task<IReadOnlyList<string>> ListSymbolsAsync(CancellationToken cancellationToken)
    {
        var prefixes = await ListAsync(ArchiveFileKey.MonthlyKlinesRoot, delimiter: "/", maxKeys: 1000, onePage: false, cancellationToken);
        return prefixes.CommonPrefixes
            .Select(p => p[ArchiveFileKey.MonthlyKlinesRoot.Length..].TrimEnd('/'))
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Earliest monthly 1h file of <paramref name="symbol"/> in <paramref name="years"/> (ascending), using
    /// year prefixes and max-keys=1; years after the policy's listing year are never queried.
    /// </summary>
    public async Task<ArchiveFileKey?> FindFirstMonthAsync(string symbol, IEnumerable<int> years, CancellationToken cancellationToken)
    {
        foreach (var year in years.Order())
        {
            var prefix = $"{ArchiveFileKey.MonthlyKlinesRoot}{symbol}/1h/{symbol}-1h-{year:D4}-";
            var page = await ListAsync(prefix, delimiter: null, maxKeys: 1, onePage: true, cancellationToken);
            var first = page.Keys.Select(ArchiveFileKey.TryParsePath).FirstOrDefault(k => k is not null);
            if (first is not null)
            {
                return new ArchiveFileKey(first.Symbol, first.Interval, first.Year, first.Month);
            }
        }

        return null;
    }

    private async Task<(List<string> Keys, List<string> CommonPrefixes)> ListAsync(
        string prefix, string? delimiter, int maxKeys, bool onePage, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var common = new List<string>();
        string? marker = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var query = $"?prefix={Uri.EscapeDataString(prefix)}&max-keys={maxKeys}"
                + (delimiter is null ? string.Empty : $"&delimiter={Uri.EscapeDataString(delimiter)}")
                + (marker is null ? string.Empty : $"&marker={Uri.EscapeDataString(marker)}");
            using var response = await _http.GetAsync(new Uri(_baseUri, query), cancellationToken);
            EnsureSuccess(response, prefix);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = xml.Root ?? throw new ArchivePolicyViolationException("Listing response is empty.");
            var ns = root.Name.Namespace;

            var pageKeys = root.Elements(ns + "Contents").Select(c => (string?)c.Element(ns + "Key")).OfType<string>().ToList();
            var pagePrefixes = root.Elements(ns + "CommonPrefixes").Select(c => (string?)c.Element(ns + "Prefix")).OfType<string>().ToList();
            foreach (var entry in pageKeys.Concat(pagePrefixes))
            {
                _policy.ValidateListedKey(prefix, entry);
            }

            keys.AddRange(pageKeys);
            common.AddRange(pagePrefixes);

            var truncatedText = (string?)root.Element(ns + "IsTruncated");
            if (truncatedText is null || !bool.TryParse(truncatedText, out var truncated))
            {
                throw new ArchivePolicyViolationException("Listing response has no valid IsTruncated element; stopping.");
            }

            if (!truncated || onePage)
            {
                return (keys, common);
            }

            var next = (string?)root.Element(ns + "NextMarker") ?? pageKeys.Concat(pagePrefixes).Order(StringComparer.Ordinal).LastOrDefault();
            if (next is null || next == marker)
            {
                throw new ArchivePolicyViolationException("Listing is truncated but provides no new marker; stopping.");
            }

            marker = next;
        }

        throw new ArchivePolicyViolationException("Listing exceeded the page limit; stopping.");
    }

    private async Task<byte[]?> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(_baseUri, path), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, path);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Transient or unexpected failures stop the run; they are never recorded as missing data.
            throw new HttpRequestException($"Archive request for '{what}' failed with {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }
}
