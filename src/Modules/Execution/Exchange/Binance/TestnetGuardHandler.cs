using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Web;

namespace PortfolioIntelligence.Execution.Exchange.Binance;

/// <summary>
/// The single egress point of Trading.Host's exchange access (D33, m6-protocol §8.1). Only HTTPS to
/// testnet.binance.vision on the default port, only the listed method and path pairs, only allow-listed symbols;
/// no user info or fragment. Redirects are refused (the network handler does not follow them, and any 3xx is an
/// error); 418 and 429 become typed exceptions. The host is a constant: no configuration can point this client at
/// another exchange. It is the last handler before the network, so it checks the final, signed request.
/// </summary>
public sealed class TestnetGuardHandler(IReadOnlySet<string> symbols, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string Host = "testnet.binance.vision";

    private static readonly HashSet<(string Method, string Path)> Allowed =
    [
        ("GET", "/api/v3/time"), ("GET", "/api/v3/exchangeInfo"), ("GET", "/api/v3/avgPrice"), ("GET", "/api/v3/account"),
        ("GET", "/api/v3/order"), ("GET", "/api/v3/openOrders"), ("GET", "/api/v3/myTrades"),
        ("POST", "/api/v3/order"), ("POST", "/api/v3/order/cancelReplace"), ("DELETE", "/api/v3/order"),
    ];

    private static readonly HashSet<string> WithoutSymbol = ["/api/v3/time", "/api/v3/account", "/api/v3/exchangeInfo"];

    public int? UsedWeight { get; private set; }

    public int? OrderCount { get; private set; }

    public static void Validate(HttpMethod method, Uri uri, IReadOnlySet<string> symbols)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.Ordinal) || !uri.IsDefaultPort)
        {
            throw new ExchangeRequestRejectedException($"Host {uri.Host} is not allowed: exchange access is locked to {Host}.");
        }

        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !Allowed.Contains((method.Method, uri.AbsolutePath)))
        {
            throw new ExchangeRequestRejectedException($"{method.Method} {uri.AbsolutePath} is not an allowed exchange request.");
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (query.AllKeys.Any(k => k is null || query.GetValues(k)!.Length != 1))
        {
            throw new ExchangeRequestRejectedException("Every query parameter must be named and appear once.");
        }

        if (uri.AbsolutePath == "/api/v3/exchangeInfo")
        {
            var requested = query["symbols"] is { } list ? JsonSerializer.Deserialize<string[]>(list) ?? [] : [];
            if (requested.Length == 0 || requested.Any(s => !symbols.Contains(s)))
            {
                throw new ExchangeRequestRejectedException("exchangeInfo is allowed only for allow-listed symbols.");
            }
        }
        else if (!WithoutSymbol.Contains(uri.AbsolutePath) && (query["symbol"] is not { } symbol || !symbols.Contains(symbol)))
        {
            throw new ExchangeRequestRejectedException("The symbol is not allow-listed.");
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            throw new ExchangeRequestRejectedException("An absolute request URI is required.");
        }

        Validate(request.Method, request.RequestUri, symbols);
        var response = await base.SendAsync(request, cancellationToken);
        UsedWeight = Header(response, "X-MBX-USED-WEIGHT-1M") ?? UsedWeight;
        OrderCount = Header(response, "X-MBX-ORDER-COUNT-10S") ?? OrderCount;
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            response.Dispose();
            throw new ExchangeRequestRejectedException($"Unexpected redirect ({status}); redirects are rejected.");
        }

        if (status == 418)
        {
            response.Dispose();
            throw new ExchangeIpBannedException();
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            throw new ExchangeRateLimitedException(retry);
        }

        return response;
    }

    private static int? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The network handler: never follows redirects.</summary>
    public static HttpMessageHandler CreateNetworkHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
