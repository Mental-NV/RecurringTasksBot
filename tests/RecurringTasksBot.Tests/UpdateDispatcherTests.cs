using Moq;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class UpdateDispatcherTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

    private static (UpdateDispatcher P, FakeOperationStore Ops, FakeReceiptStore R,
        FakeOrchestrations O, FakeTelegramSender T) New()
    {
        var ops = new FakeOperationStore();
        var r = new FakeReceiptStore();
        var o = new FakeOrchestrations();
        var t = new FakeTelegramSender();
        return (new UpdateDispatcher(ops, r, o, t), ops, r, o, t);
    }

    private static IncomingUpdate Msg(long updateId, long userId, string text) =>
        new(updateId, userId, 777, TelegramUpdateKind.Message, text);

    [Fact]
    public async Task BadSecret_403_NoWork()
    {
        var (p, _, _, o, t) = New();
        var result = await p.ProcessAsync(false, Msg(1, 42, "/list"), Now);
        Assert.Equal(403, result.StatusCode);
        Assert.Empty(t.Payloads);
        Assert.Empty(o.StartedInstances);
    }

    [Fact]
    public async Task UnsupportedUpdate_200_Silent()
    {
        var (p, _, _, _, t) = New();
        var update = new IncomingUpdate(1, 42, 777, TelegramUpdateKind.Unsupported, null);
        var result = await p.ProcessAsync(true, update, Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Empty(t.Payloads);
    }

    [Fact]
    public async Task NullUpdate_200_Silent()
    {
        var (p, ops, _, _, t) = New();
        var result = await p.ProcessAsync(true, null, Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Empty(t.Payloads);
        Assert.Empty(await ops.ListOwnedAsync("42"));
    }

    [Fact]
    public async Task Create_EndToEnd_200_WithConfirmation()
    {
        var (p, ops, r, o, t) = New();
        var result = await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(t.Payloads);
        Assert.Contains("created", t.Payloads[0].Content);
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
        t.Payloads.Clear();
        var second = await p.ProcessAsync(true, Msg(100, 42, "/create 0 0 9 * * * hi"), Now);

        Assert.Equal(200, second.StatusCode);
        Assert.Single(o.StartedInstances);
        Assert.Empty(t.Payloads); // confirmation already delivered; nothing repeated
    }

    [Fact]
    public async Task Create_FailedReply_IsResent_WithoutReExecuting()
    {
        var ops = new FakeOperationStore();
        var r = new FakeReceiptStore();
        var o = new FakeOrchestrations();
        var sender = new Mock<ITelegramTransport>();
        // First reply attempt fails transiently.
        sender.SetupSequence(s => s.SendAsync(It.IsAny<long>(), It.IsAny<TelegramPayload>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TelegramSendException(500, "boom"))
            .ReturnsAsync(2L);
        var p = new UpdateDispatcher(ops, r, o, sender.Object);

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
        sender.Verify(s => s.SendAsync(777, It.Is<TelegramPayload>(m =>
            m.Kind == TelegramPayloadKind.LiteralRich && m.Content.Contains("created")),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.True((await r.GetAsync("42", 100))!.ReplyDelivered);
    }

    [Fact]
    public async Task InvalidCreate_200_WithHelp_NoRecurrence()
    {
        var (p, _, _, o, t) = New();
        var result = await p.ProcessAsync(true, Msg(1, 42, "/create bogus schedule"), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(t.Payloads);
        Assert.Contains("/create", t.Payloads[0].Content);
        Assert.Empty(o.StartedInstances);
    }

    [Fact]
    public async Task UnknownCommand_200_WithHelp()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(1, 42, "/frobnicate"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(HelpText.Message, t.Payloads[0].Content);
    }

    [Fact]
    public async Task List_SendsSplitMessages_200()
    {
        var (p, ops, _, _, t) = New();
        ops.Seed(TestRecords.Operation("42", "op1"));
        ops.Seed(TestRecords.Operation("42", "op2"));

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.NotEmpty(t.Payloads);
        Assert.All(t.Payloads, m => Assert.True(TextLimits.CountChars(m.Content) <= 32768));
        var joined = string.Join("\n", t.Payloads.Select(m => m.Content));
        Assert.Contains("op1", joined);
        Assert.Contains("op2", joined);
    }

    [Fact]
    public async Task List_Empty_200_WithHint()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(ListFormatter.EmptyListMessage, t.Payloads[0].Content);
    }

    [Fact]
    public async Task List_IsOwnerIsolated()
    {
        var (p, ops, _, _, t) = New();
        ops.Seed(TestRecords.Operation("alice", "opA"));
        var result = await p.ProcessAsync(true, Msg(3, 99, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.DoesNotContain("opA", t.Payloads[0].Content);
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
        Assert.Contains("deleted", t.Payloads[^1].Content);
        Assert.Equal(OperationStatus.Deleted, (await ops.GetAsync("42", opId))!.Status);
        Assert.Contains(Ids.DeriveInstanceId(opId), o.TerminatedInstances);
    }

    [Fact]
    public async Task List_DuplicateTextOperations_RenderTwoSeparatedBlocks()
    {
        var (p, ops, _, _, t) = New();
        ops.Seed(TestRecords.Operation("42", "op1") with { Text = "same prompt" });
        ops.Seed(TestRecords.Operation("42", "op2") with { Text = "same prompt" });

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);

        Assert.Equal(200, result.StatusCode);
        var body = t.Payloads[0].Content;
        Assert.Contains("op1", body);
        Assert.Contains("op2", body);
        var first = body.IndexOf("op1", StringComparison.Ordinal);
        var second = body.IndexOf("op2", StringComparison.Ordinal);
        Assert.InRange(second, first + 1, body.Length - 1);
        Assert.Contains("\n\n", body[first..second]);
    }

    [Fact]
    public async Task Delete_UnknownId_200_WithNotice()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(5, 42, "/delete ghost"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("No reminder", t.Payloads[0].Content);
    }

    [Fact]
    public async Task TransientStoreFailure_503()
    {
        var ops = new Mock<IOperationStore>();
        ops.Setup(o => o.ListOwnedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("tables down"));
        var p = new UpdateDispatcher(ops.Object, new FakeReceiptStore(),
            new FakeOrchestrations(), new FakeTelegramSender());

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task BlockedBot_OnReply_Still200()
    {
        var sender = new Mock<ITelegramTransport>();
        sender.Setup(s => s.SendAsync(It.IsAny<long>(), It.IsAny<TelegramPayload>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TelegramSendException(403, "Forbidden: bot was blocked by the user"));
        var p = new UpdateDispatcher(new FakeOperationStore(), new FakeReceiptStore(),
            new FakeOrchestrations(), sender.Object);

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task ReplyWithScheduleOnly_UsesRepliedText()
    {
        var (p, ops, _, o, t) = New();
        var update = new IncomingUpdate(50, 42, 777, TelegramUpdateKind.Message,
            "/create 0 0 9 * * *", "Long prompt from the replied message.", 42);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(o.StartedInstances);
        var stored = (await ops.ListOwnedAsync("42")).Single();
        Assert.Equal("Long prompt from the replied message.", stored.Text);
        Assert.Contains("created", t.Payloads[0].Content);
    }

    [Fact]
    public async Task ReplyFromAnotherUser_IsIgnored()
    {
        var (p, _, _, o, t) = New();
        var update = new IncomingUpdate(51, 42, 777, TelegramUpdateKind.Message,
            "/create 0 0 9 * * *", "Someone else's text.", 99);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(o.StartedInstances);
    }

    [Fact]
    public async Task OversizedReply_IsRejected_NotTruncated()
    {
        var (p, _, _, o, t) = New();
        var update = new IncomingUpdate(52, 42, 777, TelegramUpdateKind.Message,
            "/create 0 0 9 * * *", new string('q', 40000), 42);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(o.StartedInstances);
        Assert.Contains("32768", t.Payloads[0].Content);
    }

    [Fact]
    public async Task Help_MentionsExternalService()
    {
        Assert.Contains("external LLM", HelpText.Message);
    }
}

public sealed class SingleAttemptTests
{
    private static readonly DateTime Scheduled =
        new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    private static (ExecuteOccurrenceHandler H, FakeOperationStore Ops, FakeTelegramSender Sender, FakeOccurrenceRepository Repo) New(
        OperationStatus status = OperationStatus.Active, TelegramSendException? fail = null)
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("42", "op1", status));
        var clock = new FakeClock();
        var repo = new FakeOccurrenceRepository(ops, clock);
        var sender = new FakeTelegramSender { AlwaysThrow = fail };
        var h = new ExecuteOccurrenceHandler(ops, repo, sender, new FakeLlmExecutor(),
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);
        return (h, ops, sender, repo);
    }

    [Fact]
    public async Task FirstFailure_ReturnsNeedRetry_WithIncreasingDelay()
    {
        var (h, _, _, _) = New(fail: new TelegramSendException(500, "x"));

        var r0 = await h.ExecuteAttemptAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.NeedRetry, r0.Outcome);
        Assert.Equal(2, r0.Attempts); // generation plus the failed delivery
        Assert.Equal(TimeSpan.FromSeconds(5), r0.RetryIn);
    }

    [Fact]
    public async Task ExhaustedAttempts_RecordFailedOccurrence_KeepActive()
    {
        var (h, ops, _, repo) = New(fail: new TelegramSendException(500, "x"));

        var r = await h.ExecuteAttemptAsync("42", "op1", Scheduled,
            OccurrenceExecution.MaxCombinedRetriesAfterInitial);
        Assert.Equal(SingleAttemptOutcome.OccurrenceFailed, r.Outcome);
        Assert.Equal(OperationStatus.Active,
            (await ops.GetAsync("42", "op1"))!.Status);
        Assert.Equal("failed",
            (await repo.GetReceiptAsync("42", "op1", Scheduled))!.Status);
    }

    [Fact]
    public async Task PermanentFailure_StopsOperation()
    {
        var (h, ops, _, _) = New(fail: new TelegramSendException(403, "Forbidden: bot was blocked by the user"));

        var r = await h.ExecuteAttemptAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.OperationFailed, r.Outcome);
        Assert.Equal(OperationStatus.Failed,
            (await ops.GetAsync("42", "op1"))!.Status);
    }

    [Fact]
    public async Task StoppedOperation_Skipped()
    {
        var (h, _, sender, _) = New(status: OperationStatus.Deleted);

        var r = await h.ExecuteAttemptAsync("42", "op1", Scheduled, 0);
        Assert.Equal(SingleAttemptOutcome.SkippedStopped, r.Outcome);
        Assert.Empty(sender.Payloads);
    }
}
