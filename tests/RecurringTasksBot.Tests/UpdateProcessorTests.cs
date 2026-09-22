using Moq;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class UpdateProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

    private static (UpdateProcessor P, FakeOperationStore Ops, FakeReceiptStore R,
        FakeOrchestrations O, FakeTelegramSender T) New()
    {
        var ops = new FakeOperationStore();
        var r = new FakeReceiptStore();
        var o = new FakeOrchestrations();
        var t = new FakeTelegramSender();
        return (new UpdateProcessor(ops, r, o, t), ops, r, o, t);
    }

    private static IncomingUpdate Msg(long updateId, long userId, string text) =>
        new(updateId, userId, 777, TelegramUpdateKind.Message, text);

    [Fact]
    public async Task BadSecret_403_NoWork()
    {
        var (p, _, _, o, t) = New();
        var result = await p.ProcessAsync(false, Msg(1, 42, "/list"), Now);
        Assert.Equal(403, result.StatusCode);
        Assert.Empty(t.Sent);
        Assert.Empty(o.StartedInstances);
    }

    [Fact]
    public async Task UnsupportedUpdate_200_Silent()
    {
        var (p, _, _, _, t) = New();
        var update = new IncomingUpdate(1, 42, 777, TelegramUpdateKind.Unsupported, null);
        var result = await p.ProcessAsync(true, update, Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Empty(t.Sent);
    }

    [Fact]
    public async Task Create_EndToEnd_200_WithConfirmation()
    {
        var (p, ops, r, o, t) = New();
        var result = await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(t.Sent);
        Assert.Contains("created", t.Sent[0].Text);
        Assert.Single(o.StartedInstances);
        var receipt = await r.GetAsync("42", 100);
        Assert.True(receipt!.CommandCompleted);
        Assert.True(receipt.ReplyDelivered);
        var op = await ops.GetAsync("42", receipt.OperationId!);
        Assert.Equal(OperationStatus.Active, op!.Status);
    }

    [Fact]
    public async Task Create_Redelivery_Acks200_WithoutSecondRecurrence()
    {
        var (p, _, _, o, t) = New();
        await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);
        t.Sent.Clear();
        var second = await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);

        Assert.Equal(200, second.StatusCode);
        Assert.Single(o.StartedInstances);
        Assert.Empty(t.Sent); // confirmation already delivered; nothing repeated
    }

    [Fact]
    public async Task Create_FailedReply_IsResent_WithoutReExecuting()
    {
        var ops = new FakeOperationStore();
        var r = new FakeReceiptStore();
        var o = new FakeOrchestrations();
        var sender = new Mock<ITelegramSender>();
        // First reply attempt fails transiently.
        sender.SetupSequence(s => s.SendTextAsync(It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TelegramSendException(500, "boom"))
            .ReturnsAsync(2L);
        var p = new UpdateProcessor(ops, r, o, sender.Object);

        var first = await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);
        Assert.Equal(503, first.StatusCode);
        Assert.Single(o.StartedInstances);

        var receipt = await r.GetAsync("42", 100);
        Assert.True(receipt!.CommandCompleted);
        Assert.False(receipt.ReplyDelivered);

        // Redelivery resends only the confirmation; no second orchestration.
        var second = await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);
        Assert.Equal(200, second.StatusCode);
        Assert.Single(o.StartedInstances);
        sender.Verify(s => s.SendTextAsync(777, It.Is<string>(m => m.Contains("created")),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.True((await r.GetAsync("42", 100))!.ReplyDelivered);
    }

    [Fact]
    public async Task InvalidCreate_200_WithHelp_NoRecurrence()
    {
        var (p, _, _, o, t) = New();
        var result = await p.ProcessAsync(true, Msg(1, 42, "/create bogus schedule"), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(t.Sent);
        Assert.Contains("/create", t.Sent[0].Text);
        Assert.Empty(o.StartedInstances);
    }

    [Fact]
    public async Task UnknownCommand_200_WithHelp()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(1, 42, "/frobnicate"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(ListFormatter.HelpMessage, t.Sent[0].Text);
    }

    [Fact]
    public async Task List_SendsSplitMessages_200()
    {
        var (p, ops, _, _, t) = New();
        ops.Seed(TestRecords.Operation("42", "op1"));
        ops.Seed(TestRecords.Operation("42", "op2"));

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.NotEmpty(t.Sent);
        Assert.All(t.Sent, m => Assert.True(m.Text.Length <= 4096));
        var joined = string.Join("\n", t.Sent.Select(m => m.Text));
        Assert.Contains("op1", joined);
        Assert.Contains("op2", joined);
    }

    [Fact]
    public async Task List_Empty_200_WithHint()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(ListFormatter.EmptyListMessage, t.Sent[0].Text);
    }

    [Fact]
    public async Task List_IsOwnerIsolated()
    {
        var (p, ops, _, _, t) = New();
        ops.Seed(TestRecords.Operation("alice", "opA"));
        var result = await p.ProcessAsync(true, Msg(3, 99, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.DoesNotContain("opA", t.Sent[0].Text);
    }

    [Fact]
    public async Task Delete_EndToEnd_200_TerminatesAfterTombstone()
    {
        var (p, ops, _, o, t) = New();
        var create = await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);
        Assert.Equal(200, create.StatusCode);
        var opId = (await ops.ListOwnedAsync("42"))[0].OperationId;

        var del = await p.ProcessAsync(true, Msg(101, 42, $"/delete {opId}"), Now);
        Assert.Equal(200, del.StatusCode);
        Assert.Contains("deleted", t.Sent[^1].Text);
        Assert.Equal(OperationStatus.Deleted, (await ops.GetAsync("42", opId))!.Status);
        Assert.Contains(Ids.DeriveInstanceId(opId), o.TerminatedInstances);
    }

    [Fact]
    public async Task Delete_UnknownId_200_WithNotice()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(5, 42, "/delete ghost"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("No reminder", t.Sent[0].Text);
    }

    [Fact]
    public async Task TransientStoreFailure_503()
    {
        var ops = new Mock<IOperationStore>();
        ops.Setup(o => o.ListOwnedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("tables down"));
        var p = new UpdateProcessor(ops.Object, new FakeReceiptStore(),
            new FakeOrchestrations(), new FakeTelegramSender());

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task BlockedBot_OnReply_Still200()
    {
        var sender = new Mock<ITelegramSender>();
        sender.Setup(s => s.SendTextAsync(It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TelegramSendException(403, "Forbidden: bot was blocked by the user"));
        var p = new UpdateProcessor(new FakeOperationStore(), new FakeReceiptStore(),
            new FakeOrchestrations(), sender.Object);

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
    }
}

public sealed class SingleAttemptTests
{
    private static readonly DateTime Scheduled =
        new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FirstFailure_ReturnsNeedRetry_WithIncreasingDelay()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var sender = new FakeTelegramSender
        {
            AlwaysThrow = new TelegramSendException(500, "x"),
        };
        var h = new DeliveryHandler(ops, new FakeDeliveryStore(), sender);

        var r0 = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, r0.Outcome);
        Assert.Equal(1, r0.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(5), r0.RetryIn);
    }

    [Fact]
    public async Task ExhaustedAttempts_RecordFailedOccurrence_KeepActive()
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var sender = new FakeTelegramSender
        {
            AlwaysThrow = new TelegramSendException(500, "x"),
        };
        var h = new DeliveryHandler(ops, d, sender);

        var r = await h.AttemptOnceAsync("42", "op1", Scheduled,
            DeliveryPolicy.MaxRetriesAfterInitial);
        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, r.Outcome);
        Assert.Equal(4, r.Attempts);
        Assert.Equal(OperationStatus.Active, (await ops.GetAsync("42", "op1"))!.Status);
        Assert.Equal("failed", (await d.GetAsync("42", "op1", Scheduled))!.Status);
    }

    [Fact]
    public async Task PermanentFailure_StopsOperation()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1"));
        var sender = new FakeTelegramSender
        {
            AlwaysThrow = new TelegramSendException(403, "Forbidden: bot was blocked by the user"),
        };
        var h = new DeliveryHandler(ops, new FakeDeliveryStore(), sender);

        var r = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.OperationFailed, r.Outcome);
        Assert.Equal(OperationStatus.Failed, (await ops.GetAsync("42", "op1"))!.Status);
    }

    [Fact]
    public async Task StoppedOperation_Skipped()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1", OperationStatus.Deleted));
        var sender = new FakeTelegramSender();
        var h = new DeliveryHandler(ops, new FakeDeliveryStore(), sender);

        var r = await h.AttemptOnceAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.SkippedStopped, r.Outcome);
        Assert.Empty(sender.Sent);
    }
}
