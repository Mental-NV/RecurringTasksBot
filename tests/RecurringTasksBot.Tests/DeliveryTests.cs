using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class DeliveryTests
{
    private static readonly DateTime Scheduled =
        new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    private static (DeliveryHandler H, FakeOperationStore Ops, FakeDeliveryStore D,
        FakePayloadStore P, FakeTelegramSender T, FakeLlmExecutor L) New(
        OperationStatus status = OperationStatus.Active)
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var p = new FakePayloadStore();
        var t = new FakeTelegramSender();
        var l = new FakeLlmExecutor();
        ops.Seed(TestRecords.Operation("42", "op1", status));
        return (new DeliveryHandler(ops, d, p, t, l, TestLlm.Options()), ops, d, p, t, l);
    }

    [Fact]
    public async Task HappyPath_GeneratesPersistsThenSendsAnswer()
    {
        var (h, _, d, p, t, l) = New();
        l.Handler = prompt => Task.FromResult(FakeLlmExecutor.Answer(
            "The plants need water.",
            [new LlmSource("Garden guide", "https://example.com/garden")]));
        var result = await h.DeliverAsync("42", "op1", Scheduled);

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Single(l.Calls);
        // Fresh prompt without history: the stored text plus scheduling context.
        Assert.Equal("Water the plants", l.Calls[0].PromptText);
        Assert.Equal(Scheduled, l.Calls[0].ScheduledUtc);
        Assert.Single(t.Sent);
        Assert.Contains("The plants need water.", t.Sent[0].Text);
        Assert.Contains("op1", t.Sent[0].Text);
        Assert.Contains("https://example.com/garden", t.Sent[0].Text);
        Assert.DoesNotContain("Hi!", t.Sent[0].Text);

        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal("sent", receipt!.Status);
        Assert.Equal("generated", receipt.ExecutionStatus);
        Assert.Equal("OpenRouter", receipt.Provider);
        Assert.Equal("deepseek/deepseek-v4.1-flash", receipt.ModelName);
        Assert.Equal(10, receipt.PromptTokens);
        Assert.True(receipt.SearchUsed);
        var payload = await p.LoadAsync("42", "op1", Scheduled);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task DeletedOperation_IsNotSent()
    {
        var (h, _, _, _, t, l) = New(OperationStatus.Deleted);
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
        Assert.Empty(t.Sent);
        Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task FailedOperation_IsNotSent()
    {
        var (h, _, _, _, t, l) = New(OperationStatus.Failed);
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
        Assert.Empty(t.Sent);
        Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task MissingOperation_IsNotSent()
    {
        var h = new DeliveryHandler(new FakeOperationStore(), new FakeDeliveryStore(),
            new FakePayloadStore(), new FakeTelegramSender(), new FakeLlmExecutor(),
            TestLlm.Options());
        var result = await h.DeliverAsync("42", "ghost", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
    }

    [Fact]
    public async Task AlreadySentOccurrence_IsSuppressed()
    {
        var (h, _, d, _, t, l) = New();
        await d.UpsertAsync(new DeliveryReceipt("42", "op1", Scheduled, "sent", 1, null, 9));
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDuplicate, result.Outcome);
        Assert.Empty(t.Sent);
        Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task TransientLlmFailure_RetriesTwice_ThenSendsFailureNotice()
    {
        var (h, ops, d, _, t, l) = New();
        var calls = 0;
        l.Handler = _ =>
        {
            calls++;
            return calls <= 2
                ? Task.FromException<LlmResult>(new LlmExecutionException(
                    LlmFailureKind.Transient, "boom"))
                : Task.FromResult(FakeLlmExecutor.Answer("recovered"));
        };
        var waits = new List<TimeSpan>();

        var result = await h.DeliverAsync("42", "op1", Scheduled,
            waitAsync: w => { waits.Add(w); return Task.CompletedTask; });

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(3, calls);
        Assert.Equal(2, waits.Count);
        Assert.True(waits[0] < waits[1], "generation delays increase");
        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal(3, receipt!.GenerationAttempts);
        Assert.Equal("generated", receipt.ExecutionStatus);
    }

    [Fact]
    public async Task ExhaustedLlmRetries_SendsFailureNotice_KeepsActive()
    {
        var (h, ops, d, _, t, l) = New();
        l.Handler = _ => Task.FromException<LlmResult>(
            new LlmExecutionException(LlmFailureKind.Transient, "upstream 500"));

        var result = await h.DeliverAsync("42", "op1", Scheduled);

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(3, l.Calls.Count); // initial + 2 retries
        Assert.Single(t.Sent);
        Assert.Contains("op1", t.Sent[0].Text);
        Assert.Contains("future runs remain scheduled", t.Sent[0].Text);
        Assert.DoesNotContain("upstream 500", t.Sent[0].Text);
        Assert.Equal(OperationStatus.Active, (await ops.GetAsync("42", "op1"))!.Status);
        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal("failed", receipt!.ExecutionStatus);
        Assert.Equal("sent", receipt.Status);
        Assert.NotNull(receipt.FailureNotice);
    }

    [Fact]
    public async Task PermanentLlmFailure_NoRetry_SendsNotice()
    {
        var (h, _, _, _, t, l) = New();
        l.Handler = _ => Task.FromException<LlmResult>(
            new LlmExecutionException(LlmFailureKind.Permanent, "invalid request"));

        var result = await h.DeliverAsync("42", "op1", Scheduled);

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Single(l.Calls);
        Assert.Contains("future runs remain scheduled", t.Sent[0].Text);
    }

    [Fact]
    public async Task EmptyLlmResponse_CountsAsExecutionFailure()
    {
        var (h, _, _, _, t, l) = New();
        l.Handler = _ => Task.FromResult(FakeLlmExecutor.Answer("   "));

        var result = await h.DeliverAsync("42", "op1", Scheduled);

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(3, l.Calls.Count);
        Assert.Contains("future runs remain scheduled", t.Sent[0].Text);
    }

    [Fact]
    public async Task PersistedResult_ReusedAfterRestart_WithoutRegenerating()
    {
        var (h, _, _, _, t, l) = New();
        // First invocation generates and persists but the send fails transiently.
        t.EnqueueThrow(new TelegramSendException(500, "x"));
        var first = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.Single(l.Calls);

        // Restart: delivery retries reuse the persisted result.
        var second = await h.AttemptOnceAsync("42", "op1", Scheduled, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        Assert.Single(l.Calls);
        Assert.Single(t.Sent);
        Assert.Contains("Canned answer.", t.Sent[0].Text);
    }

    [Fact]
    public async Task ConcurrentDuplicateActivity_RetriesDurably_WithoutGenerating()
    {
        // Exclusive ownership: the duplicate never generates, and never
        // skips the occurrence either — it retries until the owner makes
        // progress, completes, or its claim goes stale.
        var (h, _, d, _, _, l) = New();
        Assert.True(await d.TryClaimAsync("42", "op1", Scheduled, "other-worker"));
        var result = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, result.Outcome);
        Assert.Equal(OccurrenceExecution.ClaimRecheckDelay, result.RetryIn);
        Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task SameIndexDuplicate_RetriesInsteadOfSkipping()
    {
        // A fresh, unprogressed claim means the owner is in flight: a
        // same-index duplicate retries durably instead of skipping, so an
        // early crash never loses the occurrence. Lease expiry allows recovery.
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var h = new DeliveryHandler(ops, d, new FakePayloadStore(),
            new FakeTelegramSender(), new FakeLlmExecutor(), TestLlm.Options());
        Assert.True(await d.TryClaimAsync("42", "op1", Scheduled, "other-worker"));

        var result = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, result.Outcome);
    }

    [Fact]
    public async Task LongAnswer_TruncatedWithMark_BeforePersist()
    {
        var (h, _, _, p, t, l) = New();
        l.Handler = _ => Task.FromResult(
            FakeLlmExecutor.Answer(new string('z', 40000)));
        var result = await h.DeliverAsync("42", "op1", Scheduled);

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var payload = await p.LoadAsync("42", "op1", Scheduled);
        Assert.NotNull(payload);
        Assert.Contains("truncated", string.Concat(payload));
    }

    [Fact]
    public async Task PartialDelivery_ResumesAtFirstUnconfirmedPart()
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var p = new FakePayloadStore();
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var h = new DeliveryHandler(ops, d, p, sender, llm, TestLlm.Options());

        await p.PersistAsync("42", "op1", Scheduled, ["part-one", "part-two"]);
        await d.UpsertAsync(new DeliveryReceipt("42", "op1", Scheduled,
            OccurrenceExecution.StatusGenerating, 0, null, null,
            ExecutionStatus: "generated", TotalParts: 2,
            UpdatedUtc: DateTimeOffset.UtcNow - TimeSpan.FromHours(1)));
        sender.EnqueueSuccess(messageId: 11);
        sender.EnqueueThrow(new TelegramSendException(500, "x"));

        var first = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        // First part confirmed; resume at the second.
        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal(1, receipt!.SentParts);

        var second = await h.AttemptOnceAsync("42", "op1", Scheduled, 1);
        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        Assert.Empty(llm.Calls);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("part-one", sender.Sent[0].Text);
        Assert.Equal("part-two", sender.Sent[1].Text);
    }

    [Fact]
    public async Task DeletionDuringGeneration_DiscardsResult()
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var orch = new FakeOrchestrations();
        var llm = new FakeLlmExecutor();
        llm.Handler = async prompt =>
        {
            await DeletionHandler.DeleteAsync(ops, orch, "42", "op1");
            return FakeLlmExecutor.Answer("too late");
        };
        var h = new DeliveryHandler(ops, d, new FakePayloadStore(), sender, llm, TestLlm.Options());

        var result = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.SkippedStopped, result.Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task TransientTelegramFailure_RetriesThreeTimes_ThenKeepsActive()
    {
        var (h, ops, d, _, _, _) = New();
        var sender = new FakeTelegramSender
        {
            AlwaysThrow = new TelegramSendException(500, "Internal Server Error"),
        };
        h = new DeliveryHandler(ops, d, new FakePayloadStore(), sender,
            new FakeLlmExecutor(), TestLlm.Options());
        var waits = new List<TimeSpan>();

        var result = await h.DeliverAsync("42", "op1", Scheduled,
            waitAsync: w => { waits.Add(w); return Task.CompletedTask; });

        Assert.Equal(DeliveryOutcome.FailedOccurrenceKeptActive, result.Outcome);
        Assert.Equal(OperationStatus.Active, (await ops.GetAsync("42", "op1"))!.Status);
        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal("failed", receipt!.Status);
        Assert.Equal("generated", receipt.ExecutionStatus);
        Assert.NotNull(result.ErrorSummary);
    }

    [Fact]
    public async Task RateLimit_HonoursRetryAfter()
    {
        var (h, ops, d, p, _, l) = New();
        var sender = new FakeTelegramSender();
        sender.EnqueueThrow(new TelegramSendException(429, "Too Many Requests",
            TimeSpan.FromSeconds(70)));
        h = new DeliveryHandler(ops, d, p, sender, l, TestLlm.Options());
        var waits = new List<TimeSpan>();

        var result = await h.DeliverAsync("42", "op1", Scheduled,
            waitAsync: w => { waits.Add(w); return Task.CompletedTask; });

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(70), waits[0]);
    }

    [Fact]
    public async Task BlockedBot_MarksOperationFailed_AndStops()
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var sender = new FakeTelegramSender
        {
            AlwaysThrow = new TelegramSendException(403, "Forbidden: bot was blocked by the user"),
        };
        ops.Seed(TestRecords.Operation("42", "op1"));
        var h = new DeliveryHandler(ops, d, new FakePayloadStore(), sender,
            new FakeLlmExecutor(), TestLlm.Options());

        var result = await h.DeliverAsync("42", "op1", Scheduled);

        Assert.Equal(DeliveryOutcome.OperationFailed, result.Outcome);
        Assert.Equal(1, result.Attempts); // no retries for permanent failures
        Assert.Equal(OperationStatus.Failed, (await ops.GetAsync("42", "op1"))!.Status);
        Assert.Contains("blocked", (await ops.GetAsync("42", "op1"))!.FailureSummary);
        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal("failed", receipt!.Status);
    }

    [Fact]
    public async Task ChatNotFound_IsPermanent()
    {
        var failure = DeliveryPolicy.ClassifyTelegram(404, "Bad Request: chat not found");
        Assert.Equal(SendFailureKind.Permanent, failure.Kind);
    }

    [Theory]
    [InlineData(500, "Internal Server Error", SendFailureKind.Transient)]
    [InlineData(502, "Bad Gateway", SendFailureKind.Transient)]
    [InlineData(429, "Too Many Requests", SendFailureKind.Transient)]
    [InlineData(403, "Forbidden: bot was blocked by the user", SendFailureKind.Permanent)]
    [InlineData(403, "Forbidden: bot was kicked", SendFailureKind.Permanent)]
    public void Classification_Matrix(int status, string desc, SendFailureKind expected)
    {
        Assert.Equal(expected, DeliveryPolicy.ClassifyTelegram(status, desc).Kind);
    }

    [Fact]
    public async Task DeleteBeforeSend_SuppressesSend()
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var orch = new FakeOrchestrations();
        var h = new DeliveryHandler(ops, d, new FakePayloadStore(), sender,
            new FakeLlmExecutor(), TestLlm.Options());

        Assert.True(await DeletionHandler.DeleteAsync(ops, orch, "42", "op1"));
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
        Assert.Empty(sender.Sent);
    }
}
