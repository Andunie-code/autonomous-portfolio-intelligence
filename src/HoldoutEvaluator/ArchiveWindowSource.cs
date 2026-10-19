using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.HoldoutEvaluator;

public sealed class ArchiveWindowException(string message) : Exception(message);

/// <summary>
/// The evaluator's only egress (m5-protocol §10 E3): GET https://data.binance.vision for 4h spot kline files and
/// their checksums under /data/spot/{monthly|daily}/klines/; redirects are rejected. Runs only on the egress network.
/// </summary>
public sealed partial class ArchiveWindowGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string Host = "data.binance.vision";

    public static void Validate(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != Host || !uri.IsDefaultPort || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            throw new ArchiveWindowException($"{uri.Host}{uri.AbsolutePath} is not an allowed archive request.");
        }

        if (!FilePath().IsMatch(uri.AbsolutePath))
        {
            throw new ArchiveWindowException($"{uri.AbsolutePath} is not a 4h spot kline file.");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
        {
            throw new ArchiveWindowException("Only GET requests with an absolute URI are allowed.");
        }

        Validate(request.RequestUri);
        var response = await base.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new ArchiveWindowException("Redirects are rejected.");
        }

        return response;
    }

    public static HttpMessageHandler CreateNetworkHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    [GeneratedRegex(@"^/data/spot/(monthly|daily)/klines/[A-Z0-9]{2,30}/4h/[A-Z0-9]{2,30}-4h-\d{4}-\d{2}(-\d{2})?\.zip(\.CHECKSUM)?$")]
    private static partial Regex FilePath();
}

/// <summary>
/// Window data from the canonical archive (architecture §6.2 "evaluator data acquisition", m5-protocol §10 E3):
/// monthly files for whole months, daily files otherwise; a SHA-256 checksum is required for every file; candles
/// are parsed and checked (grid, order, OHLC, close time) and cut to [start, end). A window that ends later than
/// now − publication buffer is refused before any request (application layer of V4; the database trigger is the
/// second layer). Verified files may be cached in <paramref name="cacheDirectory"/> (the evaluator's volume).
/// It does not depend on the MarketData module (architecture rule); the M1 parser rejects data after C by design.
/// </summary>
public sealed class ArchiveWindowSource(HttpClient http, IClock clock, string? cacheDirectory = null) : IWindowSource
{
    private static readonly DateTimeOffset MicrosecondsFrom = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string Kind => "archive";

    public IReadOnlyDictionary<string, IReadOnlyList<Candle>> Load(IReadOnlyList<string> symbols, DateTimeOffset start, DateTimeOffset end)
    {
        if (end > clock.UtcNow - ProspectiveService.PublicationBuffer)
        {
            throw new EarlyEvaluationException($"archive window ends at {end:O}, after now minus the publication buffer");
        }

        var result = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (!ArchiveFileKey.IsValidSymbol(symbol))
            {
                throw new ArchiveWindowException($"invalid symbol {symbol}");
            }

            var candles = new List<Candle>();
            foreach (var (path, from, to) in Files(symbol, start, end))
            {
                var zip = Fetch(path);
                candles.AddRange(Parse(symbol, Lines(zip), from, to).Where(c => c.OpenTime >= start && c.OpenTime < end));
            }

            result[symbol] = candles.OrderBy(c => c.OpenTime).ToList();
        }

        return result;
    }

    /// <summary>Monthly files for months inside [start, end), daily files for the partial months at the edges.</summary>
    public static IEnumerable<(string Path, DateTimeOffset From, DateTimeOffset To)> Files(string symbol, DateTimeOffset start, DateTimeOffset end)
    {
        var day = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
        while (day < end)
        {
            var month = new DateTimeOffset(day.Year, day.Month, 1, 0, 0, 0, TimeSpan.Zero);
            if (day == month && month.AddMonths(1) <= end)
            {
                yield return (string.Create(CultureInfo.InvariantCulture, $"/data/spot/monthly/klines/{symbol}/4h/{symbol}-4h-{day:yyyy-MM}.zip"), month, month.AddMonths(1));
                day = month.AddMonths(1);
            }
            else
            {
                yield return (string.Create(CultureInfo.InvariantCulture, $"/data/spot/daily/klines/{symbol}/4h/{symbol}-4h-{day:yyyy-MM-dd}.zip"), day, day.AddDays(1));
                day = day.AddDays(1);
            }
        }
    }

    private byte[] Fetch(string path)
    {
        var cached = cacheDirectory is null ? null : System.IO.Path.Combine(cacheDirectory, path.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (cached is not null && File.Exists(cached))
        {
            return File.ReadAllBytes(cached);
        }

        var zip = Get($"https://{ArchiveWindowGuard.Host}{path}");
        var checksum = Encoding.ASCII.GetString(Get($"https://{ArchiveWindowGuard.Host}{path}.CHECKSUM")).Trim();
        var match = Regex.Match(checksum, @"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S+)$");
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (!match.Success || match.Groups["name"].Value != name)
        {
            throw new ArchiveWindowException($"{name}: checksum file is missing or malformed");
        }

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(zip)), match.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveWindowException($"{name}: SHA-256 mismatch");
        }

        if (cached is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cached)!);
            File.WriteAllBytes(cached, zip);
        }

        return zip;
    }

    private byte[] Get(string url)
    {
        using var response = http.GetAsync(new Uri(url)).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new ArchiveWindowException($"{url}: HTTP {(int)response.StatusCode}");
        }

        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    private static IEnumerable<string> Lines(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = archive.Entries.Count == 1 ? archive.Entries[0] : throw new ArchiveWindowException("archive must contain one CSV file");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>12-column kline CSV; timestamps in milliseconds before 2025 and microseconds from 2025 on (S-B7).</summary>
    public static IReadOnlyList<Candle> Parse(string symbol, IEnumerable<string> lines, DateTimeOffset fileStart, DateTimeOffset fileEnd)
    {
        var interval = CandleInterval.FourHours;
        var length = interval.FixedLength!.Value;
        var candles = new List<Candle>();
        foreach (var (raw, number) in lines.Select((l, i) => (l.Trim(), i + 1)))
        {
            if (raw.Length == 0 || (number == 1 && raw.StartsWith("open_time", StringComparison.Ordinal)))
            {
                continue;
            }

            var f = raw.Split(',');
            if (f.Length != 12)
            {
                throw new ArchiveWindowException($"{symbol} line {number}: expected 12 columns");
            }

            var open = Time(f[0], fileStart);
            var close = Time(f[6], fileStart);
            if (open < fileStart || open >= fileEnd || !interval.IsAligned(open) || close <= open || close >= open + length
                || (candles.Count > 0 && open <= candles[^1].OpenTime))
            {
                throw new ArchiveWindowException($"{symbol} line {number}: time fields are inconsistent");
            }

            var candle = new Candle(symbol, interval, open, D(f[1]), D(f[2]), D(f[3]), D(f[4]), D(f[5]), D(f[7]), long.Parse(f[8], CultureInfo.InvariantCulture));
            if (!(candle.Low > 0 && candle.Low <= Math.Min(candle.Open, candle.Close) && Math.Max(candle.Open, candle.Close) <= candle.High) || candle.BaseVolume < 0 || candle.QuoteVolume < 0)
            {
                throw new ArchiveWindowException($"{symbol} line {number}: OHLC rules violated");
            }

            candles.Add(candle);
        }

        return candles;

        static decimal D(string text) => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        static DateTimeOffset Time(string text, DateTimeOffset fileStart)
        {
            var value = long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
            var micro = fileStart >= MicrosecondsFrom;
            if (micro != (text.Length == 16))
            {
                throw new ArchiveWindowException($"timestamp {text} has the wrong unit for {fileStart:yyyy-MM}");
            }

            return micro ? DateTimeOffset.UnixEpoch.AddTicks(value * 10) : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }
}
