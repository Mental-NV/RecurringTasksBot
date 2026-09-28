using Moq;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

// Phase 5 command handlers over the task store contract.
public sealed class TaskHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeTaskStore Tasks { get; } = new();
        public FakeReceiptStore Receipts { get; } = new();
        public FakeOrchestrations Orchestrations { get; } = new();
        public FakeTelegramSender Transport { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeOccurrenceRepository Occurrences { get; }

        public Harness() => Occurrences = new(new FakeOperationStore(), Clock);
        public BotReplySender Replies => new(Transport);

        public async Task StartLifecycleAsync(string taskId, string ownerId = "111")
        {
            var record = await Tasks.GetAsync(ownerId, taskId);
            await Orchestrations.StartTaskAsync(record!.InstanceId, ownerId, taskId);
        }

        public IncomingUpdate Update(long updateId = 7, long userId = 111, string? text = null,
            string? replyPrompt = null) =>
            new(updateId, userId, 111L, TelegramUpdateKind.Message, text,
                replyPrompt, replyPrompt is null ? null : userId);
    }

    private const string CreateJson =
        """/create {"prompt": "Review the release checklist", "schedule": {"cron": "0 0 9 * * *"}}""";

    [Fact]
    public async Task Create_StoresStartsAndConfirms()
    {
        var h = new Harness();
        var handler = new TaskCreateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        var result = await handler.HandleCreateAsync("111", h.Update(text: CreateJson), CreateJson, Now);
        Assert.Equal(200, result.StatusCode);
        var reply = Assert.Single(result.RepliesSent);
        Assert.Contains("revision 1", reply);
        Assert.Contains("Limits:", reply);
        var owned = await h.Tasks.ListOwnedAsync("111");
        var record = Assert.Single(owned);
        Assert.Equal(1, record.Revision);
        Assert.Equal(TaskState.Active, record.Status);
        Assert.Equal(record.WaterlineUtc, record.CreatedAtUtc);
        Assert.Contains(record.InstanceId, h.Orchestrations.StartedTaskInstances);
    }

    [Fact]
    public async Task Create_LegacyPositional_ReturnsHelpAndStoresNothing()
    {
        var h = new Harness();
        var handler = new TaskCreateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = "/create 0 0 9 * * * Water the plants";
        var result = await handler.HandleCreateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("Usage", result.RepliesSent[0]);
        Assert.Empty(await h.Tasks.ListOwnedAsync("111"));
        Assert.Empty(h.Orchestrations.StartedTaskInstances);
    }

    [Fact]
    public async Task Create_FieldError_StoresNothing()
    {
        var h = new Harness();
        var handler = new TaskCreateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/create {"prompt": "x", "ownerId": "9"}""";
        var result = await handler.HandleCreateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("field_read_only", result.RepliesSent[0]);
        Assert.Empty(await h.Tasks.ListOwnedAsync("111"));
    }

    [Fact]
    public async Task Create_ReplyPrompt_FillsMissingPrompt()
    {
        var h = new Harness();
        var handler = new TaskCreateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/create {"schedule": {"cron": "0 0 9 * * *"}}""";
        var result = await handler.HandleCreateAsync("111",
            h.Update(text: text, replyPrompt: "Replied prompt"), text, Now);
        Assert.Contains("revision 1", result.RepliesSent[0]);
        var record = Assert.Single(await h.Tasks.ListOwnedAsync("111"));
        Assert.Equal("Replied prompt", record.Definition.Prompt);
    }

    [Fact]
    public async Task Create_Redelivery_DoesNotDuplicate()
    {
        var h = new Harness();
        var handler = new TaskCreateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        await handler.HandleCreateAsync("111", h.Update(text: CreateJson), CreateJson, Now);
        var again = await handler.HandleCreateAsync("111", h.Update(text: CreateJson), CreateJson, Now);
        Assert.Equal(200, again.StatusCode);
        Assert.Single(await h.Tasks.ListOwnedAsync("111"));
    }

    [Fact]
    public async Task Get_ExplicitAndEffective_RoundTrip()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskGetHandler(h.Tasks, h.Receipts, h.Replies);
        var explicitResult = await handler.HandleGetAsync("111",
            h.Update(text: "/get a31f9c"), "/get a31f9c", Now);
        Assert.Contains("(explicit)", explicitResult.RepliesSent[0]);
        Assert.Contains("Water the plants", explicitResult.RepliesSent[0]);
        var effectiveResult = await handler.HandleGetAsync("111",
            h.Update(updateId: 8, text: "/get a31f9c00aa effective"), "/get a31f9c00aa effective", Now);
        Assert.Contains("\"memoryMode\": \"None\"", effectiveResult.RepliesSent[0]);
    }

    [Fact]
    public async Task Get_OversizedDefinition_SendsWholeJsonAsDocument()
    {
        var h = new Harness();
        var prompt = new string('p', 40000);
        var definition = new TaskDefinition(
            prompt, new TaskSchedule("0 0 9 * * *", []), "UTC", TaskParameters.Empty);
        h.Tasks.Seed(new TaskRecord("111", "a31f9c00aa", 111L,
            Ids.DeriveInstanceId("a31f9c00aa"), definition, 1, TaskState.Active, null,
            Now.UtcDateTime, 0, null, Now.UtcDateTime, Now.UtcDateTime));
        var handler = new TaskGetHandler(h.Tasks, h.Receipts, h.Replies);
        var result = await handler.HandleGetAsync("111",
            h.Update(text: "/get a31f9c"), "/get a31f9c", Now);
        Assert.Equal(200, result.StatusCode);
        var payload = Assert.Single(h.Transport.Payloads);
        Assert.Equal(TelegramPayloadKind.Document, payload.Kind);
        Assert.Equal("task-a31f9c00aa.json", payload.FileName);
        Assert.Contains(prompt, payload.Content, StringComparison.Ordinal);
        Assert.Contains("a31f9c00aa", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_MissingDeletedAndForeign_AllNotFound()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "dead0000ee", status: TaskState.Deleted));
        h.Tasks.Seed(TestRecords.Task(owner: "222", id: "f001aa00bb"));
        var handler = new TaskGetHandler(h.Tasks, h.Receipts, h.Replies);
        foreach (var text in new[] { "/get zzz999000", "/get dead0000ee", "/get f001aa00bb" })
        {
            var result = await handler.HandleGetAsync("111", h.Update(text: text), text, Now);
            Assert.Contains("task_not_found", result.RepliesSent[0]);
        }
    }

    [Fact]
    public async Task Update_PromptChange_BumpsRevision()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"prompt": "New prompt"}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("revision 2", result.RepliesSent[0]);
        Assert.Contains("prompt updated", result.RepliesSent[0]);
        var record = await h.Tasks.GetAsync("111", "a31f9c00aa");
        Assert.Equal(2, record!.Revision);
        Assert.Equal("New prompt", record.Definition.Prompt);
    }

    [Fact]
    public async Task Update_UnchangedPatch_AnswersUnchangedWithoutBump()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c00aa {"prompt": "Water the plants"}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("unchanged", result.RepliesSent[0]);
        Assert.Equal(1, (await h.Tasks.GetAsync("111", "a31f9c00aa"))!.Revision);
    }

    [Fact]
    public async Task Update_NullResetsOverride_AndFailedTasksReject()
    {
        var h = new Harness();
        var seeded = TestRecords.Task(owner: "111", id: "a31f9c00aa") with
        {
            Definition = TestRecords.Task(owner: "111", id: "x").Definition with
            {
                Parameters = new TaskParameters(null, "high", null, null, null),
            },
        };
        h.Tasks.Seed(seeded);
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"parameters": {"reasoningEffort": null}}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("inherited default", result.RepliesSent[0]);
        Assert.Null((await h.Tasks.GetAsync("111", "a31f9c00aa"))!.Definition.Parameters.ReasoningEffort);

        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "bad0000aa", status: TaskState.Failed));
        const string failedText = """/update bad0000aa {"prompt": "x"}""";
        var failed = await handler.HandleUpdateAsync("111", h.Update(updateId: 9, text: failedText),
            failedText, Now);
        Assert.Contains("Failed tasks", failed.RepliesSent[0]);
    }

    [Fact]
    public async Task Update_CompletedPromptOnly_CannotReactivate()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "c0mp1e0000", status: TaskState.Completed));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update c0mp1e {"prompt": "Another prompt"}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("replacement schedule or relaxed limits", result.RepliesSent[0]);
    }

    [Fact]
    public async Task Update_ScheduleReplacement_SignalsAndRestarts()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"schedule": {"cron": "0 30 9 * * *"}}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("revision 2", result.RepliesSent[0]);
        var seeded = await h.Tasks.GetAsync("111", "a31f9c00aa");
        Assert.Contains(seeded!.InstanceId, h.Orchestrations.SignaledInstances);
        Assert.Contains(seeded.InstanceId, h.Orchestrations.StartedTaskInstances);
        Assert.Equal(Now.UtcDateTime, seeded.WaterlineUtc);
    }

    [Fact]
    public async Task Update_Reactivation_ResumesCompletedTask()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "c0mp1e0000", status: TaskState.Completed));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update c0mp1e {"schedule": {"cron": "0 30 9 * * *"}}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("revision 2", result.RepliesSent[0]);
        var record = await h.Tasks.GetAsync("111", "c0mp1e0000");
        Assert.Equal(TaskState.Active, record!.Status);
        Assert.Null(record.StopReason);
        Assert.Equal(Now.UtcDateTime, record.WaterlineUtc);
        Assert.Contains(record.InstanceId, h.Orchestrations.StartedTaskInstances);
    }

    [Fact]
    public async Task Update_ReactivationWithoutFutureWork_IsRejected()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "c0mp1e0000", status: TaskState.Completed));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update c0mp1e {"schedule": {"once": ["2020-01-01T09:00:00"]}}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("schedule_date_invalid", result.RepliesSent[0]);
        Assert.Equal(TaskState.Completed, (await h.Tasks.GetAsync("111", "c0mp1e0000"))!.Status);
    }

    [Fact]
    public async Task Update_ReplacementDuringActiveRun_DefersWaterline()
    {
        var h = new Harness();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var seeded = TestRecords.Task(owner: "111", id: "a31f9c00aa") with
        {
            ActiveClaim = new OccurrenceClaim(scheduled, 1, "Water the plants",
                "None", "low", true, "1", scheduled),
        };
        h.Tasks.Seed(seeded);
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"schedule": {"cron": "0 30 9 * * *"}}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("revision 2", result.RepliesSent[0]);
        var record = await h.Tasks.GetAsync("111", "a31f9c00aa");
        Assert.Equal(seeded.WaterlineUtc, record!.WaterlineUtc);
        Assert.Equal(Now.UtcDateTime, record.PendingActivationUtc);
    }

    [Fact]
    public async Task Update_ConcurrentEdit_AsksForRetry()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskUpdateHandler(
            new ConflictingTaskStore(h.Tasks), h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"prompt": "New prompt"}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("Concurrent edit", result.RepliesSent[0]);
    }

    [Fact]
    public async Task Delete_TombstonesTerminatesAndHides()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskDeleteHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        var result = await handler.HandleDeleteAsync("111",
            h.Update(text: "/delete a31f9c"), "/delete a31f9c", Now);
        Assert.Contains("deleted", result.RepliesSent[0]);
        Assert.Contains("recurring-a31f9c00aa", h.Orchestrations.TerminatedInstances);
        Assert.Empty(await h.Tasks.ListOwnedAsync("111"));
        var again = await handler.HandleDeleteAsync("111",
            h.Update(updateId: 8, text: "/delete a31f9c"), "/delete a31f9c", Now);
        Assert.Contains("task_not_found", again.RepliesSent[0]);
    }

    private static TaskRecord ClaimedTask(
        string id, OccurrenceClaim claim, DateTime? waterline = null)
    {
        var water = waterline ?? new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var definition = new TaskDefinition("Hourly",
            new TaskSchedule("0 0 * * * *", []), "UTC", TaskParameters.Empty);
        return new TaskRecord("111", id, 111L, Ids.DeriveInstanceId(id), definition,
            1, TaskState.Active, null, water, 1, null, water, water, ActiveClaim: claim);
    }

    private static OccurrenceClaim ListClaim(
        DateTime? scheduled = null, DateTime? nextRetry = null) =>
        new(scheduled ?? new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            1, "Hourly", "None", "low", false, "1",
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), 0, nextRetry);

    [Fact]
    public async Task List_ClaimedRunningWithStaleLease_Recovering()
    {
        var h = new Harness();
        var claim = ListClaim();
        h.Tasks.Seed(ClaimedTask("a31f9c00aa", claim));
        h.Clock.Now = Now;
        h.Occurrences.SeedStaleClaim("111", "a31f9c00aa", claim.ScheduledUtc);
        await h.StartLifecycleAsync("a31f9c00aa");
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Contains("recovering", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ClaimedRunningWithFreshLease_Executing()
    {
        var h = new Harness();
        var claim = ListClaim();
        h.Tasks.Seed(ClaimedTask("a31f9c00aa", claim));
        await h.Occurrences.SeedReceiptAsync(new DeliveryReceipt("111", "a31f9c00aa",
            new DateTimeOffset(claim.ScheduledUtc, TimeSpan.Zero),
            OccurrenceExecution.StatusGenerating, 0, null,
            UpdatedUtc: Now, ClaimId: "worker-1"));
        await h.StartLifecycleAsync("a31f9c00aa");
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Contains("executing", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ClaimedWithFutureRetryNote_Retrying()
    {
        var h = new Harness();
        h.Tasks.Seed(ClaimedTask("a31f9c00aa",
            ListClaim(nextRetry: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))));
        await h.StartLifecycleAsync("a31f9c00aa");
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Contains("retrying", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_StaleClaimOverFinishedWork_Unknown()
    {
        var h = new Harness();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        h.Tasks.Seed(ClaimedTask("a31f9c00aa", ListClaim(scheduled: scheduled), waterline: scheduled));
        await h.StartLifecycleAsync("a31f9c00aa");
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Contains("unknown", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_FailedRuntime_ShowsFailed()
    {
        // Stored status is active but the orchestration failed: the row
        // must read failed, not active.
        var h = new Harness();
        h.Tasks.Seed(ClaimedTask("a31f9c00aa", ListClaim()));
        var record = await h.Tasks.GetAsync("111", "a31f9c00aa");
        h.Orchestrations.MarkFailed(record!.InstanceId);
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Contains("failed", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ClaimlessTaskWithoutRuntime_ShowsUnknown()
    {
        // Active with no claim and no orchestration health to read: the
        // stored active status must not display as healthy.
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Contains("unknown", result.RepliesSent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("active", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_UnreadableRuntime_ShowsUnknown()
    {
        // A transient health-read failure degrades the row to unknown,
        // not to a redeliverable 503.
        var orch = new Mock<IOrchestrationClient>();
        orch.Setup(o => o.GetRuntimeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("durable down"));
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, orch.Object);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("unknown", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_BerlinGapExpiration_RejectedInStoredZone()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa", timezone: "Europe/Berlin"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"parameters": {"expiresAt": "2027-03-28T02:30:00"}}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Contains("02:30", result.RepliesSent[0], StringComparison.Ordinal);
        Assert.Equal(1, (await h.Tasks.GetAsync("111", "a31f9c00aa"))!.Revision);
    }

    [Fact]
    public async Task Update_BerlinValidExpiration_ResolvesInStoredZone()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa", timezone: "Europe/Berlin"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"parameters": {"expiresAt": "2027-03-28T03:30:00"}}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Equal(200, result.StatusCode);
        var row = await h.Tasks.GetAsync("111", "a31f9c00aa");
        Assert.Equal(new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc), row!.ExpiresAtUtc);
        Assert.Equal(7L, row.LastAppliedUpdateId);
    }

    [Fact]
    public async Task Update_RedeliveredCommand_AcknowledgesWithoutReapplying()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"prompt": "Rewritten after crash"}""";
        var first = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Equal(200, first.StatusCode);
        // Redelivery finds the command record and acknowledges as-is.
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Equal(200, result.StatusCode);
        var row = await h.Tasks.GetAsync("111", "a31f9c00aa");
        Assert.Equal("Rewritten after crash", row!.Definition.Prompt);
        Assert.Equal(2, row.Revision);
        Assert.NotNull(await h.Tasks.GetAppliedCommandAsync("111", "a31f9c00aa", 7));
        Assert.True((await h.Receipts.GetAsync("111", 7))!.CommandCompleted);
    }

    [Fact]
    public async Task Update_UnchangedWithPendingBoundary_ResignalsLifecycle()
    {
        var h = new Harness();
        var claim = ListClaim();
        h.Tasks.Seed(ClaimedTask("a31f9c00aa", claim) with
        {
            PendingActivationUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        });
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"prompt": "Hourly"}""";
        var result = await handler.HandleUpdateAsync("111", h.Update(text: text), text, Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains(Ids.DeriveInstanceId("a31f9c00aa"), h.Orchestrations.SignaledInstances);
    }

    [Fact]
    public async Task Update_RetryAfterLaterEdit_DoesNotOverwrite()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string textA = """/update a31f9c {"prompt": "Edit A"}""";
        const string textB = """/update a31f9c {"prompt": "Edit B"}""";
        var resultA = await handler.HandleUpdateAsync(
            "111", h.Update(updateId: 7, text: textA), textA, Now);
        Assert.Equal(200, resultA.StatusCode);
        var resultB = await handler.HandleUpdateAsync(
            "111", h.Update(updateId: 8, text: textB), textB, Now);
        Assert.Equal(200, resultB.StatusCode);
        // Retry of A after B committed: acknowledges, keeps B.
        var retryA = await handler.HandleUpdateAsync(
            "111", h.Update(updateId: 7, text: textA), textA, Now);
        Assert.Equal(200, retryA.StatusCode);
        var row = await h.Tasks.GetAsync("111", "a31f9c00aa");
        Assert.Equal("Edit B", row!.Definition.Prompt);
        Assert.Equal(3, row.Revision);
    }

    [Fact]
    public async Task Update_ReactivationRedelivery_EnsuresLifecycleRunning()
    {
        var h = new Harness();
        var completed = TestRecords.Task(owner: "111", id: "a31f9c00aa",
            status: TaskState.Completed);
        h.Tasks.Seed(completed);
        // First attempt committed reactivation but crashed before startup.
        Assert.True(await h.Tasks.TryReactivateTaskAsync("111", "a31f9c00aa", 1,
            completed.Definition, 2, null, appliedUpdateId: 7, updatedAtUtc: Now.UtcDateTime));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"prompt": "Water the plants"}""";
        var result = await handler.HandleUpdateAsync(
            "111", h.Update(updateId: 7, text: text), text, Now);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains(Ids.DeriveInstanceId("a31f9c00aa"), h.Orchestrations.StartedTaskInstances);
        Assert.Equal(TaskState.Active, (await h.Tasks.GetAsync("111", "a31f9c00aa"))!.Status);
    }

    [Fact]
    public async Task Update_SignalFailure_PropagatesFor503Mapping()
    {
        var h = new Harness();
        var claim = ListClaim();
        h.Tasks.Seed(ClaimedTask("a31f9c00aa", claim) with
        {
            PendingActivationUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        });
        h.Orchestrations.OnSignal = _ =>
            Task.FromException(new TransientStoreException("durable down"));
        var handler = new TaskUpdateHandler(h.Tasks, h.Receipts, h.Orchestrations, h.Replies);
        const string text = """/update a31f9c {"prompt": "Hourly"}""";
        await Assert.ThrowsAsync<TransientStoreException>(() =>
            handler.HandleUpdateAsync("111", h.Update(text: text), text, Now));
    }

    [Fact]
    public async Task Delete_TombstoneConflict_Reports503WithoutTerminating()
    {
        var record = TestRecords.Task(owner: "111", id: "a31f9c00aa");
        var store = new Mock<ITaskStore>();
        store.Setup(s => s.ListOwnedAsync("111", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TaskRecord>)new[] { record });
        store.Setup(s => s.GetAsync("111", "a31f9c00aa", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
        store.Setup(s => s.TryMarkDeletedAsync("111", "a31f9c00aa",
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var orchestrations = new FakeOrchestrations();
        var transport = new FakeTelegramSender();
        var handler = new TaskDeleteHandler(store.Object, new FakeReceiptStore(),
            orchestrations, new BotReplySender(transport));
        var update = new IncomingUpdate(7, 111, 111L, TelegramUpdateKind.Message, "/delete a31f9c");
        var result = await handler.HandleDeleteAsync("111", update, "/delete a31f9c", Now);
        Assert.Equal(503, result.StatusCode);
        Assert.Empty(orchestrations.TerminatedInstances);
        Assert.Empty(transport.Payloads);
    }

    [Fact]
    public async Task List_NonCompact_SendsNativeTableWithStackedRecord()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        await h.StartLifecycleAsync("a31f9c00aa");
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        var table = Assert.Single(h.Transport.Payloads, p => p.Kind == TelegramPayloadKind.Table);
        Assert.Contains("a31f9c", table.Content, StringComparison.Ordinal);
        Assert.Contains("\"is_header\":true", table.Content, StringComparison.Ordinal);
        Assert.Contains("\"align\":\"left\"", table.Content, StringComparison.Ordinal);
        Assert.Contains("\"valign\":\"top\"", table.Content, StringComparison.Ordinal);
        // The reply record carries the stacked fallback, not pipe text.
        Assert.Contains("active", result.RepliesSent[0], StringComparison.Ordinal);
        Assert.Contains("·", result.RepliesSent[0], StringComparison.Ordinal);
        Assert.DoesNotContain("|", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_Compact_SendsStackedTextOnly()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa"));
        await h.StartLifecycleAsync("a31f9c00aa");
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111",
            h.Update(updateId: 8, text: "/list 1 compact"), "/list 1 compact", Now);
        Assert.DoesNotContain(h.Transport.Payloads, p => p.Kind == TelegramPayloadKind.Table);
        Assert.Contains("·", result.RepliesSent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_GroupsByTimezone()
    {
        var h = new Harness();
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "b82d0400cc", timezone: "Europe/Moscow"));
        h.Tasks.Seed(TestRecords.Task(owner: "111", id: "a31f9c00aa", timezone: "UTC"));
        var handler = new TaskListHandler(h.Tasks, h.Receipts, h.Replies, h.Occurrences, h.Orchestrations);
        var result = await handler.HandleListAsync("111", h.Update(text: "/list"), "/list", Now);
        Assert.Contains("Europe/Moscow", result.RepliesSent[0]);
        var compact = await handler.HandleListAsync("111",
            h.Update(updateId: 8, text: "/list 1 compact"), "/list 1 compact", Now);
        Assert.Contains("·", compact.RepliesSent[0]);
    }

    private sealed class ConflictingTaskStore(FakeTaskStore inner) : ITaskStore
    {
        public Task<TaskRecord?> GetAsync(string o, string id, CancellationToken ct = default) =>
            inner.GetAsync(o, id, ct);
        public Task InsertAsync(TaskRecord r, CancellationToken ct = default) =>
            inner.InsertAsync(r, ct);
        public Task<bool> TryUpdateDefinitionAsync(string o, string id, int rev, TaskState st,
            TaskDefinition d, int next, DateTime? exp, bool admit, long applied, DateTime upd,
            CancellationToken ct = default) => Task.FromResult(false);
        public Task<TaskAppliedCommand?> GetAppliedCommandAsync(string o, string id, long u,
            CancellationToken ct = default) => inner.GetAppliedCommandAsync(o, id, u, ct);
        public Task<bool> TryReactivateTaskAsync(string o, string id, int rev,
            TaskDefinition d, int next, DateTime? exp, long applied, DateTime upd,
            CancellationToken ct = default) =>
            inner.TryReactivateTaskAsync(o, id, rev, d, next, exp, applied, upd, ct);
        public Task<bool> TryClaimOccurrenceAsync(string o, string id, int rev,
            OccurrenceClaim c, int n, DateTime u,
            CancellationToken ct = default) => inner.TryClaimOccurrenceAsync(o, id, rev, c, n, u, ct);
        public Task<bool> TryUpdateClaimRetryAsync(string o, string id, DateTime s,
            int f, DateTime? n, DateTime u,
            CancellationToken ct = default) => inner.TryUpdateClaimRetryAsync(o, id, s, f, n, u, ct);
        public Task<bool> TryCompleteOccurrenceAsync(string o, string id, DateTime s, int rev,
            DateTime w, DateTime? p, string? r, DateTime? ra, TaskState? st, DateTime u,
            CancellationToken ct = default) =>
            inner.TryCompleteOccurrenceAsync(o, id, s, rev, w, p, r, ra, st, u, ct);
        public Task<bool> TryMarkTaskFailedAsync(string o, string id, DateTime u,
            CancellationToken ct = default) => inner.TryMarkTaskFailedAsync(o, id, u, ct);
        public Task<bool> TryFinishTaskAsync(string o, string id, int rev, string r, DateTime ra,
            TaskState st, DateTime u, CancellationToken ct = default) =>
            inner.TryFinishTaskAsync(o, id, rev, r, ra, st, u, ct);
        public Task<IReadOnlyList<TaskRecord>> ListOwnedAsync(string o, CancellationToken ct = default) =>
            inner.ListOwnedAsync(o, ct);
        public Task<bool> TryMarkDeletedAsync(string o, string id, DateTime u,
            CancellationToken ct = default) => inner.TryMarkDeletedAsync(o, id, u, ct);
    }
}
