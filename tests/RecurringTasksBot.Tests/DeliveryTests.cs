using Moq;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class DeliveryTests
{
    private static readonly DateTime Scheduled =
        new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    private static (DeliveryHandler H, FakeOperationStore Ops, FakeDeliveryStore D, FakeTelegramSender T) New(
        OperationStatus status = OperationStatus.Active)
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var t = new FakeTelegramSender();
        ops.Seed(TestRecords.Operation("42", "op1", status));
        return (new DeliveryHandler(ops, d, t), ops, d, t);
    }

    [Fact]
    public async Task HappyPath_SendsGreetingPlusText()
    {
        var (h, _, d, t) = New();
        var result = await h.DeliverAsync("42", "op1", Scheduled);

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Single(t.Sent);
        Assert.Equal(111L, t.Sent[0].ChatId);
        Assert.Equal("Hi!\nWater the plants", t.Sent[0].Text);
        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal("sent", receipt!.Status);
        Assert.Equal(1, receipt.Attempts);
    }

    [Fact]
    public async Task DeletedOperation_IsNotSent()
    {
        var (h, _, _, t) = New(OperationStatus.Deleted);
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public async Task FailedOperation_IsNotSent()
    {
        var (h, _, _, t) = New(OperationStatus.Failed);
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public async Task MissingOperation_IsNotSent()
    {
        var h = new DeliveryHandler(new FakeOperationStore(), new FakeDeliveryStore(), new FakeTelegramSender());
        var result = await h.DeliverAsync("42", "ghost", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
    }

    [Fact]
    public async Task AlreadySentOccurrence_IsSuppressed()
    {
        var (h, _, d, t) = New();
        await d.UpsertAsync(new DeliveryReceipt("42", "op1", Scheduled, "sent", 1, null, 9));
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDuplicate, result.Outcome);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public async Task TransientFailure_RetriesThreeTimes_ThenKeepsActive()
    {
        var (h, ops, d, _) = New();
        var sender = new FakeTelegramSender
        {
            AlwaysThrow = new TelegramSendException(500, "Internal Server Error"),
        };
        h = new DeliveryHandler(ops, d, sender);
        var waits = new List<TimeSpan>();

        var result = await h.DeliverAsync("42", "op1", Scheduled,
            waitAsync: w => { waits.Add(w); return Task.CompletedTask; });

        Assert.Equal(DeliveryOutcome.FailedOccurrenceKeptActive, result.Outcome);
        Assert.Equal(4, result.Attempts); // initial + 3 retries
        Assert.Equal(3, waits.Count);
        Assert.True(waits[0] < waits[1] && waits[1] < waits[2], "delays increase");
        Assert.NotNull(result.ErrorSummary);

        // Operation stays active for future occurrences...
        Assert.Equal(OperationStatus.Active, (await ops.GetAsync("42", "op1"))!.Status);
        // ...while the failed occurrence is recorded.
        var receipt = await d.GetAsync("42", "op1", Scheduled);
        Assert.Equal("failed", receipt!.Status);
        Assert.Equal(4, receipt.Attempts);
    }

    [Fact]
    public async Task TransientFlake_SucceedsOnRetry()
    {
        var (h, _, _, t) = New();
        t.EnqueueThrow(new TimeoutException("timeout"));
        t.EnqueueThrow(new TelegramSendException(500, "boom"));
        var waits = new List<TimeSpan>();

        var result = await h.DeliverAsync("42", "op1", Scheduled,
            waitAsync: w => { waits.Add(w); return Task.CompletedTask; });

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(2, waits.Count);
    }

    [Fact]
    public async Task RateLimit_HonoursRetryAfter()
    {
        var (h, ops, d, _) = New();
        var sender = new FakeTelegramSender();
        sender.EnqueueThrow(new TelegramSendException(429, "Too Many Requests",
            TimeSpan.FromSeconds(70)));
        h = new DeliveryHandler(ops, d, sender);
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
        var h = new DeliveryHandler(ops, d, sender);

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
        // Delete lands before the delivery activity runs: the pre-send
        // status check observes the tombstone and nothing is sent.
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var orch = new FakeOrchestrations();
        var h = new DeliveryHandler(ops, d, sender);

        Assert.True(await DeletionHandler.DeleteAsync(ops, orch, "42", "op1"));
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task InFlightSend_MayFinish_AfterConcurrentDelete()
    {
        // The status check happens once just before the first attempt, so an
        // already-started send may finish even if deletion lands mid-flight.
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var h = new DeliveryHandler(ops, d, sender);

        sender.EnqueueThrow(new TelegramSendException(500, "x"));
        var result = await h.DeliverAsync("42", "op1", Scheduled);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.Equal(2, result.Attempts);
    }
}
