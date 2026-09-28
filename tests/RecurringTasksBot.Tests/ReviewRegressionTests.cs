using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NodaTime;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Bot;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.Tests;

public sealed class ReviewRegressionTests
{
    [Fact]
    public async Task RawRichUpdate_PreservesInlineCommandAcrossFormatting()
    {
        var update = TelegramUpdateParser.Parse("""
            {"update_id":10,"message":{"from":{"id":42},"chat":{"id":42,"type":"private"},
            "rich_message":{"blocks":[{"type":"paragraph","text":["/cre",{"type":"bold","text":"ate"}," {\"schedule\": {\"cron\": \"0 0 9 * * *\"}, \"prompt\": \"Brief me\"}"]}]}}}
            """);
        Assert.Equal("/create {\"schedule\": {\"cron\": \"0 0 9 * * *\"}, \"prompt\": \"Brief me\"}", update!.Text);
        var tasks = new FakeTaskStore();
        var processor = new UpdateDispatcher(tasks, new FakeReceiptStore(), new FakeOrchestrations(), new FakeTelegramSender(), TaskDefaults.Default, new FakeOccurrenceRepository(new FakeOperationStore(), new FakeClock()));
        Assert.Equal(200, (await processor.ProcessAsync(true, update, DateTimeOffset.UtcNow)).StatusCode);
        Assert.Equal("Brief me", Assert.Single(await tasks.ListOwnedAsync("42")).Definition.Prompt);
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
                from = new { id = 42 }, chat = new { id = 42, type = "private" }, text = "/create {\"schedule\": {\"cron\": \"0 0 9 * * *\"}}",
                reply_to_message = new
                {
                    from = new { id = 42 }, chat = new { id = 42, type = "private" },
                    rich_message = new { blocks = new[] { new { type = "paragraph", text = prompt } } }
                }
            }
        });
        var tasks = new FakeTaskStore();
        var receipts = new FakeReceiptStore();
        var processor = new UpdateDispatcher(tasks, receipts, new FakeOrchestrations(), new FakeTelegramSender(), TaskDefaults.Default, new FakeOccurrenceRepository(new FakeOperationStore(), new FakeClock()));
        Assert.Equal(200, (await processor.ProcessAsync(true, TelegramUpdateParser.Parse(json), DateTimeOffset.UtcNow)).StatusCode);
        Assert.Equal(prompt, Assert.Single(await tasks.ListOwnedAsync("42")).Definition.Prompt);
        Assert.True(Encoding.Unicode.GetByteCount((await receipts.GetAsync("42", 11))!.Command) <= 65536);
    }

    [Fact]
    public void RealRichTextShapes_PreserveLinksTablesAndCode()
    {
        var prompt = TelegramPromptNormalizer.NormalizePrompt(null, """
            {"blocks":[
              {"type":"paragraph","text":["Read ",{"type":"url","url":"https://example.com","text":[{"type":"bold","text":"this"}," page"]},"."]},
              {"type":"table","cells":[[{"text":["A",{"type":"italic","text":"B"}]},{"text":"C"}]]},
              {"type":"pre","language":"text","text":["a", "\n\n\nb"]}
            ]}
            """);
        Assert.Equal("Read this page (https://example.com).\n\n| AB | C |\n\n```text\na\n\n\nb\n```", prompt);
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

    private static (ExecuteOccurrenceHandler Handler, FakeOccurrenceRepository Repo, FakeLlmExecutor Llm, FakeTelegramSender Sender, FakeClock Clock) New()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1", OperationStatus.Active));
        var llm = new FakeLlmExecutor();
        var sender = new FakeTelegramSender();
        var clock = new FakeClock();
        var repo = new FakeOccurrenceRepository(ops, clock);
        return (new ExecuteOccurrenceHandler(ops, repo, sender, llm, TestLlm.Execution(),
            TestLlm.ProviderName, TestLlm.ModelName, clock, TimeSpan.FromMilliseconds(10)), repo, llm, sender, clock);
    }

    [Fact]
    public async Task HigherRetryIndex_CannotStealLiveGeneration_AndHeartbeatRenews()
    {
        var (handler, repo, llm, sender, clock) = New();
        var answer = new TaskCompletionSource<LlmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.Responder = _ => answer.Task;
        var first = handler.ExecuteAttemptAsync("42", "op1", Scheduled, 0);
        try
        {
            var original = (await repo.GetReceiptAsync("42", "op1", Scheduled))!;
            clock.Now += TimeSpan.FromMinutes(8);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await repo.GetReceiptAsync("42", "op1", Scheduled))!.UpdatedUtc == original.UpdatedUtc)
                await Task.Delay(10, timeout.Token);
            foreach (var index in new[] { 0, 1, 5, 50 })
                Assert.Equal(SingleAttemptOutcome.WaitingForClaim,
                    (await handler.ExecuteAttemptAsync("42", "op1", Scheduled, index)).Outcome);
            Assert.Single(llm.Requests);
            Assert.Empty(sender.Payloads);
        }
        finally { answer.TrySetResult(FakeLlmExecutor.Answer("original")); }
        Assert.Equal(SingleAttemptOutcome.Sent, (await first).Outcome);
        Assert.Single(sender.Payloads);
        Assert.Null((await repo.GetReceiptAsync("42", "op1", Scheduled))!.ClaimId);
    }

    [Fact]
    public async Task DuplicateDuringDelivery_WaitsWithoutResendingPersistedAnswer()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1", OperationStatus.Active));
        var llm = new FakeLlmExecutor();
        var sender = new PendingSender();
        var clock = new FakeClock();
        var repo = new FakeOccurrenceRepository(ops, clock);
        var handler = new ExecuteOccurrenceHandler(ops, repo, sender, llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);
        var first = handler.ExecuteAttemptAsync("42", "op1", Scheduled, 0);
        try
        {
            Assert.Equal(1, sender.Calls);
            Assert.Equal("generated", (await repo.GetReceiptAsync("42", "op1", Scheduled))!.ExecutionStatus);
            Assert.Equal(SingleAttemptOutcome.WaitingForClaim,
                (await handler.ExecuteAttemptAsync("42", "op1", Scheduled, 5)).Outcome);
            Assert.Single(llm.Requests);
            Assert.Equal(1, sender.Calls);
        }
        finally { sender.Completion.TrySetResult(1); }
        Assert.Equal(SingleAttemptOutcome.Sent, (await first).Outcome);
    }

    private sealed class PendingSender : ITelegramTransport
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<long> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> SendAsync(long chatId, TelegramPayload payload, CancellationToken ct = default)
        {
            Calls++;
            return Completion.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task ReleasedLease_AllowsSameIndexRecovery_WithoutRegeneration()
    {
        var (handler, repo, llm, sender, clock) = New();
        sender.EnqueueThrow(new TelegramSendException(500, "retry"));
        Assert.Equal(SingleAttemptOutcome.NeedRetry, (await handler.ExecuteAttemptAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Null((await repo.GetReceiptAsync("42", "op1", Scheduled))!.ClaimId);
        Assert.Equal(SingleAttemptOutcome.Sent, (await handler.ExecuteAttemptAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Single(llm.Requests);
        Assert.Single(sender.Payloads);
    }

    [Fact]
    public async Task ExpiredOwner_CannotWriteOrReleaseItsSuccessor()
    {
        var (_, repo, _, _, clock) = New();
        Assert.True(await repo.TryClaimAsync("42", "op1", Scheduled, "old"));
        clock.Now += TimeSpan.FromMinutes(16);
        Assert.True(await repo.TryClaimAsync("42", "op1", Scheduled, "new"));
        await Assert.ThrowsAsync<ClaimLostException>(() =>
            repo.MarkUnsupportedVersionAsync("42", "op1", Scheduled, "old", 1));
        Assert.False(await repo.RenewClaimAsync("42", "op1", Scheduled, "old"));
        await repo.ReleaseClaimAsync("42", "op1", Scheduled, "old");
        Assert.Equal("new", (await repo.GetReceiptAsync("42", "op1", Scheduled))!.ClaimId);
    }

    [Fact]
    public async Task CrashClaim_IsRecoveredAfterExpiry()
    {
        var (handler, repo, llm, sender, clock) = New();
        await repo.TryClaimAsync("42", "op1", Scheduled, "crashed");
        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, (await handler.ExecuteAttemptAsync("42", "op1", Scheduled, 0)).Outcome);
        clock.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(SingleAttemptOutcome.Sent, (await handler.ExecuteAttemptAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Single(llm.Requests);
        Assert.Single(sender.Payloads);
    }

    [Fact]
    public async Task LostLease_DiscardsLlmResult()
    {
        var (handler, repo, llm, sender, clock) = New();
        llm.Responder = _ =>
        {
            repo.LoseOnRenewal = true;
            return Task.FromResult(FakeLlmExecutor.Answer("must not send"));
        };
        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, (await handler.ExecuteAttemptAsync("42", "op1", Scheduled, 0)).Outcome);
        Assert.Empty(sender.Payloads);
    }

    [Fact]
    public void ConfirmationInstant_FormatsUtcPortionFromUtc()
    {
        // 14:08Z is 17:08 in Moscow (+03:00, no DST): the Z portion must
        // render the UTC instant, not the local time a second time.
        var zone = DateTimeZoneProviders.Tzdb["Europe/Moscow"];
        var line = TaskConfirmationPreview.FormatInstant(
            new DateTime(2026, 6, 1, 14, 8, 0, DateTimeKind.Utc), zone, "Europe/Moscow");
        Assert.Equal("01 Jun 17:08 Europe/Moscow (+03:00) / 01 Jun 14:08Z", line);
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
