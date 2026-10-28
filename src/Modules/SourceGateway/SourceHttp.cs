using System.Net;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>A raw response: status, body (empty unless 2xx), content type.</summary>
public sealed record HttpFetch(int Status, byte[] Body, string? ContentType);

/// <summary>Raised when a response is larger than its content class allows; the body is not stored.</summary>
public sealed class SourceTooLargeException(string message) : Exception(message);

/// <summary>
/// GET through the guard with the retry rule of m7-protocol §2.2: 429 and 503 honour Retry-After (capped), at most the
/// configured retries with exponential back-off; 403 and other 4xx are never retried; policy violations propagate at once.
/// Bodies are read with a hard size cap, from Content-Length when present and by counting otherwise.
/// </summary>
public sealed class SourceHttp
{
    private readonly HttpClient _http;
    private readonly SourceConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryBaseDelay;

    public SourceHttp(SourceGuardHandler guard, TimeProvider time, TimeSpan? retryBaseDelay = null)
    {
        _configuration = guard.Policy.Configuration;
        _http = new HttpClient(guard, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(_configuration.RequestTimeoutSeconds) };
        _time = time;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(_configuration.MinIntervalSeconds);
    }

    public async Task<HttpFetch> GetAsync(Uri uri, ContentClass content, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)response.StatusCode;
                var transient = status is 429 or 503;
                if (!transient || attempt >= _configuration.MaxRetries)
                {
                    var body = status is >= 200 and < 300 ? await ReadCappedAsync(response, content, ct) : [];
                    return new HttpFetch(status, body, response.Content.Headers.ContentType?.MediaType);
                }

                retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - _time.GetUtcNow() : null);
            }
            catch (HttpRequestException) when (attempt < _configuration.MaxRetries)
            {
            }
            catch (TaskCanceledException) when (attempt < _configuration.MaxRetries && !ct.IsCancellationRequested)
            {
            }

            var backoff = _retryBaseDelay * Math.Pow(2, attempt);
            var cap = TimeSpan.FromSeconds(_configuration.MaxRetryAfterSeconds);
            var delay = retryAfter is { } wait && wait > backoff ? wait : backoff;
            await Task.Delay(delay > cap ? cap : delay, _time, ct);
        }
    }

    private async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, ContentClass content, CancellationToken ct)
    {
        var max = _configuration.MaxBytes[content];
        if (response.Content.Headers.ContentLength is { } length && length > max)
        {
            throw new SourceTooLargeException($"{content} response of {length} bytes exceeds the {max}-byte cap; not read.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new SourceTooLargeException($"{content} response exceeds the {max}-byte cap; reading stopped, nothing stored.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static bool IsSuccess(int status) => status is >= 200 and < 300;

    public static bool IsNotFound(int status) => status is (int)HttpStatusCode.NotFound or (int)HttpStatusCode.Gone;
}
