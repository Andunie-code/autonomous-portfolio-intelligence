using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Web;
using PortfolioIntelligence.Kernel.MarketData;
using PortfolioIntelligence.MarketData.Archive;

namespace PortfolioIntelligence.MarketData.Tests.Support;

/// <summary>Synthetic monthly kline files built in memory; no real archive data is involved.</summary>
public static class SyntheticKlines
{
    public static DateTimeOffset Utc(int year, int month, int day, int hour = 0) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    /// <summary>CSV lines for the aligned candles of a monthly file that satisfy <paramref name="include"/>.</summary>
    public static List<string> Lines(ArchiveFileKey key, Func<DateTimeOffset, bool>? include = null, decimal volumeScale = 1m)
    {
        var lines = new List<string>();
        var length = key.Interval.FixedLength ?? throw new ArgumentException("fixed intervals only");
        var hoursPerCandle = (int)length.TotalHours;
        for (var open = key.MonthStart; open < key.MonthEnd; open += length)
        {
            if (include is not null && !include(open))
            {
                continue;
            }

            lines.Add(Line(key, open, Aggregate(open, hoursPerCandle, volumeScale)));
        }

        return lines;
    }

    public static string Line(ArchiveFileKey key, DateTimeOffset open, (decimal O, decimal H, decimal L, decimal C, decimal V, decimal Q, long N) c)
    {
        var closeTime = key.Interval.CloseOf(open);
        return string.Join(',',
            Stamp(open, key), F(c.O), F(c.H), F(c.L), F(c.C), F(c.V), Stamp(closeTime, key, minusOneUnit: true), F(c.Q),
            c.N.ToString(CultureInfo.InvariantCulture), F(c.V / 2), F(c.Q / 2), "0");
    }

    public static byte[] Zip(ArchiveFileKey key, IEnumerable<string> lines)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(key.FileName.Replace(".zip", ".csv", StringComparison.Ordinal));
            using var writer = new StreamWriter(entry.Open(), Encoding.ASCII);
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        return memory.ToArray();
    }

    public static string Checksum(ArchiveFileKey key, byte[] zip) => $"{ChecksumVerifier.Sha256Hex(zip)}  {key.FileName}";

    /// <summary>Hourly prices derived from the open time; 4h and 1d values are exact aggregates of these.</summary>
    private static (decimal O, decimal H, decimal L, decimal C, decimal V, decimal Q, long N) Aggregate(DateTimeOffset open, int hours, decimal volumeScale)
    {
        var parts = Enumerable.Range(0, hours).Select(i => Hourly(open.AddHours(i), volumeScale)).ToList();
        return (parts[0].O, parts.Max(p => p.H), parts.Min(p => p.L), parts[^1].C, parts.Sum(p => p.V), parts.Sum(p => p.Q), parts.Sum(p => p.N));
    }

    private static (decimal O, decimal H, decimal L, decimal C, decimal V, decimal Q, long N) Hourly(DateTimeOffset open, decimal volumeScale)
    {
        var step = (open.ToUnixTimeSeconds() / 3600) % 97;
        var o = 100m + step;
        var c = o + 0.5m;
        var v = (10m + (step % 7)) * volumeScale;
        return (o, c + 1m, o - 1m, c, v, v * o, 5);
    }

    private static string Stamp(DateTimeOffset instant, ArchiveFileKey key, bool minusOneUnit = false)
    {
        var micro = key.ExpectsMicroseconds;
        var value = micro
            ? (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10
            : instant.ToUnixTimeMilliseconds();
        return (minusOneUnit ? value - 1 : value).ToString(CultureInfo.InvariantCulture);
    }

    private static string F(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}

/// <summary>
/// In-memory stand-in for the archive host: S3-style listings and file downloads. Records every request so
/// tests can prove what was (and was not) asked for. It never touches the network.
/// </summary>
public sealed class FakeArchive : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public List<Uri> Requests { get; } = [];

    public int ListingPageSize { get; init; } = 1000;

    public void Add(ArchiveFileKey key, IEnumerable<string> lines, bool withChecksum = true, string? checksumOverride = null)
    {
        var zip = SyntheticKlines.Zip(key, lines);
        _files[key.Path] = zip;
        if (withChecksum)
        {
            _files[key.ChecksumPath] = Encoding.ASCII.GetBytes(checksumOverride ?? SyntheticKlines.Checksum(key, zip));
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri);
        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (!string.IsNullOrEmpty(uri.Query))
        {
            return Task.FromResult(Listing(HttpUtility.ParseQueryString(uri.Query)));
        }

        return Task.FromResult(_files.TryGetValue(path, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private HttpResponseMessage Listing(System.Collections.Specialized.NameValueCollection query)
    {
        var prefix = query["prefix"] ?? string.Empty;
        var delimiter = query["delimiter"];
        var marker = query["marker"];
        var maxKeys = Math.Min(int.Parse(query["max-keys"] ?? "1000", CultureInfo.InvariantCulture), ListingPageSize);

        var entries = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => delimiter is null ? (Key: k, IsPrefix: false) : Collapse(k, prefix, delimiter))
            .Distinct()
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Where(e => marker is null || string.CompareOrdinal(e.Key, marker) > 0)
            .ToList();
        var page = entries.Take(maxKeys).ToList();
        var truncated = entries.Count > page.Count;

        var xml = new StringBuilder("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        xml.Append($"<Prefix>{prefix}</Prefix><IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>");
        if (truncated)
        {
            xml.Append($"<NextMarker>{page[^1].Key}</NextMarker>");
        }

        foreach (var entry in page)
        {
            xml.Append(entry.IsPrefix ? $"<CommonPrefixes><Prefix>{entry.Key}</Prefix></CommonPrefixes>" : $"<Contents><Key>{entry.Key}</Key></Contents>");
        }

        xml.Append("</ListBucketResult>");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }

    private static (string Key, bool IsPrefix) Collapse(string key, string prefix, string delimiter)
    {
        var rest = key[prefix.Length..];
        var index = rest.IndexOf(delimiter, StringComparison.Ordinal);
        return index < 0 ? (key, false) : (prefix + rest[..(index + delimiter.Length)], true);
    }
}
