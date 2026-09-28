// Telegram transport: one typed payload per call, error classification, payload shapes, and chunking helpers.
using System.Net;
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Bot;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.Tests;

public sealed class TelegramTransportTests
{
    private sealed class TelegramScriptHandler : HttpMessageHandler
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

    private static TelegramBotSender Sender(TelegramScriptHandler handler) =>
        new(new HttpClient(handler), new TelegramOptions("fake", "unused"));

    private static JsonDocument Body(TelegramScriptHandler handler, int index = 0) =>
        JsonDocument.Parse(handler.Requests[index].Body);


    [Fact]
    public async Task MarkdownPayload_SendsOneNativeMessageUnchanged()
    {
        var http = new TelegramScriptHandler();
        const string answer = "## Progress\n\n- 🟢 Ready\n- Review the [report](https://example.org/report)";
        var id = await Sender(http).SendAsync(123456789,
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
        var http = new TelegramScriptHandler();
        const string text = "**This stays literal** <tag> & text\nnewline";
        await Sender(http).SendAsync(7,
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
        var http = new TelegramScriptHandler();
        await Sender(http).SendAsync(7,
            new TelegramPayload(TelegramPayloadKind.LiteralPlain, "literal segment"));
        Assert.Equal(1, http.Calls);
        Assert.Equal("/botfake/sendMessage", http.Requests[0].Path);
        using var body = Body(http);
        Assert.Equal("literal segment", body.RootElement.GetProperty("text").GetString());
    }


    [Fact]
    public async Task TablePayload_SendsOneTableBlock()
    {
        var http = new TelegramScriptHandler();
        const string cells = """[[{"text":"ID","is_header":true}],[{"text":"a31f9c","colspan":2}]]""";
        var id = await Sender(http).SendAsync(7,
            new TelegramPayload(TelegramPayloadKind.Table, cells, null, "Tasks"));
        Assert.Equal(901, id);
        Assert.Equal(1, http.Calls);
        Assert.Equal("/botfake/sendRichMessage", http.Requests[0].Path);
        using var body = Body(http);
        var blocks = body.RootElement.GetProperty("rich_message").GetProperty("blocks");
        Assert.Equal(2, blocks.GetArrayLength());
        var table = blocks[1];
        Assert.Equal("table", table.GetProperty("type").GetString());
        Assert.True(table.GetProperty("is_compact").GetBoolean());
        Assert.True(table.GetProperty("cells")[0][0].GetProperty("is_header").GetBoolean());
        Assert.Equal("a31f9c", table.GetProperty("cells")[1][0].GetProperty("text").GetString());
        Assert.Equal(2, table.GetProperty("cells")[1][0].GetProperty("colspan").GetInt32());
        Assert.Equal("Tasks", blocks[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task DocumentPayload_SendsMultipartAttachmentWhole()
    {
        var http = new TelegramScriptHandler();
        var filler = new string('p', 1000);
        var json = "{\"prompt\": \"" + filler + "\"}";
        var id = await Sender(http).SendAsync(7,
            new TelegramPayload(TelegramPayloadKind.Document, json, "task-abc.json", "Task abc attached."));
        Assert.Equal(901, id);
        Assert.Equal(1, http.Calls);
        Assert.Equal("/botfake/sendDocument", http.Requests[0].Path);
        var body = http.Requests[0].Body;
        Assert.Contains("task-abc.json", body, StringComparison.Ordinal);
        Assert.Contains("chat_id", body, StringComparison.Ordinal);
        Assert.Contains("Task abc attached.", body, StringComparison.Ordinal);
        Assert.Contains(filler, body, StringComparison.Ordinal);
    }


    [Fact]
    public async Task SuccessWithoutMessageId_IsAmbiguousFailure()
    {
        var http = new TelegramScriptHandler
        {
            Responder = _ => Ok(0),
        };
        var sender = Sender(http);
        var markdown = await Assert.ThrowsAsync<TelegramSendException>(() =>
            sender.SendAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Same(typeof(TelegramSendException), markdown.GetType());
        Assert.Equal(TelegramDisposition.Transient,
            TelegramErrorClassifier.ClassifyException(markdown).Disposition);
        http.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{}}")
        };
        await Assert.ThrowsAsync<TelegramSendException>(() =>
            sender.SendAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Equal(2, http.Calls);
    }


    [Fact]
    public async Task ContentRejection_RetainsBothStatuses()
    {
        var http = new TelegramScriptHandler
        {
            Responder = _ => Fail(HttpStatusCode.BadRequest,
                "{\"ok\":false,\"error_code\":400," +
                "\"description\":\"Bad Request: can't parse entities: ...\"}")
        };
        var ex = await Assert.ThrowsAsync<TelegramSendException>(() =>
            Sender(http).SendAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
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
        var http = new TelegramScriptHandler { Responder = _ => Fail(status, body) };
        var ex = await Assert.ThrowsAsync<TelegramUnknownMethodException>(() =>
            Sender(http).SendAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Equal(TelegramDisposition.UnknownMethod,
            TelegramErrorClassifier.ClassifyException(ex).Disposition);
        Assert.Equal(1, http.Calls);
    }


    [Fact]
    public async Task OrdinaryNotFound_IsNotAnUnknownMethod()
    {
        var http = new TelegramScriptHandler
        {
            Responder = _ => Fail(HttpStatusCode.BadRequest,
                "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message not found\"}")
        };
        var ex = await Assert.ThrowsAsync<TelegramSendException>(() =>
            Sender(http).SendAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Same(typeof(TelegramSendException), ex.GetType());
        Assert.Equal(TelegramDisposition.TerminalFailure,
            TelegramErrorClassifier.ClassifyException(ex).Disposition);
    }


    [Fact]
    public async Task RateLimit_HonorsRetryAfter()
    {
        var http = new TelegramScriptHandler
        {
            Responder = _ => Fail((HttpStatusCode)429,
                "{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\"," +
                "\"parameters\":{\"retry_after\":30}}")
        };
        var ex = await Assert.ThrowsAsync<TelegramSendException>(() =>
            Sender(http).SendAsync(7, new TelegramPayload(TelegramPayloadKind.Markdown, "hi")));
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Equal(TelegramDisposition.RateLimited,
            TelegramErrorClassifier.ClassifyException(ex).Disposition);
    }


    [Fact]
    public async Task Transport_SendsExactlyOneRequestPerCall()
    {
        var http = new TelegramScriptHandler();
        var sender = Sender(http);
        var first = await sender.SendAsync(7,
            new TelegramPayload(TelegramPayloadKind.LiteralPlain, "one"));
        var second = await sender.SendAsync(7,
            new TelegramPayload(TelegramPayloadKind.LiteralPlain, "two"));
        Assert.Equal(901, first);
        Assert.Equal(901, second);
        Assert.Equal(2, http.Calls);
    }



    [Theory]
    [InlineData(400, 400, "Bad Request: can't parse entities: ...", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "Bad Request: can’t parse rich message", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "can't parse markdown", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "Bad Request: message is too long", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "Bad Request: text is too long", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "MESSAGE_TOO_LONG", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "too many blocks", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "Too many columns", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "nesting too deep", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(200, 400, "nesting limit exceeded", TelegramDisposition.ContentRejection, "content_rejected")]
    [InlineData(400, 400, "Bad Request: message not found", TelegramDisposition.TerminalFailure, "bad_request")]
    [InlineData(404, 404, "Not Found", TelegramDisposition.UnknownMethod, "unknown_method")]
    [InlineData(200, -1, "unknown method: sendRichMessage", TelegramDisposition.UnknownMethod, "unknown_method")]
    [InlineData(200, 400, "method not found", TelegramDisposition.UnknownMethod, "unknown_method")]
    [InlineData(403, 403, "Forbidden: bot was blocked by the user", TelegramDisposition.PermanentRecipient, "recipient_failure")]
    [InlineData(200, 400, "Bad Request: chat not found", TelegramDisposition.PermanentRecipient, "recipient_failure")]
    [InlineData(404, 404, "chat not found", TelegramDisposition.PermanentRecipient, "recipient_failure")]
    [InlineData(429, 429, "Too Many Requests", TelegramDisposition.RateLimited, "rate_limited")]
    [InlineData(401, 401, "Unauthorized", TelegramDisposition.TerminalFailure, "unauthorized")]
    [InlineData(200, 400, "Bad Request: wrong file identifier", TelegramDisposition.TerminalFailure, "bad_request")]
    [InlineData(500, 500, "Internal Server Error", TelegramDisposition.Transient, "transient")]
    [InlineData(408, -1, "Request Timeout", TelegramDisposition.Transient, "transient")]
    [InlineData(-1, -1, "connection reset", TelegramDisposition.Transient, "transient")]
    public void ClassifiesTelegramErrors(
        int http, int api, string desc, TelegramDisposition expected, string category)
    {
        int? h = http < 0 ? null : http;
        int? a = api < 0 ? null : api;
        var result = TelegramErrorClassifier.Classify(h, a, desc);
        Assert.Equal(expected, result.Disposition);
        Assert.Equal(category, result.Category);
    }



    [Fact]
    public void MarkdownPayload_ArrivesUnchangedWithoutWrappers()
    {
        const string answer = "## Progress\n\n- 🟢 Ready\n- Review the [report](https://example.org/report)";
        var json = TelegramRichMessage.BuildMarkdownRequestJson(123456789, answer);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(123456789, root.GetProperty("chat_id").GetInt64());
        Assert.Equal(answer, root.GetProperty("rich_message").GetProperty("markdown").GetString());
        Assert.False(root.TryGetProperty("parse_mode", out _));
        Assert.False(root.GetProperty("rich_message").TryGetProperty("html", out _));
        Assert.False(root.GetProperty("rich_message").TryGetProperty("blocks", out _));
        Assert.False(root.TryGetProperty("entities", out _));
    }


    [Fact]
    public void LiteralRichPayload_IsOneParagraphBlockRoundTrippingExactly()
    {
        const string text = "**This stays literal** <tag> & text\nsecond line \"quoted\"";
        var json = TelegramRichMessage.BuildLiteralRichRequestJson(7, text);
        using var doc = JsonDocument.Parse(json);
        var block = Assert.Single(doc.RootElement.GetProperty("rich_message").GetProperty("blocks").EnumerateArray());
        Assert.Equal("paragraph", block.GetProperty("type").GetString());
        Assert.Equal(text, block.GetProperty("text").GetString());
    }

    [Fact]
    public void PlainText_PassesThrough()
        {
        Assert.Equal("hello", TelegramPromptNormalizer.NormalizePrompt("hello", null));
    }

    [Fact]
    public void RichMessage_PreservesOrder_Lists_Tables_Code_Links()
        {
        var rich = """
            {"blocks": [
              {"type": "paragraph", "text": "Morning briefing"},
              {"type": "list", "ordered": false, "items": ["alpha", "beta"]},
              {"type": "table", "cells": [[{"text": "a"}, {"text": "b"}]]},
              {"type": "code", "language": "python", "code": "print(1)"},
              {"type": "link", "text": "source", "url": "https://s.example/x"}
            ]}
            """;
        var text = TelegramPromptNormalizer.NormalizePrompt(null, rich)!;
        Assert.Contains("Morning briefing", text);
        Assert.Contains("- alpha", text);
        Assert.Contains("| a | b |", text);
        Assert.Contains("```python", text);
        Assert.Contains("source (https://s.example/x)", text);
        Assert.True(text.IndexOf("Morning", StringComparison.Ordinal) <
            text.IndexOf("source", StringComparison.Ordinal));
    }

    [Fact]
    public void RichMessage_InvalidJson_ReturnsNull()
        {
        Assert.Null(TelegramPromptNormalizer.NormalizePrompt(null, "{nope"));
    }

    [Fact]
    public void Split_NeverSeparatesSurrogatePairs()
        {
        var text = "a" + string.Concat(Enumerable.Repeat("🌍", 100)) + "b";
        var parts = TextLimits.SplitByChars(text, 10);
        Assert.All(parts, p => Assert.True(TextLimits.CountChars(p) <= 10));
        Assert.Equal(text, string.Concat(parts));
    }

    [Fact]
    public void BlockType_NeverLeaksIntoPrompt_CommandDispatchIntact()
        {
        var rich = """{"type": "paragraph", "text": "/create 0 0 9 * * * Water the plants"}""";
        Assert.Equal("/create 0 0 9 * * * Water the plants",
            TelegramPromptNormalizer.NormalizePrompt(null, rich));
    }

    [Fact]
    public void InlineSegments_PreserveOrderAndSpacing()
        {
        var rich = """{"type": "paragraph", "segments": ["Hello", " ", {"text": "world", "bold": true}, "!"]}""";
        Assert.Equal("Hello world!",
            TelegramPromptNormalizer.NormalizePrompt(null, rich));
    }

    [Fact]
    public void UnknownType_SkipsDiscriminator_KeepsContent()
        {
        var rich = """{"type": "callout", "text": "note this"}""";
        Assert.Equal("note this", TelegramPromptNormalizer.NormalizePrompt(null, rich));
    }

    [Fact]
    public void SendLiteralRichMessage_UsesBlocksObject()
        {
        var json = TelegramRichMessage.BuildLiteralRichRequestJson(777, "hi");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(777, root.GetProperty("chat_id").GetInt64());
        Assert.False(root.TryGetProperty("text", out _));
        var rich = root.GetProperty("rich_message");
        var block = Assert.Single(rich.GetProperty("blocks").EnumerateArray());
        Assert.Equal("paragraph", block.GetProperty("type").GetString());
        Assert.Equal("hi", block.GetProperty("text").GetString());
    }
}
