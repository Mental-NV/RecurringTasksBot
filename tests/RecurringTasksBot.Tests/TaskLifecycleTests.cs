using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RecurringTasksBot.Application;
using RecurringTasksBot.FunctionApp;

namespace RecurringTasksBot.Tests;

// Phase 5 lifecycle orchestration: planner-driven activities with
// revision-guarded writes, plus the orchestrator's stop/wait paths.
public sealed class TaskLifecycleTests
{
    private static readonly DateTime Scheduled = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private sealed class StubRunner(SingleAttemptResult result) : ITaskOccurrenceRunner
    {
        public Task<SingleAttemptResult> RunAsync(string ownerId, string taskId,
            OccurrenceClaim claim, int attemptIndex, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private static TaskLifecycleFunctions Functions(
        FakeTaskStore? tasks = null, ITaskOccurrenceRunner? runner = null) =>
        new(tasks ?? new FakeTaskStore(), TaskDefaults.Default,
            runner ?? new StubRunner(
                new SingleAttemptResult(SingleAttemptOutcome.Sent, 1, null, null)));

    private static TaskRecord Hourly(string id = "task1",
        TaskState status = TaskState.Active, int started = 0,
        DateTime? waterline = null, OccurrenceClaim? claim = null)
    {
        var water = waterline ?? new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var definition = new TaskDefinition("Hourly",
            new TaskSchedule("0 0 * * * *", []), "UTC", TaskParameters.Empty);
        return new TaskRecord("u1", id, 111L, Ids.DeriveInstanceId(id), definition,
            1, status, null, water, started, null, water, water, ActiveClaim: claim);
    }

    [Fact]
    public async Task PlanTask_MissingOrInactive_Stops()
    {
        var functions = Functions();
        Assert.Equal(TaskPlanOutcome.Stop,
            (await functions.Plan(new TaskRef("u1", "nope"), null!)).Kind);
        var tasks = new FakeTaskStore();
        tasks.Seed(Hourly(status: TaskState.Completed));
        Assert.Equal(TaskPlanOutcome.Stop,
            (await new TaskLifecycleFunctions(tasks, TaskDefaults.Default,
                new StubRunner(new SingleAttemptResult(SingleAttemptOutcome.Sent, 0, null, null)))
                .Plan(new TaskRef("u1", "task1"), null!)).Kind);
    }

    [Fact]
    public async Task PlanTask_DueWork_StartsWithFrozenSnapshot()
    {
        var tasks = new FakeTaskStore();
        tasks.Seed(Hourly());
        var before = DateTime.UtcNow;
        var plan = await Functions(tasks).Plan(new TaskRef("u1", "task1"), null!);
        Assert.Equal(TaskPlanOutcome.StartOccurrence, plan.Kind);
        Assert.NotNull(plan.Start);
        Assert.True(plan.Start.ScheduledUtc <= DateTime.UtcNow);
        Assert.True(plan.Start.ScheduledUtc >= before.AddMinutes(-61));
        Assert.Equal(0, plan.Start.ScheduledUtc.Minute);
        Assert.Equal(0, plan.Start.ScheduledUtc.Second);
        Assert.Equal("Hourly", plan.Start.Effective.Prompt);
        Assert.Equal(1, plan.Start.TaskRevision);
    }

    [Fact]
    public async Task PlanTask_Exhausted_CompletesWithRevision()
    {
        var tasks = new FakeTaskStore();
        tasks.Seed(Hourly() with
        {
            Definition = Hourly().Definition with
            {
                Schedule = new TaskSchedule(null, ["2026-01-01T07:00:00"], true),
            },
            WaterlineUtc = new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc),
        });
        var plan = await Functions(tasks).Plan(new TaskRef("u1", "task1"), null!);
        Assert.Equal(TaskPlanOutcome.CompleteTask, plan.Kind);
        Assert.Equal("schedule_exhausted", plan.Complete!.StopReason);
        Assert.Equal(1, plan.ExpectedRevision);
    }

    [Fact]
    public async Task ClaimAndComplete_CommitOnce_WithStopReason()
    {
        var tasks = new FakeTaskStore();
        tasks.Seed(Hourly());
        var functions = Functions(tasks);
        var plan = await functions.Plan(new TaskRef("u1", "task1"), null!);
        var claim = await functions.Claim(
            new TaskClaimRequest("u1", "task1", plan.Start!), null!);
        Assert.NotNull(claim);
        Assert.Equal("Hourly", claim.Prompt);
        Assert.Equal(1, (await tasks.GetAsync("u1", "task1"))!.StartedOccurrences);

        // A concurrent revision move defeats a stale second claim.
        Assert.Null(await functions.Claim(
            new TaskClaimRequest("u1", "task1", plan.Start! with { TaskRevision = 9 }), null!));

        Assert.True(await functions.Complete(
            new TaskCompleteRequest("u1", "task1", claim.ScheduledUtc), null!));
        var done = await tasks.GetAsync("u1", "task1");
        Assert.Null(done!.ActiveClaim);
        Assert.Equal(claim.ScheduledUtc, done.WaterlineUtc);
    }

    [Fact]
    public async Task Complete_WithClosedLimit_PersistsStopAndStatus()
    {
        var tasks = new FakeTaskStore();
        var limited = Hourly() with
        {
            Definition = Hourly().Definition with
            {
                Parameters = new TaskParameters(null, null, null, null, 1),
            },
        };
        tasks.Seed(limited);
        var functions = Functions(tasks);
        var plan = await functions.Plan(new TaskRef("u1", "task1"), null!);
        var claim = await functions.Claim(
            new TaskClaimRequest("u1", "task1", plan.Start!), null!);
        Assert.NotNull(claim);
        Assert.True(await functions.Complete(
            new TaskCompleteRequest("u1", "task1", claim.ScheduledUtc), null!));
        var done = await tasks.GetAsync("u1", "task1");
        Assert.Equal(TaskState.Completed, done!.Status);
        Assert.Equal("max_occurrences", done.StopReason);
    }

    [Fact]
    public async Task FinishTask_ClosesWithRevisionGuard()
    {
        var tasks = new FakeTaskStore();
        tasks.Seed(Hourly());
        var functions = Functions(tasks);
        var at = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(await functions.Finish(
            new TaskFinishRequest("u1", "task1", 1, "expired", at), null!));
        var done = await tasks.GetAsync("u1", "task1");
        Assert.Equal(TaskState.Completed, done!.Status);
        Assert.Equal("expired", done.StopReason);
        Assert.Equal(at, done.StopReasonAtUtc);
        Assert.False(await functions.Finish(
            new TaskFinishRequest("u1", "task1", 1, "expired", at), null!));
    }

    private sealed class RecordingContext : TaskOrchestrationContext
    {
        private readonly TaskLifecycleInput _input;
        private readonly Queue<Func<object>> _plans;
        public readonly List<string> Calls = new();

        public RecordingContext(TaskLifecycleInput input, IEnumerable<Func<object>> plans)
        {
            _input = input;
            _plans = new Queue<Func<object>>(plans);
        }

        public override string InstanceId => "test-instance";
        public override bool IsReplaying => false;
        public override TaskName Name => new("TaskLifecycle");
        public override ParentOrchestrationInstance? Parent => null;
        public override DateTime CurrentUtcDateTime =>
            new(2026, 1, 1, 10, 20, 0, DateTimeKind.Utc);
        protected override ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public override T GetInput<T>() where T : default => (T)(object)_input!;
        public override Guid NewGuid() => Guid.NewGuid();
        public override void SendEvent(string instanceId, string eventName, object payload) =>
            throw new NotSupportedException();
        public override void SetCustomStatus(object? customStatus) { }
        public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken)
        {
            Calls.Add($"event:{eventName}");
            return Task.FromResult((T)(object)true);
        }

        public override Task<TResult> CallActivityAsync<TResult>(
            TaskName name, object? input, TaskOptions? options)
        {
            Calls.Add($"activity:{name.Name}");
            return Task.FromResult((TResult)_plans.Dequeue()());
        }

        public override Task<TResult> CallSubOrchestratorAsync<TResult>(
            TaskName orchestratorName, object? input, TaskOptions? options) =>
            throw new NotSupportedException();
        public override void ContinueAsNew(object? newInput, bool preserveUnprocessedEvents = false) =>
            Calls.Add("continue-as-new");
        public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken)
        {
            Calls.Add("timer");
            return Task.FromResult(Task.CompletedTask).ContinueWith(_ => { });
        }
    }

    [Fact]
    public async Task Orchestrator_StoppedTask_ReturnsAfterPlan()
    {
        var functions = Functions();
        var ctx = new RecordingContext(new TaskLifecycleInput("u1", "task1"),
            [() => new TaskPlanOutcome(TaskPlanOutcome.Stop)]);
        await functions.RunLifecycleAsync(ctx);
        Assert.Equal(["activity:PlanTask"], ctx.Calls);
    }

    [Fact]
    public async Task Orchestrator_WaitThenStop_WakesOnEvent()
    {
        var functions = Functions();
        var wake = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        var ctx = new RecordingContext(new TaskLifecycleInput("u1", "task1"),
            [
                () => new TaskPlanOutcome(TaskPlanOutcome.Wait, WakeAtUtc: wake),
                () => new TaskPlanOutcome(TaskPlanOutcome.Stop),
            ]);
        await functions.RunLifecycleAsync(ctx);
        Assert.Contains("activity:PlanTask", ctx.Calls);
        Assert.Contains("timer", ctx.Calls);
        Assert.Contains("event:TaskUpdated", ctx.Calls);
        Assert.Equal(2, ctx.Calls.Count(c => c == "activity:PlanTask"));
    }

    [Fact]
    public async Task Orchestrator_FinishNotCommitted_ReplansWithBackoff()
    {
        var complete = new TaskPlanOutcome(TaskPlanOutcome.CompleteTask,
            Complete: new CompleteTaskDecision("expired", Scheduled.AddHours(1)),
            ExpectedRevision: 1);
        var functions = Functions();
        var ctx = new RecordingContext(new TaskLifecycleInput("u1", "task1"),
            [
                () => complete,
                () => false,
                () => new TaskPlanOutcome(TaskPlanOutcome.Stop),
            ]);
        await functions.RunLifecycleAsync(ctx);
        Assert.Contains("activity:FinishTask", ctx.Calls);
        Assert.Contains("timer", ctx.Calls);
        Assert.Equal(2, ctx.Calls.Count(c => c == "activity:PlanTask"));
    }

    [Fact]
    public async Task Orchestrator_HundredPasses_RestartsHistory()
    {
        var wait = new TaskPlanOutcome(TaskPlanOutcome.Wait,
            WakeAtUtc: Scheduled.AddHours(1));
        var functions = Functions();
        var ctx = new RecordingContext(new TaskLifecycleInput("u1", "task1"),
            Enumerable.Range(0, TaskLifecycleNames.MaxPassesPerExecution)
                .Select(_ => (Func<object>)(() => wait)));
        await functions.RunLifecycleAsync(ctx);
        Assert.Contains("continue-as-new", ctx.Calls);
        Assert.Equal(TaskLifecycleNames.MaxPassesPerExecution,
            ctx.Calls.Count(c => c == "activity:PlanTask"));
    }

    [Fact]
    public async Task Plan_TransientRead_WaitsAndReplans()
    {
        var store = new Mock<ITaskStore>();
        store.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("tables down"));
        var functions = new TaskLifecycleFunctions(store.Object, TaskDefaults.Default,
            new StubRunner(new SingleAttemptResult(SingleAttemptOutcome.Sent, 0, null, null)));
        var plan = await functions.Plan(new TaskRef("u1", "task1"), null!);
        Assert.Equal(TaskPlanOutcome.Wait, plan.Kind);
        Assert.NotNull(plan.WakeAtUtc);
    }

    [Fact]
    public async Task Plan_PermanentFailure_ParksFailedInsteadOfStranding()
    {
        var tasks = new FakeTaskStore();
        tasks.Seed(Hourly() with
        {
            Definition = Hourly().Definition with { Timezone = "EST" },
        });
        var functions = Functions(tasks);
        var plan = await functions.Plan(new TaskRef("u1", "task1"), null!);
        Assert.Equal(TaskPlanOutcome.Stop, plan.Kind);
        Assert.Equal(TaskState.Failed, (await tasks.GetAsync("u1", "task1"))!.Status);
    }

    [Fact]
    public async Task Complete_CountFilledBeforeExpiration_ClosesMaxOccurrences()
    {
        var tasks = new FakeTaskStore();
        var scheduled = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(scheduled, 1, "Hourly", "None", "low", true, "1", scheduled);
        tasks.Seed(Hourly(started: 2) with
        {
            ActiveClaim = claim,
            ExpiresAtUtc = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Definition = Hourly().Definition with
            {
                Parameters = Hourly().Definition.Parameters with { MaxOccurrences = 2 },
            },
        });
        var functions = Functions(tasks);
        Assert.True(await functions.Complete(
            new TaskCompleteRequest("u1", "task1", scheduled), null!));
        var done = await tasks.GetAsync("u1", "task1");
        Assert.Equal(TaskState.Completed, done!.Status);
        Assert.Equal(TaskStopReasons.MaxOccurrences, done.StopReason);
    }

    [Fact]
    public async Task CompleteAfterExpiration_StillClosesEarlierCount()
    {
        // Final claim 09:00, expiration 10:00, completion observed later:
        // the persisted closure wins over the observed expiration.
        var tasks = new FakeTaskStore();
        var nine = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var deadline = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(nine, 1, "Hourly", "None", "low", true, "1", nine);
        tasks.Seed(Hourly(started: 1, claim: claim) with
        {
            ExpiresAtUtc = deadline,
            Definition = Hourly().Definition with
            {
                Parameters = Hourly().Definition.Parameters with { MaxOccurrences = 1 },
            },
            CountClosedAtUtc = nine,
        });
        var functions = Functions(tasks);
        Assert.True(await functions.Complete(
            new TaskCompleteRequest("u1", "task1", nine), null!));
        var done = await tasks.GetAsync("u1", "task1");
        Assert.Equal(TaskState.Completed, done!.Status);
        Assert.Equal(TaskStopReasons.MaxOccurrences, done.StopReason);
        Assert.Equal(nine, done.StopReasonAtUtc);
    }

    [Fact]
    public async Task Orchestrator_StartClaimRunComplete_LoopsToStop()
    {
        var scheduled = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var claim = new OccurrenceClaim(scheduled, 1, "Hourly", "None", "low", true, "1", scheduled);
        var start = new StartOccurrenceDecision(scheduled,
            new EffectiveTaskSettings("Hourly", "0 0 * * * *", [], "UTC", "None", "low", true, null, null),
            1, "1");
        var functions = Functions();
        var ctx = new RecordingContext(new TaskLifecycleInput("u1", "task1"),
            [
                () => new TaskPlanOutcome(TaskPlanOutcome.StartOccurrence, Start: start),
                () => (object)claim,
                () => new SingleAttemptResult(SingleAttemptOutcome.Sent, 1, null, null),
                () => true,
                () => new TaskPlanOutcome(TaskPlanOutcome.Stop),
            ]);
        await functions.RunLifecycleAsync(ctx);
        Assert.Contains("activity:PlanTask", ctx.Calls);
        Assert.Contains("activity:ClaimTask", ctx.Calls);
        Assert.Contains("activity:RunTask", ctx.Calls);
        Assert.Contains("activity:CompleteTask", ctx.Calls);
    }
}
