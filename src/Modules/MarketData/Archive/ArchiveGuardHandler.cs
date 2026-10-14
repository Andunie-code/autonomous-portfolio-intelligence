using System.Net;

namespace PortfolioIntelligence.MarketData.Archive;

/// <summary>
/// The single egress point for archive traffic. Every request is validated before it is sent and every 3xx
/// response is rejected (automatic redirects must be disabled on the inner handler).
/// </summary>
public sealed class ArchiveGuardHandler(ArchiveRequestPolicy policy, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public ArchiveRequestPolicy Policy { get; } = policy;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
        {
            throw new ArchivePolicyViolationException("Only GET requests with an absolute URI are allowed.");
        }

        Policy.Validate(request.RequestUri);
        var response = await base.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            response.Dispose();
            throw new ArchivePolicyViolationException($"Unexpected redirect ({status}) from {request.RequestUri.Host}; redirects are rejected.");
        }

        return response;
    }

    /// <summary>Builds the real network handler. Used only by the CLI after the Gate 2 confirmation.</summary>
    public static HttpMessageHandler CreateNetworkHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
