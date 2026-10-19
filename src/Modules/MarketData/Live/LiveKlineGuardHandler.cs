using System.Globalization;
using System.Net;
using System.Web;

namespace PortfolioIntelligence.MarketData.Live;

public sealed class LiveRequestRejectedException(string message) : Exception(message);

/// <summary>HTTP 429: back off for <see cref="RetryAfter"/> (S-B10).</summary>
public sealed class LiveRateLimitedException(TimeSpan? retryAfter)
    : Exception(string.Create(CultureInfo.InvariantCulture, $"Rate limited (429); retry after {retryAfter?.TotalSeconds ?? 0:F0} s."))
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>HTTP 418: the IP is banned; trading halts automatically (risk-model §3.1).</summary>
public sealed class LiveIpBannedException() : Exception("IP banned (418); new entries halt until the user reviews.");

/// <summary>
/// The single egress point of Trading.Host live market data (m5-protocol §2.3, S-B9/S-B10): only GET
/// https://data-api.binance.vision/api/v3/klines for manifest symbols, 1h or 4h, with startTime and endTime and
/// limit at most 1000; redirects are rejected; 429 and 418 become typed exceptions. Weight usage is exposed.
/// </summary>
public sealed class LiveKlineGuardHandler(IReadOnlySet<string> symbols, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string Host = "data-api.binance.vision";
    public const string Path = "/api/v3/klines";

    private static readonly HashSet<string> Allowed = ["symbol", "interval", "startTime", "endTime", "limit"];

    public int? UsedWeight { get; private set; }

    public static void Validate(Uri uri, IReadOnlySet<string> symbols)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.Ordinal) || !uri.IsDefaultPort)
        {
            throw new LiveRequestRejectedException($"Host {uri.Host} is not allowed for live market data.");
        }

        if (uri.AbsolutePath != Path || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new LiveRequestRejectedException($"Path {uri.AbsolutePath} is not allowed.");
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query.AllKeys.Any(k => k is null || !Allowed.Contains(k)) || query.AllKeys.Any(k => query.GetValues(k!)!.Length != 1))
        {
            throw new LiveRequestRejectedException("Only symbol, interval, startTime, endTime and limit are allowed, once each.");
        }

        if (query["symbol"] is not { } symbol || !symbols.Contains(symbol))
        {
            throw new LiveRequestRejectedException("The symbol is not a manifest pair.");
        }

        if (query["interval"] is not ("1h" or "4h"))
        {
            throw new LiveRequestRejectedException("Only 1h and 4h intervals are allowed.");
        }

        if (!long.TryParse(query["startTime"], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(query["endTime"], NumberStyles.None, CultureInfo.InvariantCulture, out var end) || end < start)
        {
            throw new LiveRequestRejectedException("startTime and endTime (ms) are required and must be ordered.");
        }

        if (query["limit"] is { } limit && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 1000))
        {
            throw new LiveRequestRejectedException("limit must be between 1 and 1000.");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
        {
            throw new LiveRequestRejectedException("Only GET requests with an absolute URI are allowed.");
        }

        Validate(request.RequestUri, symbols);
        var response = await base.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (response.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var weights) && int.TryParse(weights.FirstOrDefault(), CultureInfo.InvariantCulture, out var used))
        {
            UsedWeight = used;
        }

        if (status is >= 300 and < 400)
        {
            response.Dispose();
            throw new LiveRequestRejectedException($"Unexpected redirect ({status}); redirects are rejected.");
        }

        if (status == 418)
        {
            response.Dispose();
            throw new LiveIpBannedException();
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            throw new LiveRateLimitedException(retry);
        }

        return response;
    }

    public static HttpMessageHandler CreateNetworkHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
