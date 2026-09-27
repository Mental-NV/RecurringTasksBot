using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Tests;

public sealed class OpenRouterTransportTests
{
    private static readonly LlmRequest Prompt = new([new ChatMessage("user", "test")], 131072);

    [Fact]
    public async Task Stream_CollectsUnicodeCitationsUsage_AndIgnoresReasoningAndKeepAlives()
    {
        const string events = """
            : OPENROUTER PROCESSING

            event: message
            data: {"choices":[{"index":0,"delta":{"reasoning":"private reasoning"}}]}

            data: {"choices":[{"index":0,
            data: "delta":{"content":"Лайм 🌍","annotations":[{"type":"url_citation","url_citation":{"url":"https://example.com","title":"Source"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"content":" — результат"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":34}}

            data: [DONE]


            """;
        var result = await Execute(events);
        Assert.Equal("Лайм 🌍 — результат", result.AnswerText);
        Assert.Single(result.Sources);
        Assert.Equal(12, result.Usage.PromptTokens);
        Assert.Equal(34, result.Usage.CompletionTokens);
        Assert.True(result.SearchUsed);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"stop\"}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n")]
    [InlineData("data: broken-json\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":\"invalid\"}]}\n\n")]
    public async Task IncompleteOrMalformedStream_NeverReturnsPartialAnswer(string events)
    {
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => Execute(events));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
    }

    [Theory]
    [InlineData(429, LlmFailureKind.Transient)]
    [InlineData(502, LlmFailureKind.Transient)]
    [InlineData(401, LlmFailureKind.Permanent)]
    [InlineData(402, LlmFailureKind.Permanent)]
    public async Task Http200ProviderError_ClassifiedWithoutLeakingRawMessage(int code, LlmFailureKind expected)
    {
        var error = JsonSerializer.Serialize(new
        {
            error = new { code, message = "secret user prompt and credential", metadata = new { raw = "private" } }
        });
        foreach (var streaming in new[] { true, false })
        {
            var handler = new Handler(_ => Reply(streaming ? "data: " + error + "\n\n" : error,
                streaming ? "text/event-stream" : "application/json"));
            var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => Executor(handler).ExecuteAsync(Prompt));
            Assert.Equal(expected, ex.Kind);
            Assert.Contains($"code={code}", ex.Summary);
            Assert.DoesNotContain("secret", ex.Summary);
            Assert.DoesNotContain("private", ex.Summary);
        }
    }

    [Fact]
    public async Task ErrorAfterPartialContent_IsNotAcceptedAsSuccess()
    {
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => Execute(
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" +
            "data: {\"error\":{\"code\":502},\"choices\":[{\"finish_reason\":\"error\"}]}\n\n"));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
        Assert.Throws<LlmExecutionException>(() => OpenRouterResponseReader.ReadResponse(
            """{"choices":[{"message":{"content":"partial"},"finish_reason":"error","error":{"code":502}}]}""",
            TestLlm.Provider(), 131072));
    }

    [Fact]
    public async Task TransportFailure_PreservesSafeNetworkCategory()
    {
        var handler = new Handler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError,
            "private URL sk-or-v1-secret-user-prompt", new SocketException((int)SocketError.ConnectionReset)));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => Executor(handler).ExecuteAsync(Prompt));
        Assert.Contains("connecting", ex.Summary);
        Assert.Contains("ConnectionError", ex.Summary);
        Assert.Contains("ConnectionReset", ex.Summary);
        Assert.DoesNotContain("private", ex.Summary);
        Assert.DoesNotContain("sk-", ex.Summary);
    }

    [Fact]
    public async Task BodyReadTimeout_IsTransient_AndCallerCancellationIsPropagated()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream())
            {
                Headers = { ContentType = new("text/event-stream") }
            }
        });
        var shortExecution = TestLlm.Execution() with { RequestTimeout = TimeSpan.FromMilliseconds(50) };
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => Executor(handler, execution: shortExecution).ExecuteAsync(Prompt));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
        Assert.Contains("timed out", ex.Summary);
        Assert.Contains("reading response", ex.Summary);

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(handler).ExecuteAsync(Prompt, caller.Token));
    }

    [Fact]
    public async Task BodyReadIoFailure_IsRetriedWithoutExposingExceptionMessage()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream(fail: true))
            {
                Headers = { ContentType = new("text/event-stream") }
            }
        });
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => Executor(handler).ExecuteAsync(Prompt));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
        Assert.Contains("reading response", ex.Summary);
        Assert.DoesNotContain("secret", ex.Summary);
    }

    private static Task<LlmResult> Execute(string events) =>
        Executor(new Handler(_ => Reply(events, "text/event-stream"))).ExecuteAsync(Prompt);

    private static HttpResponseMessage Reply(string body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType)
    };

    private static OpenRouterLlmExecutor Executor(
        Handler handler, OpenRouterOptions? provider = null, ExecutionOptions? execution = null) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            provider ?? TestLlm.Provider(), execution ?? TestLlm.Execution(), "fake");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class WaitingStream(bool fail = false) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (fail) throw new IOException("secret raw transport data");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Maximum_MapsToMaxEffort()
        {
        Assert.Equal("max", OpenRouterLlmExecutor.MapReasoningEffort("Maximum"));
    }

    [Fact]
    public void UnknownEffort_FailsClearly()
        {
        Assert.Throws<InvalidOperationException>(() =>
            OpenRouterLlmExecutor.MapReasoningEffort("ultra"));
    }

    [Fact]
    public void MessageRequest_UsesMaxReasoning_SearchTool_Budget()
        {
        var messages = new[]
        {
            new ChatMessage("system", "instruction"),
            new ChatMessage("user", "Summarize today's AI news."),
        };
        var json = OpenRouterRequestBuilder.BuildRequestJson(TestLlm.Provider(), TestLlm.Execution(), messages);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("deepseek/deepseek-v4.1-flash", root.GetProperty("model").GetString());
        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("max", reasoning.GetProperty("effort").GetString());
        Assert.True(reasoning.GetProperty("exclude").GetBoolean());
        Assert.Equal(131072, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));

        var tools = root.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        var tool = tools[0];
        Assert.Equal("openrouter:web_search", tool.GetProperty("type").GetString());
        var parameters = tool.GetProperty("parameters");
        Assert.Equal("exa", parameters.GetProperty("engine").GetString());
        Assert.Equal(5, parameters.GetProperty("max_results").GetInt32());
        Assert.Equal(10, parameters.GetProperty("max_total_results").GetInt32());
        Assert.Equal(2, parameters.GetProperty("max_uses").GetInt32());
        Assert.Equal(2, root.GetProperty("max_tool_calls").GetInt32());

        Assert.DoesNotContain("plugins", json);
        Assert.DoesNotContain(":online", json);
        var wire = root.GetProperty("messages");
        Assert.Equal(2, wire.GetArrayLength());
        Assert.Equal("system", wire[0].GetProperty("role").GetString());
        Assert.Equal("instruction", wire[0].GetProperty("content").GetString());
        Assert.Equal("user", wire[1].GetProperty("role").GetString());
    }

    [Fact]
    public void Request_NeverDisablesSearchSilently()
        {
        var provider = TestLlm.Provider() with { SearchEnabled = false };
        Assert.Throws<InvalidOperationException>(() =>
            OpenRouterRequestBuilder.BuildRequestJson(provider, TestLlm.Execution(),
    [new ChatMessage("user", "hi")]));
    }

    [Fact]
    public void ParseResponse_ExtractsAnswer_Sources_Usage()
        {
        var body = """
            {"choices": [{"message": {
              "content": "Here is the news.",
              "reasoning_content": "secret chain of thought",
              "annotations": [
                {"type": "url_citation", "url_citation": {"url": "https://a.example/x", "title": "A"}},
                {"type": "other", "url_citation": {"url": "https://b.example/y", "title": "B"}},
                {"type": "url_citation", "url_citation": {"url": "ftp://c.example/z", "title": "C"}}
              ]}}],
             "usage": {"prompt_tokens": 12, "completion_tokens": 34}}
            """;
        var result = OpenRouterResponseReader.ReadResponse(body, TestLlm.Provider(), 131072);
        Assert.Equal("Here is the news.", result.AnswerText);
        Assert.DoesNotContain("secret chain", result.AnswerText);
        Assert.Single(result.Sources);
        Assert.Equal("https://a.example/x", result.Sources[0].Url);
        Assert.Equal(12, result.Usage.PromptTokens);
        Assert.Equal(34, result.Usage.CompletionTokens);
        Assert.True(result.SearchUsed);
    }

    [Fact]
    public void ParseResponse_CapsSources_AtMaxTotal()
        {
        var annotations = string.Join(",", Enumerable.Range(0, 15).Select(i =>
            $"{{\"type\": \"url_citation\", \"url_citation\": {{\"url\": \"https://e.example/{i}\", \"title\": \"T{i}\"}}}}"));
        var body = $"{{\"choices\": [{{\"message\": {{\"content\": \"ok\", \"annotations\": [{annotations}]}}}}]}}";
        var result = OpenRouterResponseReader.ReadResponse(body, TestLlm.Provider(), 131072);
        Assert.Equal(10, result.Sources.Count);
    }

    [Fact]
    public void ParseResponse_EmptyContent_IsExecutionFailure()
        {
        var ex = Assert.Throws<LlmExecutionException>(() =>
            OpenRouterResponseReader.ReadResponse(
                """{"choices": [{"message": {"content": "   "}}]}""", TestLlm.Provider(), 131072));
        Assert.Equal(LlmFailureKind.EmptyResponse, ex.Kind);
    }
    private static OpenRouterLlmExecutor WithHandler(ScriptHandler h) =>
        new(new HttpClient(h), TestLlm.Provider(), TestLlm.Execution(), "test-key");

    [Fact]
    public async Task Success_PostsToChatCompletions_WithBearerAuth()
        {
        var h = new ScriptHandler();
        var ex = WithHandler(h);
        var result = await ex.ExecuteAsync(new LlmRequest([new ChatMessage("user", "hi")], 131072));
        Assert.Equal("ok", result.AnswerText);
        Assert.EndsWith("/chat/completions", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", h.LastRequest.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task RateLimited_MapsTransient_WithRetryAfter()
        {
        var h = new ScriptHandler();
        h.Responder = _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "45");
            return response;
        };
        var ex = WithHandler(h);
        var thrown = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            ex.ExecuteAsync(new LlmRequest([new ChatMessage("user", "hi")], 131072)));
        Assert.Equal(LlmFailureKind.Transient, thrown.Kind);
        Assert.Equal(TimeSpan.FromSeconds(45), thrown.RetryAfter);
    }

    [Fact]
    public async Task Unauthorized_IsPermanent_NoRetry()
        {
        var h = new ScriptHandler();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var ex = WithHandler(h);
        var thrown = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            ex.ExecuteAsync(new LlmRequest([new ChatMessage("user", "hi")], 131072)));
        Assert.Equal(LlmFailureKind.Permanent, thrown.Kind);
        Assert.False(GenerationPolicy.ShouldRetry(thrown.Kind, 1));
    }


    [Fact]
    public async Task Stream_LengthIsTerminalAndBoundIsEnforced()
    {
        var opts = TestLlm.Provider();
        var lengthStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var length = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            OpenRouterStreamReader.ReadStreamAsync(lengthStream, opts, 131072));
        Assert.Equal(LlmFailureKind.AnswerIncomplete, length.Kind);
        Assert.Equal("answer_incomplete", length.Summary);

        var bigStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"0123456789ABCDEF\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var bound = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            OpenRouterStreamReader.ReadStreamAsync(bigStream, opts, 10));
        Assert.Equal(LlmFailureKind.SourceLimit, bound.Kind);

        var ok = OpenRouterResponseReader.ReadResponse(
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\" hi \"}}]}", opts, 131072);
        Assert.Equal("hi", ok.AnswerText);
        var tooLong = Assert.Throws<LlmExecutionException>(() =>
            OpenRouterResponseReader.ReadResponse(
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"" +
                new string('z', 11) + "\"}}]}", opts, 10));
        Assert.Equal(LlmFailureKind.SourceLimit, tooLong.Kind);
    }

    [Fact]
    public async Task Stream_WhitespaceFloodStaysBounded()
    {
        var opts = TestLlm.Provider();
        var flood = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + new string(' ', 1_000_001) +
            "\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var result = await OpenRouterStreamReader.ReadStreamAsync(flood, opts, 131072);
        Assert.Equal("x", result.AnswerText);
    }

    [Fact]
    public async Task Stream_ContentAfterExcessWhitespaceIsSourceLimit()
    {
        var opts = TestLlm.Provider();
        var flood = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + new string(' ', 131073) +
            "\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"y\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            OpenRouterStreamReader.ReadStreamAsync(flood, opts, 131072));
        Assert.Equal(LlmFailureKind.SourceLimit, ex.Kind);
        Assert.Equal("answer_source_limit", ex.Summary);
    }
}
