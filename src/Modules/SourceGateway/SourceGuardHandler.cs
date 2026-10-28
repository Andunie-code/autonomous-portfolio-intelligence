using System.Collections.Concurrent;
using System.Net;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>
/// Per-host pacing (m7-protocol §2.3, S-X1): one request in flight per host and at least the configured interval between
/// the starts of two requests to the same host, retries and robots.txt included.
/// </summary>
public sealed class DomainRateLimiter(TimeSpan minInterval, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastStart = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan MinInterval { get; } = minInterval;

    /// <summary>Waits for the host's turn; the returned lease ends the in-flight slot when disposed.</summary>
    public async Task<IDisposable> AcquireAsync(string host, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_lastStart.TryGetValue(host, out var last))
            {
                var wait = last + MinInterval - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, ct);
                }
            }

            _lastStart[host] = time.GetUtcNow();
            return new Lease(gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

/// <summary>
/// The single egress point for web research (ADR-0022 §2). Every request is validated against the allowlist before it is
/// sent and paced per host; every 3xx response is rejected (automatic redirects must be off on the inner handler), and
/// after a 403 no further request goes to that host in this process (S-X7: continued requests after "Access denied"
/// are treated as an attack).
/// </summary>
public sealed class SourceGuardHandler(SourceRequestPolicy policy, DomainRateLimiter limiter, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly ConcurrentDictionary<string, bool> _blocked = new(StringComparer.OrdinalIgnoreCase);

    public SourceRequestPolicy Policy { get; } = policy;

    /// <summary>Requests that reached the inner handler (tests count them).</summary>
    public int Sent => _sent;

    private int _sent;

    public bool IsBlocked(string host) => _blocked.ContainsKey(host);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
        {
            throw new SourcePolicyViolationException("Only GET requests with an absolute URI are allowed.");
        }

        Policy.Validate(request.RequestUri);
        var host = request.RequestUri.Host;
        if (IsBlocked(host))
        {
            throw new SourcePolicyViolationException($"{host} answered 403 earlier in this run; no further request is sent to it.");
        }

        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", Policy.Configuration.UserAgent);
        using (await limiter.AcquireAsync(host, cancellationToken))
        {
            Interlocked.Increment(ref _sent);
            var response = await base.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                response.Dispose();
                throw new SourcePolicyViolationException($"Unexpected redirect ({status}) from {host}; every redirect is rejected.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _blocked[host] = true;
            }

            return response;
        }
    }

    /// <summary>Builds the real network handler. Used only by the sources CLI after the interactive confirmation.</summary>
    public static HttpMessageHandler CreateNetworkHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 1,
    };
}
