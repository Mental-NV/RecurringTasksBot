using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class OpenRouterTransportTests
{
    private static readonly LlmPrompt Prompt = new("test", DateTime.UnixEpoch, DateTime.UnixEpoch);

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
        Assert.Throws<LlmExecutionException>(() => OpenRouterLlmExecutor.ParseResponse(
            """{"choices":[{"message":{"content":"partial"},"finish_reason":"error","error":{"code":502}}]}""",
            TestLlm.Options()));
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
        var shortRequest = TestLlm.Options() with { RequestTimeout = TimeSpan.FromMilliseconds(50) };
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => Executor(handler, shortRequest).ExecuteAsync(Prompt));
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

    private static OpenRouterLlmExecutor Executor(Handler handler, LlmOptions? opts = null) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, opts ?? TestLlm.Options(), "fake");

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
}
