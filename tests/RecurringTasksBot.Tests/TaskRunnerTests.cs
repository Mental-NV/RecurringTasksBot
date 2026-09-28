using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Tests;

// Phase 5 occurrence execution: frozen effective settings reach the model,
// and task rows project onto the occurrence pipeline.
public sealed class TaskRunnerTests
{
    private static LlmRequest Request(string? effort = null, bool? search = null) =>
        new([new ChatMessage("user", "hi")], 131072, effort, search);

    [Fact]
    public void EffortMapping_CoversTaskTokens()
    {
        Assert.Equal("low", OpenRouterLlmExecutor.MapReasoningEffort("low"));
        Assert.Equal("medium", OpenRouterLlmExecutor.MapReasoningEffort("med"));
        Assert.Equal("high", OpenRouterLlmExecutor.MapReasoningEffort("high"));
        Assert.Equal("xhigh", OpenRouterLlmExecutor.MapReasoningEffort("xhigh"));
        Assert.Equal("max", OpenRouterLlmExecutor.MapReasoningEffort("max"));
        Assert.Throws<InvalidOperationException>(() =>
            OpenRouterLlmExecutor.MapReasoningEffort("ultra"));
    }

    [Fact]
    public void RequestJson_UsesPerRequestOverrides()
    {
        var json = OpenRouterRequestBuilder.BuildRequestJson(
            TestLlm.Provider(), TestLlm.Execution(),
            [new ChatMessage("user", "hi")], Request("med", false));
        Assert.Contains("\"medium\"", json);
        Assert.DoesNotContain("\"tools\"", json);
        Assert.DoesNotContain("max_tool_calls", json);
    }

    [Fact]
    public void RequestJson_DefaultsKeepTools()
    {
        var json = OpenRouterRequestBuilder.BuildRequestJson(
            TestLlm.Provider(), TestLlm.Execution(), [new ChatMessage("user", "hi")]);
        Assert.Contains("\"max\"", json);
        Assert.Contains("openrouter:web_search", json);
    }

    [Fact]
    public void RequestJson_UnknownEffort_FailsWithoutDowngrade()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OpenRouterRequestBuilder.BuildRequestJson(
                TestLlm.Provider(), TestLlm.Execution(),
                [new ChatMessage("user", "hi")], Request("ultra")));
    }

    [Fact]
    public void BuildMessages_SearchDisabled_RetractsResearchDirective()
    {
        var snapshot = ExecutionMessageBuilder.Create(
            "Task", "0 0 9 * * *", "op1", DateTime.UtcNow, DateTime.UtcNow,
            null, null, null, 24000, 32768, null, out var instruction);
        var enabled = ExecutionMessageBuilder.BuildMessages(instruction, snapshot, null, true);
        var disabled = ExecutionMessageBuilder.BuildMessages(instruction, snapshot, null, false);
        Assert.Equal(2, enabled.Count);
        Assert.Equal(2, disabled.Count);
        Assert.DoesNotContain("disabled", enabled[1].Content);
        Assert.Contains("Web search is disabled", disabled[1].Content);
    }

    [Fact]
    public void Synthesis_MapsStatusPromptAndCron()
    {
        var record = TestRecords.Task(status: TaskState.Active);
        Assert.Equal(OperationStatus.Active, TaskOperationSynthesis.MapStatus(record.Status));
        Assert.Equal(OperationStatus.Deleted,
            TaskOperationSynthesis.MapStatus(TaskState.Deleted));
        Assert.Equal(OperationStatus.Failed,
            TaskOperationSynthesis.MapStatus(TaskState.Completed));
        Assert.Equal("Water the plants",
            TaskOperationSynthesis.PromptFor(record, DateTime.UtcNow));
        Assert.Equal("0 0 9 * * *", TaskOperationSynthesis.CronDisplay(record));
        var once = TestRecords.Task() with
        {
            Definition = TestRecords.Task().Definition with
            {
                Schedule = new TaskSchedule(null, ["2026-12-08T09:00:00"], true),
            },
        };
        Assert.Equal("once", TaskOperationSynthesis.CronDisplay(once));
    }

    [Fact]
    public void Synthesis_ClaimScheduleAndZone_WinForItsInstant()
    {
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var record = TestRecords.Task() with
        {
            ActiveClaim = new OccurrenceClaim(scheduled, 1, "Frozen prompt",
                "None", "low", true, "1", scheduled,
                ScheduleCron: "0 0 9 * * *", Timezone: "Europe/Berlin",
                ScheduleOnce: ["2026-01-01T09:00:00"]),
            Definition = TestRecords.Task().Definition with
            {
                Schedule = new TaskSchedule("0 0 10 * * *", []),
                Timezone = "UTC",
            },
        };
        var operation = TaskOperationSynthesis.ToOperationRecord(
            "u1", "task1", 111L, "recurring-task1", record, scheduled);
        Assert.Equal("0 0 9 * * *", operation.CronExpression);
        Assert.Equal("Europe/Berlin", operation.ScheduleTimezone);
        // Other instants keep projecting the live definition.
        var other = TaskOperationSynthesis.ToOperationRecord(
            "u1", "task1", 111L, "recurring-task1", record, scheduled.AddHours(1));
        Assert.Equal("0 0 10 * * *", other.CronExpression);
        Assert.Equal("UTC", other.ScheduleTimezone);
    }

    [Fact]
    public void Synthesis_ClaimPrompt_WinsForItsInstant()
    {
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var record = TestRecords.Task() with
        {
            ActiveClaim = new OccurrenceClaim(scheduled, 1, "Frozen prompt",
                "None", "low", true, "1", scheduled),
        };
        Assert.Equal("Frozen prompt", TaskOperationSynthesis.PromptFor(record, scheduled));
        Assert.Equal("Water the plants",
            TaskOperationSynthesis.PromptFor(record, scheduled.AddHours(1)));
    }

    private static (TaskOccurrenceRunner Runner, FakeTaskStore Tasks, FakeOperationStore Ops,
        FakeTelegramSender Sender, FakeLlmExecutor Llm) NewRunner()
    {
        var tasks = new FakeTaskStore();
        var ops = new FakeOperationStore();
        var clock = new FakeClock();
        var repo = new FakeOccurrenceRepository(ops, clock);
        var sender = new FakeTelegramSender();
        var llm = new FakeLlmExecutor();
        var runner = new TaskOccurrenceRunner(tasks, repo, sender, llm,
            TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);
        return (runner, tasks, ops, sender, llm);
    }

    private static void SeedRunnable(FakeTaskStore tasks, string id,
        OccurrenceClaim claim, string? cron = "0 0 * * * *")
    {
        var definition = new TaskDefinition("Task prompt",
            new TaskSchedule(cron, []), "UTC", new TaskParameters(null, "high", false, null, null));
        var now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        tasks.Seed(new TaskRecord("u1", id, 111L, Ids.DeriveInstanceId(id), definition,
            1, TaskState.Active, null, now, 0, null, now, now, ActiveClaim: claim));
    }

    [Fact]
    public async Task Runner_SendsFrozenClaim_WithOverrides()
    {
        var (runner, tasks, ops, sender, llm) = NewRunner();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(scheduled, 1, "Frozen prompt",
            "None", "high", false, "1", scheduled);
        SeedRunnable(tasks, "task1", claim);
        // The fake occurrence repository tracks liveness through its own
        // operation store; mirror the task row there.
        ops.Seed(new OperationRecord("u1", "task1", 111L, "0 0 * * * *", "Frozen prompt",
            OperationStatus.Active, "recurring-task1", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var result = await runner.RunAsync("u1", "task1", claim, 0);

        Assert.Equal(SingleAttemptOutcome.Sent, result.Outcome);
        Assert.Single(sender.Payloads);
        var request = Assert.Single(llm.Requests);
        Assert.Equal("high", request.ReasoningEffort);
        Assert.False(request.SearchEnabled);
        Assert.Contains("Frozen prompt", request.Messages[1].Content);
        Assert.Contains("Web search is disabled", request.Messages[^1].Content);
    }

    [Fact]
    public async Task Runner_DeletedTask_SkipsWithoutWork()
    {
        var (runner, tasks, _, sender, llm) = NewRunner();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(scheduled, 1, "Frozen prompt",
            "None", "low", true, "1", scheduled);
        SeedRunnable(tasks, "task1", claim);
        await tasks.TryMarkDeletedAsync("u1", "task1", scheduled);

        var result = await runner.RunAsync("u1", "task1", claim, 0);

        Assert.Equal(SingleAttemptOutcome.SkippedStopped, result.Outcome);
        Assert.Empty(sender.Payloads);
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task Runner_NeedRetry_NotesRetryOnClaim()
    {
        var (runner, tasks, ops, _, llm) = NewRunner();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(scheduled, 1, "Frozen prompt",
            "None", "low", true, "1", scheduled);
        SeedRunnable(tasks, "task1", claim);
        ops.Seed(new OperationRecord("u1", "task1", 111L, "0 0 * * * *", "Frozen prompt",
            OperationStatus.Active, "recurring-task1", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var calls = 0;
        llm.Responder = _ => ++calls == 1
            ? Task.FromException<LlmResult>(
                new LlmExecutionException(LlmFailureKind.Transient, "boom"))
            : Task.FromResult(FakeLlmExecutor.Answer("recovered"));

        var before = DateTime.UtcNow;
        var first = await runner.RunAsync("u1", "task1", claim, 0);

        Assert.Equal(SingleAttemptOutcome.NeedRetry, first.Outcome);
        Assert.NotNull(first.RetryIn);
        var noted = (await tasks.GetAsync("u1", "task1"))!.ActiveClaim!;
        Assert.Equal(first.Attempts, noted.FailedAttempts);
        Assert.NotNull(noted.NextRetryUtc);
        Assert.True(noted.NextRetryUtc > before);

        var second = await runner.RunAsync("u1", "task1", claim, 1);

        Assert.Equal(SingleAttemptOutcome.Sent, second.Outcome);
        var cleared = (await tasks.GetAsync("u1", "task1"))!.ActiveClaim!;
        Assert.Equal(0, cleared.FailedAttempts);
        Assert.Null(cleared.NextRetryUtc);
    }

    [Fact]
    public async Task Store_ClaimRetryNote_MismatchedClaim_ReturnsFalse()
    {
        var tasks = new FakeTaskStore();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(scheduled, 1, "Frozen prompt",
            "None", "low", true, "1", scheduled);
        SeedRunnable(tasks, "task1", claim);
        Assert.False(await tasks.TryUpdateClaimRetryAsync("u1", "nope",
            scheduled, 1, scheduled.AddMinutes(1), scheduled));
        Assert.False(await tasks.TryUpdateClaimRetryAsync("u1", "task1",
            scheduled.AddHours(1), 1, scheduled.AddMinutes(1), scheduled));
        Assert.Null((await tasks.GetAsync("u1", "task1"))!.ActiveClaim!.NextRetryUtc);
    }

    [Fact]
    public async Task Runner_SupersededClaim_Waits()
    {
        var (runner, tasks, _, sender, llm) = NewRunner();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(scheduled, 1, "Frozen prompt",
            "None", "low", true, "1", scheduled);
        SeedRunnable(tasks, "task1", claim);
        var stale = claim with { ScheduledUtc = scheduled.AddHours(-1) };

        var result = await runner.RunAsync("u1", "task1", stale, 0);

        Assert.Equal(SingleAttemptOutcome.WaitingForClaim, result.Outcome);
        Assert.Empty(sender.Payloads);
        Assert.Empty(llm.Requests);
    }
}
