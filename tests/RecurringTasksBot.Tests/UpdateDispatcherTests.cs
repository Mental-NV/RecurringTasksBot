using Moq;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class UpdateDispatcherTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

    private const string CreateJson =
        """/create {"prompt": "Brief me", "schedule": {"cron": "0 0 9 * * *"}}""";

    private static (UpdateDispatcher P, FakeTaskStore Tasks, FakeReceiptStore R,
        FakeOrchestrations O, FakeTelegramSender T) New()
    {
        var tasks = new FakeTaskStore();
        var r = new FakeReceiptStore();
        var o = new FakeOrchestrations();
        var t = new FakeTelegramSender();
        return (new UpdateDispatcher(tasks, r, o, t, TaskDefaults.Default, NewOccurrences()),
            tasks, r, o, t);
    }

    private static FakeOccurrenceRepository NewOccurrences() =>
        new(new FakeOperationStore(), new FakeClock());

    private static IncomingUpdate Msg(long updateId, long userId, string text) =>
        new(updateId, userId, 777, TelegramUpdateKind.Message, text);

    [Fact]
    public async Task BadSecret_403_NoWork()
    {
        var (p, _, _, o, t) = New();
        var result = await p.ProcessAsync(false, Msg(1, 42, "/list"), Now);
        Assert.Equal(403, result.StatusCode);
        Assert.Empty(t.Payloads);
        Assert.Empty(o.StartedTaskInstances);
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
        var (p, tasks, _, _, t) = New();
        var result = await p.ProcessAsync(true, null, Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Empty(t.Payloads);
        Assert.Empty(await tasks.ListOwnedAsync("42"));
    }

    [Fact]
    public async Task Create_EndToEnd_200_WithConfirmation()
    {
        var (p, tasks, r, o, t) = New();
        var result = await p.ProcessAsync(true, Msg(100, 42, CreateJson), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(t.Payloads);
        Assert.Contains("revision 1", t.Payloads[0].Content);
        Assert.Single(o.StartedTaskInstances);
        var receipt = await r.GetAsync("42", 100);
        Assert.True(receipt!.CommandCompleted);
        Assert.True(receipt.ReplyDelivered);
        var record = Assert.Single(await tasks.ListOwnedAsync("42"));
        Assert.Equal(TaskState.Active, record.Status);
        Assert.Equal("Brief me", record.Definition.Prompt);
    }

    [Fact]
    public async Task Create_Redelivery_Acks200_WithoutSecondTask()
    {
        var (p, tasks, _, o, t) = New();
        await p.ProcessAsync(true, Msg(100, 42, CreateJson), Now);
        t.Payloads.Clear();
        var second = await p.ProcessAsync(true, Msg(100, 42, CreateJson), Now);

        Assert.Equal(200, second.StatusCode);
        Assert.Single(o.StartedTaskInstances);
        Assert.Single(await tasks.ListOwnedAsync("42"));
        Assert.Empty(t.Payloads); // confirmation already delivered; nothing repeated
    }

    [Fact]
    public async Task Create_FailedReply_IsResent_WithoutReExecuting()
    {
        var tasks = new FakeTaskStore();
        var r = new FakeReceiptStore();
        var o = new FakeOrchestrations();
        var sender = new Mock<ITelegramTransport>();
        // First reply attempt fails transiently.
        sender.SetupSequence(s => s.SendAsync(It.IsAny<long>(), It.IsAny<TelegramPayload>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TelegramSendException(500, "boom"))
            .ReturnsAsync(2L);
        var p = new UpdateDispatcher(tasks, r, o, sender.Object, TaskDefaults.Default, NewOccurrences());

        var first = await p.ProcessAsync(true, Msg(100, 42, CreateJson), Now);
        Assert.Equal(503, first.StatusCode);
        Assert.Single(o.StartedTaskInstances);

        var receipt = await r.GetAsync("42", 100);
        Assert.True(receipt!.CommandCompleted);
        Assert.False(receipt.ReplyDelivered);

        // Redelivery resends only the confirmation; no second task lifecycle.
        var second = await p.ProcessAsync(true, Msg(100, 42, CreateJson), Now);
        Assert.Equal(200, second.StatusCode);
        Assert.Single(o.StartedTaskInstances);
        sender.Verify(s => s.SendAsync(777, It.Is<TelegramPayload>(m =>
            m.Kind == TelegramPayloadKind.LiteralRich && m.Content.Contains("revision 1")),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.True((await r.GetAsync("42", 100))!.ReplyDelivered);
    }

    [Fact]
    public async Task LegacyCreate_200_WithHelp_CreatesNothing()
    {
        var (p, tasks, _, o, t) = New();
        var result = await p.ProcessAsync(true, Msg(1, 42, "/create 0 0 9 * * * hi"), Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(t.Payloads);
        Assert.Contains("Usage", t.Payloads[0].Content);
        Assert.Empty(o.StartedTaskInstances);
        Assert.Empty(await tasks.ListOwnedAsync("42"));
    }

    [Fact]
    public async Task UnknownCommand_200_WithHelp()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(1, 42, "/frobnicate"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(TaskHelp.Unknown, t.Payloads[0].Content);
    }

    [Fact]
    public async Task List_Empty_200_WithHint()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("No recurring tasks", t.Payloads[0].Content);
    }

    [Fact]
    public async Task List_IsOwnerIsolated()
    {
        var (p, tasks, _, _, t) = New();
        tasks.Seed(TestRecords.Task("alice", "a31f9c00aa"));
        var result = await p.ProcessAsync(true, Msg(3, 99, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.DoesNotContain("a31f9c", t.Payloads[0].Content);
    }

    [Fact]
    public async Task Get_UnknownId_200_WithNotFound()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(4, 42, "/get zzz999000"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("task_not_found", t.Payloads[0].Content);
    }

    [Fact]
    public async Task Delete_EndToEnd_200_TerminatesAfterTombstone()
    {
        var (p, tasks, _, o, t) = New();
        var create = await p.ProcessAsync(true, Msg(100, 42, CreateJson), Now);
        Assert.Equal(200, create.StatusCode);
        var taskId = (await tasks.ListOwnedAsync("42"))[0].TaskId;

        var del = await p.ProcessAsync(true, Msg(101, 42, $"/delete {taskId[..8]}"), Now);
        Assert.Equal(200, del.StatusCode);
        Assert.Contains("deleted", t.Payloads[^1].Content);
        Assert.Equal(TaskState.Deleted, (await tasks.GetAsync("42", taskId))!.Status);
        Assert.Contains(Ids.DeriveInstanceId(taskId), o.TerminatedInstances);
    }

    [Fact]
    public async Task Delete_UnknownId_200_WithNotFound()
    {
        var (p, _, _, _, t) = New();
        var result = await p.ProcessAsync(true, Msg(5, 42, "/delete ghost99"), Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("task_not_found", t.Payloads[0].Content);
    }

    [Fact]
    public async Task TransientStoreFailure_503()
    {
        var tasks = new Mock<ITaskStore>();
        tasks.Setup(o => o.ListOwnedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("tables down"));
        var p = new UpdateDispatcher(tasks.Object, new FakeReceiptStore(),
            new FakeOrchestrations(), new FakeTelegramSender(), TaskDefaults.Default, NewOccurrences());

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
        var p = new UpdateDispatcher(new FakeTaskStore(), new FakeReceiptStore(),
            new FakeOrchestrations(), sender.Object, TaskDefaults.Default, NewOccurrences());

        var result = await p.ProcessAsync(true, Msg(2, 42, "/list"), Now);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task ReplyWithScheduleOnly_UsesRepliedText()
    {
        var (p, tasks, _, o, t) = New();
        const string text = """/create {"schedule": {"cron": "0 0 9 * * *"}}""";
        var update = new IncomingUpdate(50, 42, 777, TelegramUpdateKind.Message,
            text, "Long prompt from the replied message.", 42);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(o.StartedTaskInstances);
        var stored = Assert.Single(await tasks.ListOwnedAsync("42"));
        Assert.Equal("Long prompt from the replied message.", stored.Definition.Prompt);
        Assert.Contains("revision 1", t.Payloads[0].Content);
    }

    [Fact]
    public async Task ReplyFromAnotherUser_IsIgnored()
    {
        var (p, _, _, o, _) = New();
        const string text = """/create {"schedule": {"cron": "0 0 9 * * *"}}""";
        var update = new IncomingUpdate(51, 42, 777, TelegramUpdateKind.Message,
            text, "Someone else's text.", 99);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(o.StartedTaskInstances);
    }

    [Fact]
    public async Task OversizedReply_IsRejected_NotTruncated()
    {
        var (p, _, _, o, t) = New();
        const string text = """/create {"schedule": {"cron": "0 0 9 * * *"}}""";
        var update = new IncomingUpdate(52, 42, 777, TelegramUpdateKind.Message,
            text, new string('q', 40000), 42);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(o.StartedTaskInstances);
        Assert.Contains("32768", t.Payloads[0].Content);
    }

    [Fact]
    public async Task Update_Redelivery_DoesNotApplyTwice()
    {
        var (p, tasks, _, _, t) = New();
        await p.ProcessAsync(true, Msg(100, 42, CreateJson), Now);
        var taskId = (await tasks.ListOwnedAsync("42"))[0].TaskId;
        var updateText = $$"""/update {{taskId}} {"prompt": "Changed"}""";
        var first = await p.ProcessAsync(true, Msg(101, 42, updateText), Now);
        Assert.Equal(200, first.StatusCode);
        t.Payloads.Clear();

        // A later edit lands first; the stale redelivery must not re-apply.
        var newerText = $$"""/update {{taskId}} {"prompt": "Newer"}""";
        await p.ProcessAsync(true, Msg(102, 42, newerText), Now);
        var redelivery = await p.ProcessAsync(true, Msg(101, 42, updateText), Now);
        Assert.Equal(200, redelivery.StatusCode);
        var record = await tasks.GetAsync("42", taskId);
        Assert.Equal("Newer", record!.Definition.Prompt);
        Assert.Equal(3, record.Revision);
    }

    [Fact]
    public void Help_MentionsExternalService()
    {
        Assert.Contains("external LLM", TaskHelp.Unknown);
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
