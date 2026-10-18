using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PortfolioIntelligence.Ai.Gemini;
using PortfolioIntelligence.Ai.Tests.Support;

namespace PortfolioIntelligence.Ai.Tests;

/// <summary>
/// Gemini adapter over the SDK with an in-memory HTTP handler (ADR-0017 §2): request shape, usage and model mapping,
/// errors, SDK retries switched off, host guard and redirects. No network; the key is a dummy.
/// </summary>
public class GeminiProviderTests
{
    private const string DummyKey = "test-key-not-a-secret";

    private static readonly AiRequest Request = new(TestConfig.Model, "system text", [new AiMessage(AiRole.User, "hello")],
        TestConfig.AnswerSchema, null, 256, TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Request_goes_to_the_allowed_host_with_schema_and_system_instruction()
    {
        var handler = new ScriptedHandler(Ok("""{"answer":"ok"}"""));
        using var provider = Provider(handler);

        var response = await provider.GenerateAsync(Request, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https", sent.Uri.Scheme);
        Assert.Equal(GeminiGuardHandler.AllowedHost, sent.Uri.Host);
        Assert.Contains(TestConfig.Model + ":generateContent", sent.Uri.AbsolutePath);
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("system text", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("application/json", body["generationConfig"]!["responseMimeType"]!.GetValue<string>());
        Assert.NotNull(body["generationConfig"]!["responseJsonSchema"]);
        Assert.Equal(256, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal("""{"answer":"ok"}""", response.Text);
        Assert.Equal(AiFinish.Stop, response.Finish);
    }

    [Fact]
    public async Task Usage_counts_thinking_and_tool_prompt_tokens_and_the_model_version_is_reported()
    {
        var handler = new ScriptedHandler(Ok("{}", usage: """{"promptTokenCount":12,"toolUsePromptTokenCount":3,"candidatesTokenCount":5,"thoughtsTokenCount":7,"totalTokenCount":27}""", modelVersion: "test-model-a-002"));
        using var provider = Provider(handler);

        var response = await provider.GenerateAsync(Request, default);

        Assert.Equal(new AiUsage(15, 12), response.Usage);
        Assert.Equal("test-model-a-002", response.ActualModel);
    }

    [Fact]
    public async Task Missing_model_version_is_never_taken_as_the_requested_model()
    {
        using var provider = Provider(new ScriptedHandler(Ok("{}", modelVersion: null)));

        var response = await provider.GenerateAsync(Request, default);

        Assert.NotEqual(TestConfig.Model, response.ActualModel);
    }

    [Fact]
    public async Task Function_calls_are_mapped()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"lookup_constant","args":{"name":"alpha"}}}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1},"modelVersion":"test-model-a"}
            """));
        using var provider = Provider(handler);

        var response = await provider.GenerateAsync(Request, default);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("lookup_constant", call.Name);
        Assert.Equal("alpha", call.Arguments["name"]!.GetValue<string>());
        Assert.Equal(AiFinish.ToolCall, response.Finish);
    }

    [Fact]
    public async Task Model_turn_is_resent_exactly_as_received_with_thought_signatures()
    {
        var handler = new ScriptedHandler(
            Json(HttpStatusCode.OK, """
                {"candidates":[{"content":{"role":"model","parts":[{"text":"plan","thought":true,"thoughtSignature":"c2lnLXRob3VnaHQ="},{"functionCall":{"name":"lookup_constant","args":{"name":"alpha"}},"thoughtSignature":"c2lnLWNhbGw="}]},"finishReason":"STOP"}],
                 "usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1},"modelVersion":"test-model-a"}
                """),
            Ok("""{"answer":"42"}"""));
        using var provider = Provider(handler);

        var first = await provider.GenerateAsync(Request, default);
        var history = new List<AiMessage>(Request.Messages)
        {
            first.AsModelMessage(),
            new(AiRole.Tool, null, ToolName: "lookup_constant", ToolResultJson: """{"value":42}"""),
        };
        await provider.GenerateAsync(Request with { Messages = history }, default);

        Assert.Null(first.Text); // the thought text is never part of the answer
        var resent = JsonNode.Parse(handler.Requests[1].Body)!["contents"]![1]!;
        Assert.Equal("model", resent["role"]!.GetValue<string>());
        var parts = resent["parts"]!.AsArray();
        Assert.Equal("c2lnLXRob3VnaHQ=", parts[0]!["thoughtSignature"]!.GetValue<string>());
        Assert.True(parts[0]!["thought"]!.GetValue<bool>());
        Assert.Equal("c2lnLWNhbGw=", parts[1]!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("lookup_constant", parts[1]!["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("lookup_constant", JsonNode.Parse(handler.Requests[1].Body)!["contents"]![2]!["parts"]![0]!["functionResponse"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Api_error_message_is_reported_with_the_key_masked()
    {
        var body = """{"error":{"code":400,"message":"API key not valid: """ + DummyKey + """ / AIzaSyA1234567890abcdefghijklmnopq","status":"INVALID_ARGUMENT"}}""";
        using var provider = Provider(new ScriptedHandler(Json(HttpStatusCode.BadRequest, body)));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Request, default));

        Assert.Contains("INVALID_ARGUMENT: API key not valid", error.Message);
        Assert.DoesNotContain(DummyKey, error.Message);
        Assert.DoesNotContain("AIzaSy", error.Message);
    }

    [Fact]
    public async Task Schemas_are_sent_without_additional_properties_or_schema_keywords()
    {
        var handler = new ScriptedHandler(Ok("{}"));
        var tools = new NeutralToolSet([new NeutralTool("lookup_constant", "test",
            NeutralSchema.Parse("""{"type":"object","properties":{"name":{"type":"string","enum":["alpha"]}},"required":["name"],"additionalProperties":false}"""))]);
        using var provider = Provider(handler);

        await provider.GenerateAsync(Request with { Tools = tools }, default);

        var body = handler.Requests.Single().Body;
        Assert.DoesNotContain("additionalProperties", body);
        Assert.DoesNotContain("$schema", body);
        Assert.Contains("\"required\":[\"answer\"]", body.Replace(" ", ""));
        Assert.Contains("lookup_constant", body);
    }

    [Fact]
    public async Task Empty_answer_carries_the_finish_details()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model"},"finishReason":"MAX_TOKENS","finishMessage":"limit"}],"usageMetadata":{"promptTokenCount":5,"thoughtsTokenCount":256},"modelVersion":"test-model-a"}
            """));
        using var provider = Provider(handler);

        var response = await provider.GenerateAsync(Request, default);

        Assert.Null(response.Text);
        Assert.Equal(AiFinish.MaxTokens, response.Finish);
        Assert.Contains("finishReason=MAX_TOKENS", response.Diagnostic);
        Assert.Equal(256, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task Spike_observation_shows_why_a_call_failed()
    {
        var rig = new RouterRig();
        for (var i = 0; i < 5; i++)
        {
            rig.Provider.Throws(400);
        }

        var results = await new SpikeRunner(rig.Guard, TestConfig.Model).RunAsync(new JobContext("spike-test", JobKind.Spike), default);

        Assert.All(results, r => Assert.Contains("[call 1: Failed: HTTP 400", r.Observation));
    }

    [Fact]
    public async Task Blocked_prompt_is_a_refusal()
    {
        using var provider = Provider(new ScriptedHandler(Json(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"},"usageMetadata":{"promptTokenCount":4},"modelVersion":"test-model-a"}""")));

        var response = await provider.GenerateAsync(Request, default);

        Assert.Equal(AiFinish.Refusal, response.Finish);
    }

    [Fact]
    public async Task Rate_limit_is_retryable_with_retry_after_and_the_sdk_does_not_retry_on_its_own()
    {
        var limited = Json((HttpStatusCode)429, """{"error":{"code":429,"message":"quota","status":"RESOURCE_EXHAUSTED"}}""");
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        var handler = new ScriptedHandler(limited, Ok("{}"));
        using var provider = Provider(handler);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Request, default));

        Assert.Equal(429, error.StatusCode);
        Assert.True(error.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
        Assert.Single(handler.Requests);
        Assert.Equal(1, provider.HttpRequests);
    }

    [Fact]
    public async Task Server_error_is_one_request_and_retryable()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"busy","status":"UNAVAILABLE"}}"""), Ok("{}"));
        using var provider = Provider(handler);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Request, default));

        Assert.True(error.Retryable);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Client_error_is_not_retryable()
    {
        using var provider = Provider(new ScriptedHandler(Json(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"bad","status":"INVALID_ARGUMENT"}}""")));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateAsync(Request, default));

        Assert.Equal(400, error.StatusCode);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task Cancellation_reaches_the_http_call()
    {
        var handler = new ScriptedHandler { Hang = true };
        using var provider = Provider(handler);
        using var cancellation = new CancellationTokenSource();

        var call = provider.GenerateAsync(Request, cancellation.Token);
        await handler.Started.Task;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Theory]
    [InlineData("https://example.com/v1beta/models/x:generateContent")]
    [InlineData("http://generativelanguage.googleapis.com/v1beta/models/x:generateContent")]
    [InlineData("https://generativelanguage.googleapis.com:8443/v1beta/models/x:generateContent")]
    public async Task Guard_rejects_any_other_destination(string url)
    {
        var inner = new ScriptedHandler(Ok("{}"));
        using var client = new HttpClient(new GeminiGuardHandler(inner));

        await Assert.ThrowsAsync<GeminiPolicyViolationException>(() => client.GetAsync(url));
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Guard_rejects_redirects()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/") } };
        using var client = new HttpClient(new GeminiGuardHandler(new ScriptedHandler(redirect)));

        await Assert.ThrowsAsync<GeminiPolicyViolationException>(() => client.GetAsync("https://generativelanguage.googleapis.com/v1beta/models"));
    }

    [Fact]
    public void Network_handler_does_not_follow_redirects()
    {
        using var handler = (SocketsHttpHandler)GeminiGuardHandler.CreateNetworkHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void Estimate_reserves_the_full_output_budget_at_list_price()
    {
        using var provider = Provider(new ScriptedHandler());
        var configuration = TestConfig.Configuration();

        var estimate = provider.EstimateMaxCost(Request);

        var outputOnly = configuration.Prices.Cost("gemini", TestConfig.Model, new AiUsage(0, Request.MaxOutputTokens));
        Assert.True(estimate > outputOnly);
    }

    [Fact]
    public void Tests_only_use_in_memory_handlers()
    {
        var directory = Path.Combine(RepositoryRoot.Path, "tests", "Ai.Tests");
        var offenders = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => (l.Text.Contains("new SocketsHttpHandler", StringComparison.Ordinal)
                         || l.Text.Contains("new HttpClientHandler", StringComparison.Ordinal)
                         || l.Text.Contains("new HttpClient()", StringComparison.Ordinal)
                         || (l.Text.Contains("CreateNetworkHandler()", StringComparison.Ordinal) && !l.Text.Contains("using var handler = (SocketsHttpHandler)", StringComparison.Ordinal)))
                        && !l.Text.Contains("l.Text.Contains(", StringComparison.Ordinal)) // this scan's own patterns
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static GeminiProvider Provider(ScriptedHandler handler) => new(DummyKey, TestConfig.Configuration().Prices, handler);

    private static HttpResponseMessage Ok(string text, string usage = """{"promptTokenCount":10,"candidatesTokenCount":4}""", string? modelVersion = TestConfig.Model)
    {
        var candidate = new JsonObject
        {
            ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            ["finishReason"] = "STOP",
        };
        var body = new JsonObject { ["candidates"] = new JsonArray(candidate), ["usageMetadata"] = JsonNode.Parse(usage), ["responseId"] = "r1" };
        if (modelVersion is not null)
        {
            body["modelVersion"] = modelVersion;
        }

        return Json(HttpStatusCode.OK, body.ToJsonString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<(Uri Uri, string Body)> Requests { get; } = [];

        public bool Hang { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            Started.TrySetResult();
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("unexpected request");
        }
    }
}
