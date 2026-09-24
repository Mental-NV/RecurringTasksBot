using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RecurringTasksBot;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public async Task RealSender_PostsTelegramHtmlContract()
    {
        var http = new CaptureHandler();
        var sender = new TelegramBotSender(new HttpClient(http), new BotOptions("unused", "unused", "fake", "unused"));
        Assert.Equal(91, await sender.SendRichTextAsync(42, "<b>hello</b>"));
        using var body = JsonDocument.Parse(http.Body!);
        Assert.Equal("/botfake/sendRichMessage", http.Uri!.AbsolutePath);
        Assert.Equal(42, body.RootElement.GetProperty("chat_id").GetInt64());
        Assert.False(body.RootElement.TryGetProperty("text", out _));
        var rich = body.RootElement.GetProperty("rich_message");
        Assert.Single(rich.EnumerateObject());
        Assert.Equal("<b>hello</b>", rich.GetProperty("html").GetString());
    }

    [Fact]
    public async Task RawRichUpdate_PreservesInlineCommandAcrossFormatting()
    {
        var update = TelegramUpdateParser.Parse("""
            {"update_id":10,"message":{"from":{"id":42},"chat":{"id":42,"type":"private"},
            "rich_message":{"blocks":[{"type":"paragraph","text":["/cre",{"type":"bold","text":"ate"}," 0 0 9 * * * Brief me"]}]}}}
            """);
        Assert.Equal("/create 0 0 9 * * * Brief me", update!.Text);
        var ops = new FakeOperationStore();
        var processor = new UpdateProcessor(ops, new FakeReceiptStore(), new FakeOrchestrations(), new FakeTelegramSender());
        Assert.Equal(200, (await processor.ProcessAsync(true, update, DateTimeOffset.UtcNow)).StatusCode);
        Assert.Equal("Brief me", Assert.Single(await ops.ListOwnedAsync("42")).Text);
    }

    [Fact]
    public async Task RawReplyUpdate_AcceptsFullLengthRichPrompt()
    {
        var prompt = string.Concat(Enumerable.Repeat("🌍", 32768));
        var json = JsonSerializer.Serialize(new
        {
            update_id = 11,
            message = new
            {
                from = new { id = 42 }, chat = new { id = 42, type = "private" }, text = "/create 0 0 9 * * *",
                reply_to_message = new
                {
                    from = new { id = 42 }, chat = new { id = 42, type = "private" },
                    rich_message = new { blocks = new[] { new { type = "paragraph", text = prompt } } }
                }
            }
        });
        var ops = new FakeOperationStore();
        var receipts = new FakeReceiptStore();
        var processor = new UpdateProcessor(ops, receipts, new FakeOrchestrations(), new FakeTelegramSender());
        Assert.Equal(200, (await processor.ProcessAsync(true, TelegramUpdateParser.Parse(json), DateTimeOffset.UtcNow)).StatusCode);
        Assert.Equal(prompt, Assert.Single(await ops.ListOwnedAsync("42")).Text);
        Assert.True(Encoding.Unicode.GetByteCount((await receipts.GetAsync("42", 11))!.Command) <= 65536);
    }

    [Fact]
    public void RealRichTextShapes_PreserveLinksTablesAndCode()
    {
        var prompt = RichNormalizer.NormalizePrompt(null, """
            {"blocks":[
              {"type":"paragraph","text":["Read ",{"type":"url","url":"https://example.com","text":[{"type":"bold","text":"this"}," page"]},"."]},
              {"type":"table","cells":[[{"text":["A",{"type":"italic","text":"B"}]},{"text":"C"}]]},
              {"type":"pre","language":"text","text":["a", "\n\n\nb"]}
            ]}
            """);
        Assert.Equal("Read this page (https://example.com).\n\n| AB | C |\n\n```text\na\n\n\nb\n```", prompt);
    }

    [Theory]
    [InlineData("bold")]
    [InlineData("code")]
    [InlineData("entities")]
    public void FullLengthFormattedAnswer_IsLosslessAndEveryPartIsValid(string kind)
    {
        var content = kind == "entities" ? string.Concat(Enumerable.Repeat("&🌍", 16000)) : new string('z', 32764);
        var answer = kind == "bold" ? "**" + content + "**" : kind == "code" ? "`" + content + "`" : content;
        var parts = AnswerComposer.Compose("op1", DateTime.UnixEpoch, DateTime.UnixEpoch, answer, []);
        Assert.All(parts, part =>
        {
            var parsed = XElement.Parse("<root>" + part + "</root>", LoadOptions.PreserveWhitespace);
            Assert.InRange(TextLimits.CountChars(parsed.Value), 1, 32768);
        });
        var rendered = string.Concat(parts.Select(RichMessageParts.ToPlainText));
        Assert.EndsWith(content, rendered);
        Assert.DoesNotContain("truncated", rendered);
    }

    [Fact]
    public void HtmlSplit_UsesFullRenderedLimit_AndPreservesLinks()
    {
        var text = new string('&', 32768);
        var html = "<a href=\"https://example.com/?a=1&amp;b=2\"><b>" + RichMessageParts.Escape(text) + "</b></a>";
        Assert.Single(RichMessageParts.Split(html)); // markup/escaping do not consume the text allowance
        var parts = RichMessageParts.Split(html, 1000);
        Assert.Equal(text, string.Concat(parts.Select(RichMessageParts.ToPlainText)));
        Assert.All(parts, part =>
        {
            var element = XElement.Parse(part);
            Assert.Equal("https://example.com/?a=1&b=2", element.Attribute("href")!.Value);
            Assert.InRange(TextLimits.CountChars(element.Value), 1, 1000);
        });
    }

    [Fact]
    public void LinkFormatting_DoesNotSwallowFollowingText()
    {
        var html = RichValidator.ToSafeHtml("[first](https://one.example) then [second](https://two.example)");
        var root = XElement.Parse("<root>" + html + "</root>");
        Assert.Equal(new[] { "first", "second" }, root.Elements("a").Select(e => e.Value));
        Assert.Equal("first then second", root.Value);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":91}}")
            };
        }
    }
}

public sealed class OccurrenceLeaseTests
{
    private static readonly DateTime Scheduled = DateTime.UnixEpoch.AddDays(20000);

    private static (DeliveryHandler Handler, FakeDeliveryStore Store, FakeLlmExecutor Llm, FakeTelegramSender Sender) New()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1", OperationStatus.Active));
        var store = new FakeDeliveryStore();
        var llm = new FakeLlmExecutor();
        var sender = new FakeTelegramSender();
        return (new DeliveryHandler(ops, store, new FakePayloadStore(), sender, llm, TestLlm.Options(),
            TimeSpan.FromMilliseconds(10)), store, llm, sender);
    }

    [Fact]
    public async Task HigherRetryIndex_CannotStealLiveGeneration_AndHeartbeatRenews()
    {
        var (handler, store, llm, sender) = New();
        var answer = new TaskCompletionSource<LlmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.Handler = _ => answer.Task;
        var first = handler.AttemptOnceAsync("42", "op1", Scheduled, 0);
        try
        {
            var original = (await store.GetAsync("42", "op1", Scheduled))!;
            store.Now += TimeSpan.FromMinutes(8);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await store.GetAsync("42", "op1", Scheduled))!.UpdatedUtc == original.UpdatedUtc)
                await Task.Delay(10, timeout.Token);
            foreach (var index in new[] { 0, 1, 5, 50 })
                Assert.Equal(SingleAttemptOutcome.WaitingForClaim,
                    (await handler.AttemptOnceAsync("42", "op1", Scheduled, index)).Outcome);
            Assert.Single(llm.Calls);
            Assert.Empty(sender.Sent);
        }
        finally { answer.TrySetResult(FakeLlmExecutor.Answer("original")); }
        Assert.Equal(SingleAttemptOutcome.Sent, (await first).Outcome);
        Assert.Single(sender.Sent);
        Assert.Null((await store.GetAsync("42", "op1", Scheduled))!.ClaimId);
    }

    [Fact]
    public async Task DuplicateDuringDelivery_WaitsWithoutResendingPersistedAnswer()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1", OperationStatus.Active));
        var store = new FakeDeliveryStore();
        var llm = new FakeLlmExecutor();
        var sender = new PendingSender();
        var handler = new DeliveryHandler(ops, store, new FakePayloadStore(), sender, llm, TestLlm.Options());
        var first = handler.AttemptOnceAsync("42", "op1", Scheduled, 0);
        try
        {
            Assert.Equal(1, sender.Calls);
            Assert.Equal("generated", (await store.GetAsync("42", "op1", Scheduled))!.ExecutionStatus);
            Assert.Equal(SingleAttemptOutcome.WaitingForClaim,
                (await handler.AttemptOnceAsync("42", "op1", Scheduled, 5)).Outcome);
            Assert.Single(llm.Calls);
            Assert.Equal(1, sender.Calls);
        }
        finally { sender.Completion.TrySetResult(1); }
        Assert.Equal(SingleAttemptOutcome.Sent, (await first).Outcome);
    }

    private sealed class PendingSender : ITelegramSender
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<long> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> SendTextAsync(long chatId, string text, CancellationToken ct = default)
        {
            Calls++;
            return Completion.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task ReleasedLease_AllowsSameIndexRecovery_WithoutRegeneration()
    {
        var (handler, store, llm, sender) = New();
        sender.EnqueueThrow(new TelegramSendException(500, "retry"));
        Assert.Equal(SingleAttemptOutcome.NeedRetry, (await handler.AttemptOnceAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Null((await store.GetAsync("42", "op1", Scheduled))!.ClaimId);
        Assert.Equal(SingleAttemptOutcome.Sent, (await handler.AttemptOnceAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Single(llm.Calls);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task ExpiredOwner_CannotWriteOrReleaseItsSuccessor()
    {
        var (_, store, _, _) = New();
        Assert.True(await store.TryClaimAsync("42", "op1", Scheduled, "old"));
        var old = (await store.GetAsync("42", "op1", Scheduled))!;
        store.Now += TimeSpan.FromMinutes(16);
        Assert.True(await store.TryClaimAsync("42", "op1", Scheduled, "new"));
        await Assert.ThrowsAsync<ClaimLostException>(() => store.UpsertAsync(old with { Status = "sent" }));
        Assert.False(await store.RenewClaimAsync("42", "op1", Scheduled, "old"));
        await store.ReleaseClaimAsync("42", "op1", Scheduled, "old");
        Assert.Equal("new", (await store.GetAsync("42", "op1", Scheduled))!.ClaimId);
    }

    [Fact]
    public async Task CrashClaim_IsRecoveredAfterExpiry()
    {
        var (handler, store, llm, sender) = New();
        await store.TryClaimAsync("42", "op1", Scheduled, "crashed");
        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, (await handler.AttemptOnceAsync("42", "op1", Scheduled, 0)).Outcome);
        store.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(SingleAttemptOutcome.Sent, (await handler.AttemptOnceAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Single(llm.Calls);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task LostLease_DiscardsLlmResult()
    {
        var (handler, store, llm, sender) = New();
        llm.Handler = _ =>
        {
            store.LoseOnRenewal = true;
            return Task.FromResult(FakeLlmExecutor.Answer("must not send"));
        };
        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, (await handler.AttemptOnceAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void BusyWaits_DoNotExhaustDurableRetries()
    {
        var busy = new SingleAttemptResult(SingleAttemptOutcome.WaitingForClaim, 0, TimeSpan.FromSeconds(30), null);
        var index = OccurrenceExecution.MaxCombinedRetriesAfterInitial;
        for (var i = 0; i < 100; i++)
        {
            Assert.True(OccurrenceExecution.ShouldRetry(busy, index));
            index = OccurrenceExecution.NextAttemptIndex(busy, index);
        }
        Assert.Equal(OccurrenceExecution.MaxCombinedRetriesAfterInitial, index);
        Assert.False(OccurrenceExecution.ShouldRetry(busy with { Outcome = SingleAttemptOutcome.NeedRetry }, index));
    }
}
