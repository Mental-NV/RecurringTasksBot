// Phase 3 sender tests: one typed request per call, exact wire shapes,
// strict success acknowledgement, structured rejections, and the legacy
// single-message fallback. All HTTP mocked; no network.
using System.Net;
using System.Text.Json;
using RecurringTasksBot;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class Phase3SenderTests
{
    private sealed class ScriptHandler : HttpMessageHandler
    {
        public int Calls;
        public readonly List<(string Path, string Body)> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ => Ok(901);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Requests.Add((request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(ct)));
            return Responder(request);
        }
    }

    private static HttpResponseMessage Ok(long id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"ok\":true,\"result\":{{\"message_id\":{id}}}}}")
    };

    private static HttpResponseMessage Fail(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body)
    };

    private static TelegramBotSender Sender(ScriptHandler handler) =>
        new(new HttpClient(handler), new BotOptions("unused", "unused", "fake", "unused"));

    private static JsonDocument Body(ScriptHandler handler, int index = 0) =>
        JsonDocument.Parse(handler.Requests[index].Body);

    [Fact]
    public async Task MarkdownPayload_SendsOneNativeMessageUnchanged()
    {
        var http = new ScriptHandler();
        const string answer = "## Progress\n\n- 🟢 Ready\n- Review the [report](https://example.org/report)";
        var id = await Sender(http).SendPayloadAsync(123456789,
            new TelegramPayload(TelegramPayloadKind.Markdown, answer));
        Assert.Equal(901, id);
        Assert.Equal(1, http.Calls);
        Assert.Equal("/botfake/sendRichMessage", http.Requests[0].Path);
        using var body = Body(http);
        Assert.Equal(123456789, body.RootElement.GetProperty("chat_id").GetInt64());
        Assert.Equal(answer, body.RootElement.GetProperty("rich_message").GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task LiteralRichPayload_SendsOneParagraphBlock()
    {
        var http = new ScriptHandler();
        const string text = "**This stays literal** <tag> & text\nnewline";
        await Sender(http).SendPayloadAsync(7,
            new TelegramPayload(TelegramPayloadKind.LiteralRich, text));
        Assert.Equal(1, http.Calls);
        using var body = Body(http);
        var block = Assert.Single(
            body.RootElement.GetProperty("rich_message").GetProperty("blocks").EnumerateArray());
        Assert.Equal("paragraph", block.GetProperty("type").GetString());
        Assert.Equal(text, block.GetProperty("text").GetString());
    }

    [Fact]
    public async Task PlainPayload_SendsOneRegularMessage()
    {
        var http = new ScriptHandler();
        await Sender(http).SendPayloadAsync(7,
            new TelegramPayload(TelegramPayloadKind.LiteralPlain, "literal segment"));
        Assert.Equal(1, http.Calls);
        Assert.Equal("/botfake/sendMessage", http.Requests[0].Path);
        using var body = Body(http);
        Assert.Equal("literal segment", body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SuccessWithoutMessageId_IsAmbiguousFailure()
    {
        var http = new ScriptHandler
        {
            Responder = _ => Ok(0),
        };
        var sender = Sender(http);
        var markdown = await Assert.ThrowsAsync<TelegramSendException>(() =>
            sender.SendPayloadAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Same(typeof(TelegramSendException), markdown.GetType());
        Assert.Equal(TelegramDisposition.Transient,
            TelegramErrorClassifier.ClassifyException(markdown).Disposition);
        http.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{}}")
        };
        await Assert.ThrowsAsync<TelegramSendException>(() =>
            sender.SendPayloadAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task ContentRejection_RetainsBothStatuses()
    {
        var http = new ScriptHandler
        {
            Responder = _ => Fail(HttpStatusCode.BadRequest,
                "{\"ok\":false,\"error_code\":400," +
                "\"description\":\"Bad Request: can't parse entities: ...\"}")
        };
        var ex = await Assert.ThrowsAsync<TelegramSendException>(() =>
            Sender(http).SendPayloadAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Same(typeof(TelegramSendException), ex.GetType());
        Assert.Equal(400, ex.HttpStatusCode);
        Assert.Equal(400, ex.ApiErrorCode);
        Assert.Equal(TelegramDisposition.ContentRejection,
            TelegramErrorClassifier.ClassifyException(ex).Disposition);
        Assert.Equal(1, http.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"ok\":false,\"error_code\":404,\"description\":\"Not Found\"}")]
    [InlineData(HttpStatusCode.OK, "{\"ok\":false,\"error_code\":400,\"description\":\"unknown method: sendRichMessage\"}")]
    public async Task UnknownMethod_SurfacesDistinctlyAfterOneCall(HttpStatusCode status, string body)
    {
        var http = new ScriptHandler { Responder = _ => Fail(status, body) };
        var ex = await Assert.ThrowsAsync<TelegramUnknownMethodException>(() =>
            Sender(http).SendPayloadAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Equal(TelegramDisposition.UnknownMethod,
            TelegramErrorClassifier.ClassifyException(ex).Disposition);
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task OrdinaryNotFound_IsNotAnUnknownMethod()
    {
        var http = new ScriptHandler
        {
            Responder = _ => Fail(HttpStatusCode.BadRequest,
                "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message not found\"}")
        };
        var ex = await Assert.ThrowsAsync<TelegramSendException>(() =>
            Sender(http).SendPayloadAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Same(typeof(TelegramSendException), ex.GetType());
        Assert.Equal(TelegramDisposition.TerminalFailure,
            TelegramErrorClassifier.ClassifyException(ex).Disposition);
    }

    [Fact]
    public async Task RateLimit_HonorsRetryAfter()
    {
        var http = new ScriptHandler
        {
            Responder = _ => Fail((HttpStatusCode)429,
                "{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\"," +
                "\"parameters\":{\"retry_after\":30}}")
        };
        var ex = await Assert.ThrowsAsync<TelegramSendException>(() =>
            Sender(http).SendPayloadAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Equal(TelegramDisposition.RateLimited,
            TelegramErrorClassifier.ClassifyException(ex).Disposition);
    }

    [Fact]
    public async Task LegacyAdapter_FallsBackToBoundedPlainMessages()
    {
        var ids = new Queue<long>([555, 556]);
        var http = new ScriptHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/sendRichMessage", StringComparison.Ordinal)
                ? Fail(HttpStatusCode.NotFound,
                    "{\"ok\":false,\"error_code\":404,\"description\":\"unknown method\"}")
                : Ok(ids.Dequeue())
        };
        var part = "<b>" + new string('x', 5000) + "</b>";
        var id = await Sender(http).SendRichTextAsync(9, part);
        Assert.Equal(555, id);
        Assert.Equal(3, http.Calls);
        Assert.Equal("/botfake/sendMessage", http.Requests[1].Path);
        Assert.Equal("/botfake/sendMessage", http.Requests[2].Path);
        using var first = Body(http, 1);
        Assert.Equal(new string('x', 4096), first.RootElement.GetProperty("text").GetString());
        using var second = Body(http, 2);
        Assert.Equal(new string('x', 904), second.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task PayloadAdapter_RoutesPlainTextThroughLegacySend()
    {
        var fake = new FakeTelegramSender();
        await ((ITelegramSender)fake).SendPayloadAsync(3, new TelegramPayload(TelegramPayloadKind.LiteralPlain, "hi"));
        var payload = Assert.Single(fake.Payloads);
        Assert.Equal(3L, payload.ChatId);
        Assert.Equal(TelegramPayloadKind.LiteralPlain, payload.Kind);
        Assert.Equal("hi", payload.Content);
        Assert.Empty(fake.Sent);
    }
}
