using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.GenAI;
using Google.GenAI.Types;

namespace PortfolioIntelligence.Ai.Gemini;

public sealed class GeminiPolicyViolationException(string message) : Exception(message);

/// <summary>
/// The single egress point for Gemini traffic (ADR-0017 §2, M1's ArchiveGuardHandler pattern): HTTPS to
/// <see cref="AllowedHost"/> only, every 3xx rejected. It also keeps the last <c>Retry-After</c> header, which the SDK's
/// exception does not expose (calls are sequential, concurrency 1).
/// </summary>
public sealed class GeminiGuardHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string AllowedHost = "generativelanguage.googleapis.com";

    public TimeSpan? LastRetryAfter { get; private set; }

    /// <summary><c>error.status</c> and <c>error.message</c> of the last non-success response (truncated), for diagnostics.</summary>
    public string? LastErrorDetail { get; private set; }

    public int Requests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Scheme: "https" } uri || !string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort)
        {
            throw new GeminiPolicyViolationException($"Request to '{request.RequestUri?.GetLeftPart(UriPartial.Authority)}' rejected: only https://{AllowedHost} is allowed.");
        }

        Requests++;
        LastRetryAfter = null;
        LastErrorDetail = null;
        var response = await base.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            response.Dispose();
            throw new GeminiPolicyViolationException($"Unexpected redirect ({status}) from {AllowedHost}; redirects are rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            // Buffered, so the SDK can still read the body after us.
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            LastErrorDetail = ErrorDetail(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        LastRetryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
        return response;
    }

    private static string ErrorDetail(string body)
    {
        const int MaxLength = 500;
        string detail;
        try
        {
            var error = System.Text.Json.Nodes.JsonNode.Parse(body)?["error"];
            detail = $"{error?["status"]?.GetValue<string>()}: {error?["message"]?.GetValue<string>()}";
        }
        catch (System.Text.Json.JsonException)
        {
            detail = body;
        }

        return detail.Length > MaxLength ? detail[..MaxLength] + "…" : detail;
    }

    /// <summary>The real network handler. Created only by the CLI after the interactive --allow-network confirmation.</summary>
    public static HttpMessageHandler CreateNetworkHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}

/// <summary>
/// Gemini adapter over the official <c>Google.GenAI</c> SDK (ADR-0007, ADR-0017 §2). The SDK gets our
/// <see cref="HttpClient"/> (guard handler inside) and has its own retries switched off: retries, timeouts and the
/// kill switch belong to <see cref="AiCallGuard"/>. Model names come from configuration. Usage: input = prompt +
/// tool-use prompt tokens; output = candidate + thinking tokens (S-G2: thinking is billed as output).
/// </summary>
public sealed class GeminiProvider : IAiProvider, IDisposable
{
    public const string ProviderName = "gemini";

    // Conservative offline token estimate for the reservation: ~3 characters per token plus a per-message overhead.
    private const double CharactersPerToken = 3.0;
    private const int MessageOverheadTokens = 8;

    private readonly PriceTable _prices;
    private readonly GeminiGuardHandler _guard;
    private readonly HttpClient _http;
    private readonly Client _client;
    private readonly string _apiKey;

    public GeminiProvider(string apiKey, PriceTable prices, HttpMessageHandler innerHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _prices = prices;
        _apiKey = apiKey;
        _guard = new GeminiGuardHandler(innerHandler);
        _http = new HttpClient(_guard, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client = new Client(
            apiKey: apiKey,
            httpOptions: new HttpOptions { RetryOptions = new HttpRetryOptions { Attempts = 1 } },
            clientOptions: new ClientOptions { HttpClientFactory = () => _http });
    }

    public string Name => ProviderName;

    /// <summary>HTTP requests that passed the guard (tests check that the SDK made no retries of its own).</summary>
    public int HttpRequests => _guard.Requests;

    public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.Models.GenerateContentAsync(request.Model, Contents(request.Messages), Config(request), cancellationToken);
            return Map(response, request.Model);
        }
        catch (ApiException e)
        {
            throw new AiProviderException(Redact($"Gemini API error {e.StatusCode} {e.Status}: {_guard.LastErrorDetail ?? e.Message}"), e.StatusCode, _guard.LastRetryAfter, inner: e);
        }
        catch (GeminiPolicyViolationException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            throw new AiProviderException(Redact("Gemini network error: " + e.Message), (int?)e.StatusCode, _guard.LastRetryAfter, network: e.StatusCode is null, inner: e);
        }
    }

    public async Task<string> SubmitBatchAsync(IReadOnlyList<AiRequest> requests, CancellationToken cancellationToken)
    {
        var model = requests.Select(r => r.Model).Distinct(StringComparer.Ordinal).Single();
        var source = new BatchJobSource
        {
            InlinedRequests = requests.Select(r => new InlinedRequest { Model = r.Model, Contents = Contents(r.Messages), Config = Config(r) }).ToList(),
        };
        var job = await _client.Batches.CreateAsync(model, source, new CreateBatchJobConfig(), cancellationToken);
        return job.Name ?? throw new AiProviderException("Gemini batch job without a name.");
    }

    public async Task<AiBatchStatus> GetBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        var job = await _client.Batches.GetAsync(batchId, null, cancellationToken);
        var state = job.State?.Value switch
        {
            "JOB_STATE_SUCCEEDED" => AiBatchState.Succeeded,
            "JOB_STATE_FAILED" or "JOB_STATE_EXPIRED" => AiBatchState.Failed,
            "JOB_STATE_CANCELLED" or "JOB_STATE_CANCELLING" => AiBatchState.Cancelled,
            "JOB_STATE_RUNNING" => AiBatchState.Running,
            _ => AiBatchState.Pending,
        };
        var responses = job.Dest?.InlinedResponses?.Where(r => r.Response is not null).Select(r => Map(r.Response!, job.Model ?? "")).ToList() ?? [];
        return new AiBatchStatus(batchId, state, responses);
    }

    public Task CancelBatchAsync(string batchId, CancellationToken cancellationToken) => _client.Batches.CancelAsync(batchId, new CancelBatchJobConfig(), cancellationToken);

    public UsdAmount EstimateMaxCost(AiRequest request)
    {
        var characters = request.SystemInstruction.Length
            + request.Messages.Sum(m => (m.Text?.Length ?? 0) + (m.ToolResultJson?.Length ?? 0) + (m.ToolCalls?.Sum(c => c.Name.Length + c.Arguments.ToJsonString().Length) ?? 0))
            + (request.OutputSchema?.Json.Length ?? 0)
            + (request.Tools?.Tools.Sum(t => t.Name.Length + t.Description.Length + t.Parameters.Json.Length) ?? 0);
        var inputTokens = (int)Math.Ceiling(characters / CharactersPerToken) + MessageOverheadTokens * (request.Messages.Count + 1);
        return _prices.Cost(ProviderName, request.Model, new AiUsage(inputTokens, request.MaxOutputTokens));
    }

    /// <summary>Provider messages may echo request details: the key and anything shaped like a Google API key are masked.</summary>
    private string Redact(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Replace(_apiKey, "***", StringComparison.Ordinal), @"AIza[0-9A-Za-z_\-]{20,}", "***");

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    private static List<Content> Contents(IReadOnlyList<AiMessage> messages) => messages.Select(m => m.Role switch
    {
        AiRole.User => new Content { Role = "user", Parts = [new Part { Text = m.Text ?? "" }] },
        // A model turn received from Gemini is resent exactly as received (thought blocks and signatures, S-G8).
        AiRole.Model when m.ProviderContent is { } raw => Content.FromJson(raw) ?? throw new InvalidDataException("Unreadable provider content."),
        AiRole.Model => new Content
        {
            Role = "model",
            Parts = [
                .. string.IsNullOrEmpty(m.Text) ? [] : new[] { new Part { Text = m.Text } },
                .. (m.ToolCalls ?? []).Select(c => new Part { FunctionCall = new FunctionCall { Name = c.Name, Args = ToDictionary(c.Arguments) } }),
            ],
        },
        AiRole.Tool => new Content
        {
            Role = "user",
            Parts = [new Part { FunctionResponse = new FunctionResponse { Name = m.ToolName, Response = new Dictionary<string, object> { ["result"] = JsonDocument.Parse(m.ToolResultJson ?? "null").RootElement.Clone() } } }],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(messages), m.Role, "unknown role"),
    }).ToList();

    private static GenerateContentConfig Config(AiRequest request)
    {
        var config = new GenerateContentConfig
        {
            SystemInstruction = new Content { Parts = [new Part { Text = request.SystemInstruction }] },
            MaxOutputTokens = request.MaxOutputTokens,
        };
        if (request.OutputSchema is { } schema)
        {
            config.ResponseMimeType = "application/json";
            config.ResponseJsonSchema = Wire(schema);
        }

        if (request.Tools is { Tools.Count: > 0 } tools)
        {
            config.Tools =
            [
                new Tool
                {
                    FunctionDeclarations = tools.Tools.Select(t => new FunctionDeclaration
                    {
                        Name = t.Name,
                        Description = t.Description,
                        ParametersJsonSchema = Wire(t.Parameters),
                    }).ToList(),
                },
            ];
        }

        return config;
    }

    /// <summary>
    /// The schema as sent to Gemini: <c>additionalProperties</c> is dropped (a conservative subset; our own validator still
    /// rejects unknown properties, so nothing off-schema is accepted). No <c>$schema</c> or other keyword is ever sent.
    /// </summary>
    public static System.Text.Json.Nodes.JsonNode Wire(NeutralSchema schema)
    {
        var copy = schema.Root.DeepClone();
        Strip(copy);
        return copy;

        static void Strip(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject o)
            {
                o.Remove("additionalProperties");
                foreach (var (_, child) in o.ToList())
                {
                    Strip(child);
                }
            }
            else if (node is System.Text.Json.Nodes.JsonArray a)
            {
                foreach (var child in a)
                {
                    Strip(child);
                }
            }
        }
    }

    private static AiResponse Map(GenerateContentResponse response, string requestedModel)
    {
        var candidate = response.Candidates?.FirstOrDefault();
        var parts = candidate?.Content?.Parts ?? [];
        var text = string.Concat(parts.Where(p => p.Thought != true && p.Text is not null).Select(p => p.Text));
        var toolCalls = parts.Where(p => p.FunctionCall is not null)
            .Select(p => new AiToolCall(p.FunctionCall!.Name ?? "", JsonSerializer.SerializeToNode(p.FunctionCall.Args ?? []) as JsonObject ?? []))
            .ToList();
        var finish = response.PromptFeedback?.BlockReason is not null || candidate is null
            ? AiFinish.Refusal
            : candidate.FinishReason?.Value switch
            {
                "STOP" => toolCalls.Count > 0 ? AiFinish.ToolCall : AiFinish.Stop,
                "MAX_TOKENS" => AiFinish.MaxTokens,
                "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY" => AiFinish.Refusal,
                null when toolCalls.Count > 0 => AiFinish.ToolCall,
                _ => AiFinish.Other,
            };
        var usage = response.UsageMetadata;
        return new AiResponse(
            // An absent model version is never taken as the requested model: it makes the result degraded.
            response.ModelVersion ?? "unknown(" + requestedModel + ")",
            text.Length == 0 ? null : text,
            toolCalls,
            finish,
            new AiUsage(
                (usage?.PromptTokenCount ?? 0) + (usage?.ToolUsePromptTokenCount ?? 0),
                (usage?.CandidatesTokenCount ?? 0) + (usage?.ThoughtsTokenCount ?? 0)),
            response.ResponseId,
            Diagnostic(response, candidate),
            candidate?.Content is { } content ? JsonSerializer.Serialize(content) : null);
    }

    private static string Diagnostic(GenerateContentResponse response, Candidate? candidate) => string.Join("; ", new[]
    {
        $"candidates={response.Candidates?.Count ?? 0}",
        candidate?.FinishReason is { } finish ? $"finishReason={finish.Value}" : null,
        string.IsNullOrEmpty(candidate?.FinishMessage) ? null : $"finishMessage={candidate.FinishMessage}",
        response.PromptFeedback?.BlockReason is { } block ? $"blockReason={block.Value}" : null,
        string.IsNullOrEmpty(response.PromptFeedback?.BlockReasonMessage) ? null : $"blockMessage={response.PromptFeedback.BlockReasonMessage}",
        $"parts={candidate?.Content?.Parts?.Count ?? 0}",
    }.Where(x => x is not null));

    private static Dictionary<string, object> ToDictionary(JsonObject arguments) =>
        arguments.ToDictionary(kv => kv.Key, kv => (object)JsonDocument.Parse(kv.Value?.ToJsonString() ?? "null").RootElement.Clone());
}
