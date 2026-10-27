using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PortfolioIntelligence.Execution.Exchange.Binance;

/// <summary>Testnet API key and HMAC secret (Docker secrets of Trading.Host only, D33). Never printed.</summary>
public sealed class TestnetCredentials(string apiKey, string secret)
{
    public string ApiKey { get; } = apiKey;

    internal string Secret { get; } = secret;

    public override string ToString() => "TestnetCredentials(***)";
}

/// <summary>
/// Signs requests marked with <see cref="Signed"/> (S-B11): appends recvWindow and timestamp to the query string,
/// signs the exact, percent-encoded query string with HMAC-SHA256 (hex) and sends the key in X-MBX-APIKEY.
/// Parameters travel in the query string for every method, so the signed payload is what the exchange receives.
/// </summary>
public sealed class HmacSigningHandler(TestnetCredentials credentials, ExchangeClock clock, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const int RecvWindowMs = 5000;

    public static readonly HttpRequestOptionsKey<bool> Signed = new("pi.signed");

    public static string Sign(string payload, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(Signed, out var signed) && signed && request.RequestUri is { } uri)
        {
            var query = uri.Query.TrimStart('?');
            var timestamp = clock.Now.ToUnixTimeMilliseconds();
            var payload = string.Create(CultureInfo.InvariantCulture, $"{query}{(query.Length > 0 ? "&" : "")}recvWindow={RecvWindowMs}&timestamp={timestamp}");
            request.RequestUri = new Uri($"{uri.GetLeftPart(UriPartial.Path)}?{payload}&signature={Sign(payload, credentials.Secret)}");
            request.Headers.Remove("X-MBX-APIKEY");
            request.Headers.Add("X-MBX-APIKEY", credentials.ApiKey);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
