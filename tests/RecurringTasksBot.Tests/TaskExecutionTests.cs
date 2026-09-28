using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

// Phase 5 execution: one waterline, latest-only catch-up, frozen snapshots,
// count reserved once, stop reasons, and crash-safe claim/complete.
public sealed class TaskExecutionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 1, 1, 10, 20, 0, DateTimeKind.Utc);

    private static TaskRecord Hourly(string id = "task1", DateTime? waterline = null,
        int started = 0, TaskParameters? parameters = null, DateTime? expiresAtUtc = null,
        TaskState status = TaskState.Active, OccurrenceClaim? claim = null,
        DateTime? createdAtUtc = null)
    {
        var water = waterline ?? T0;
        var created = createdAtUtc ?? T0;
        var definition = new TaskDefinition("Hourly prompt",
            new TaskSchedule("0 0 * * * *", []), "UTC", parameters ?? TaskParameters.Empty);
        return new TaskRecord("u1", id, 111L, Ids.DeriveInstanceId(id), definition,
            1, status, null, water, started, expiresAtUtc, created, created,
            ActiveClaim: claim);
    }

    private static OccurrenceClaim ClaimFor(DateTime scheduled, int revision = 1) =>
        new(scheduled, revision, "Hourly prompt", "None", "low", true, "1", scheduled);

    [Fact]
    public void LatestOnlyCatchUp_RunsOnlyNewestDueInstant()
    {
        // Spec example: due 09:00 (cron), 09:30 (explicit), 10:00 (cron);
        // execute only 10:00Z, then advance the waterline to 10:00Z.
        var record = Hourly() with
        {
            Definition = Hourly().Definition with
            {
                Schedule = new TaskSchedule("0 0 9,10 * * *", ["2026-01-01T09:30:00"], true),
            },
        };
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now);
        var start = Assert.IsType<StartOccurrenceDecision>(decision);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), start.ScheduledUtc);
        Assert.Equal(1, start.TaskRevision);
        Assert.Equal("1", start.DefaultsRevision);
        Assert.Equal("low", start.Effective.ReasoningEffort);
    }

    [Fact]
    public void StartOccurrence_FreezesEffectiveSnapshot()
    {
        var record = Hourly(parameters: new TaskParameters(null, "high", null, null, null));
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now);
        var start = Assert.IsType<StartOccurrenceDecision>(decision);
        Assert.Equal("high", start.Effective.ReasoningEffort);
        Assert.Equal("None", start.Effective.MemoryMode);
        Assert.True(start.Effective.WebSearch);
    }

    [Fact]
    public void ExpirationDuringDowntime_CompletesWithoutCatchUp()
    {
        var record = Hourly(expiresAtUtc: new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now);
        var complete = Assert.IsType<CompleteTaskDecision>(decision);
        Assert.Equal("expired", complete.StopReason);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), complete.StopAtUtc);
    }

    [Fact]
    public void CountLimit_ReservesOnce_ThenCompletes()
    {
        var record = Hourly(parameters: new TaskParameters(null, null, null, null, 1));
        Assert.IsType<StartOccurrenceDecision>(
            TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now));
        var spent = record with { StartedOccurrences = 1 };
        var complete = Assert.IsType<CompleteTaskDecision>(
            TaskExecutionPlanner.Plan(spent, TaskDefaults.Default, Now));
        Assert.Equal("max_occurrences", complete.StopReason);
    }

    [Fact]
    public void ExpirationWinsExactTie()
    {
        var record = Hourly(
            parameters: new TaskParameters(null, null, null, null, 1),
            expiresAtUtc: new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)) with
        {
            StartedOccurrences = 1,
        };
        var complete = Assert.IsType<CompleteTaskDecision>(
            TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now));
        Assert.Equal("expired", complete.StopReason);
    }

    [Fact]
    public void ExhaustedOneTimeSchedule_Completes()
    {
        var record = Hourly() with
        {
            Definition = Hourly().Definition with
            {
                Schedule = new TaskSchedule(null, ["2026-01-01T07:00:00"], true),
            },
            WaterlineUtc = new DateTime(2026, 1, 1, 7, 0, 0, DateTimeKind.Utc),
        };
        var complete = Assert.IsType<CompleteTaskDecision>(
            TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now));
        Assert.Equal("schedule_exhausted", complete.StopReason);
    }

    [Fact]
    public void ActiveClaim_ResumesInsteadOfSelectingNewerWork()
    {
        var claim = ClaimFor(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var record = Hourly(claim: claim);
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now);
        var resume = Assert.IsType<ResumeClaimDecision>(decision);
        Assert.Equal(claim, resume.Claim);
    }

    [Fact]
    public void IdleBeforeNextOccurrence_WaitsForIt()
    {
        var record = Hourly(waterline: Now);
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now);
        var wait = Assert.IsType<WaitDecision>(decision);
        Assert.Equal(new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc), wait.WakeAtUtc);
    }

    [Fact]
    public void NextAtOrAfterExpiration_WaitsForDeadline()
    {
        var record = Hourly(waterline: Now,
            expiresAtUtc: new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc));
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default, Now);
        var wait = Assert.IsType<WaitDecision>(decision);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc), wait.WakeAtUtc);
    }

    [Fact]
    public async Task ClaimReservesCountOnce_CompleteAdvancesWaterlineOnce()
    {
        var store = new FakeTaskStore();
        store.Seed(Hourly());
        var claim = ClaimFor(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.True(await store.TryClaimOccurrenceAsync("u1", "task1", 1, claim, 1, Now));
        var claimed = await store.GetAsync("u1", "task1");
        Assert.Equal(1, claimed!.StartedOccurrences);
        Assert.Equal(claim, claimed.ActiveClaim);
        Assert.Equal(T0, claimed.WaterlineUtc);

        // A retry resumes the same receipt: no second slot.
        Assert.False(await store.TryClaimOccurrenceAsync("u1", "task1", 1, claim, 2, Now));

        var scheduled = claim.ScheduledUtc;
        Assert.True(await store.TryCompleteOccurrenceAsync("u1", "task1", scheduled, 1,
            scheduled, null, null, null, null, Now));
        var done = await store.GetAsync("u1", "task1");
        Assert.Null(done!.ActiveClaim);
        Assert.Equal(scheduled, done.WaterlineUtc);
        Assert.Equal(1, done.StartedOccurrences);

        // Terminal failure still consumes its time: a late duplicate commit
        // for the same instant cannot move the waterline backward.
        Assert.False(await store.TryCompleteOccurrenceAsync("u1", "task1", scheduled, 1,
            T0, null, null, null, null, Now));
    }

    [Fact]
    public async Task CompleteAfterScheduleEdit_AppliesPendingActivationBoundary()
    {
        var store = new FakeTaskStore();
        store.Seed(Hourly());
        var claim = ClaimFor(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.True(await store.TryClaimOccurrenceAsync("u1", "task1", 1, claim, 1, Now));
        var boundary = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(await store.TryCompleteOccurrenceAsync("u1", "task1", claim.ScheduledUtc, 1,
            claim.ScheduledUtc, boundary, null, null, null, Now));
        var done = await store.GetAsync("u1", "task1");
        Assert.Equal(boundary, done!.WaterlineUtc);
        Assert.Null(done.PendingActivationUtc);
    }

    [Fact]
    public async Task UpdateFromOlderSnapshot_NeverMovesWaterlineBackward()
    {
        var store = new FakeTaskStore();
        var nine = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = ClaimFor(nine);
        store.Seed(Hourly(waterline: nine, started: 1, claim: claim));
        var definition = Hourly().Definition;
        // A commit older than current progress still advances pending only.
        var eight = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(await store.TryUpdateDefinitionAsync("u1", "task1", 1, TaskState.Active,
            definition, 2, null, admitFutureWork: true, appliedUpdateId: 7, updatedAtUtc: eight));
        var row = await store.GetAsync("u1", "task1");
        Assert.Equal(nine, row!.WaterlineUtc);
        Assert.Equal(nine, row.PendingActivationUtc);
        // Without admission the write keeps the fresh waterline untouched.
        Assert.True(await store.TryUpdateDefinitionAsync("u1", "task1", 2, TaskState.Active,
            definition, 3, null, admitFutureWork: false, appliedUpdateId: 8, updatedAtUtc: eight));
        row = await store.GetAsync("u1", "task1");
        Assert.Equal(nine, row!.WaterlineUtc);
    }

    [Fact]
    public async Task CompleteAfterEditOrDelete_RefusesStaleCommit()
    {
        var store = new FakeTaskStore();
        var nine = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = ClaimFor(nine);
        store.Seed(Hourly(waterline: T0, started: 1, claim: claim));
        var definition = Hourly().Definition;
        // A racing edit bumps the revision: the stale completion loses.
        Assert.True(await store.TryUpdateDefinitionAsync("u1", "task1", 1, TaskState.Active,
            definition, 2, null, admitFutureWork: false, appliedUpdateId: 7, updatedAtUtc: Now));
        Assert.False(await store.TryCompleteOccurrenceAsync("u1", "task1",
            nine, 1, nine, null, null, null, null, Now));
        // A deleted task never flips to completed.
        store.Seed(Hourly(waterline: T0, started: 1, claim: claim, status: TaskState.Deleted));
        Assert.False(await store.TryCompleteOccurrenceAsync("u1", "task1",
            nine, 1, nine, null, null, null, TaskState.Completed, Now));
        var row = await store.GetAsync("u1", "task1");
        Assert.Equal(TaskState.Deleted, row!.Status);
    }

    [Fact]
    public async Task ClaimCreatedAfterExpiration_Refused()
    {
        var store = new FakeTaskStore();
        var deadline = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc);
        // Planned before the deadline for 09:00, but claimed after it.
        store.Seed(Hourly(expiresAtUtc: deadline));
        var claim = ClaimFor(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.False(await store.TryClaimOccurrenceAsync("u1", "task1", 1, claim, 1, late));
        Assert.Null((await store.GetAsync("u1", "task1"))!.ActiveClaim);
        // Claimed just before the deadline still admits.
        Assert.True(await store.TryClaimOccurrenceAsync("u1", "task1", 1, claim, 1,
            deadline.AddTicks(-1)));
    }

    [Fact]
    public void CountFilledStop_FirstLimitWins()
    {
        var expiration = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var closure = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var closed = TaskExecutionPlanner.CountFilledStop(5, 5, closure, expiration);
        Assert.NotNull(closed);
        Assert.Equal(TaskStopReasons.MaxOccurrences, closed!.StopReason);
        Assert.Equal(closure, closed.StopAtUtc);
        // Expiration already reached first: defers to ordinary planning.
        Assert.Null(TaskExecutionPlanner.CountFilledStop(5, 5, expiration, expiration));
        Assert.Null(TaskExecutionPlanner.CountFilledStop(5, 5, null, expiration));
        // Count not filled, or no count limit: no early decision.
        Assert.Null(TaskExecutionPlanner.CountFilledStop(4, 5, closure, expiration));
        Assert.Null(TaskExecutionPlanner.CountFilledStop(5, null, closure, expiration));
    }

    [Fact]
    public void IdlePlanning_PersistedClosureBeforeExpiration_StopsMaxOccurrencesAtClosure()
    {
        // Count closed at 09:00, expiration 10:00, planning resumes idle at
        // 11:00: MaxOccurrences wins at the saved closure, not expired.
        var closure = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var expiration = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var record = Hourly(started: 1,
            parameters: new TaskParameters(null, null, null, null, 1),
            expiresAtUtc: expiration) with { CountClosedAtUtc = closure };
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default,
            new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc));
        var complete = Assert.IsType<CompleteTaskDecision>(decision);
        Assert.Equal(TaskStopReasons.MaxOccurrences, complete.StopReason);
        Assert.Equal(closure, complete.StopAtUtc);
    }

    [Fact]
    public void IdlePlanning_PersistedClosureObservedBeforeExpiration_RecordsClosure()
    {
        // Same task observed at 09:30, before expiration: still stops at the
        // 09:00 closure rather than the observation time.
        var closure = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var expiration = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var record = Hourly(started: 1,
            parameters: new TaskParameters(null, null, null, null, 1),
            expiresAtUtc: expiration) with { CountClosedAtUtc = closure };
        var decision = TaskExecutionPlanner.Plan(record, TaskDefaults.Default,
            new DateTime(2026, 1, 1, 9, 30, 0, DateTimeKind.Utc));
        var complete = Assert.IsType<CompleteTaskDecision>(decision);
        Assert.Equal(TaskStopReasons.MaxOccurrences, complete.StopReason);
        Assert.Equal(closure, complete.StopAtUtc);
    }

    [Fact]
    public async Task UpdateAgainstCompletedTask_RefusedByStatusGuard()
    {
        var store = new FakeTaskStore();
        store.Seed(Hourly(status: TaskState.Completed));
        var definition = Hourly().Definition;
        // Revision matches, but the lifecycle state moved on: refused.
        Assert.False(await store.TryUpdateDefinitionAsync("u1", "task1", 1, TaskState.Active,
            definition, 2, null, admitFutureWork: true, appliedUpdateId: 9, updatedAtUtc: Now));
        var row = await store.GetAsync("u1", "task1");
        Assert.Equal(TaskState.Completed, row!.Status);
        Assert.Equal(1, row.Revision);
    }

    [Fact]
    public async Task ClaimFillingCount_PersistsClosureAtClaimTime()
    {
        var store = new FakeTaskStore();
        var deadline = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var nine = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var definition = Hourly().Definition with
        {
            Parameters = Hourly().Definition.Parameters with { MaxOccurrences = 1 },
        };
        var seeded = Hourly(expiresAtUtc: deadline) with { Definition = definition };
        store.Seed(seeded);
        var claim = ClaimFor(nine);
        Assert.True(await store.TryClaimOccurrenceAsync("u1", "task1", 1, claim, 1, nine));
        var row = await store.GetAsync("u1", "task1");
        Assert.Equal(nine, row!.CountClosedAtUtc);
    }

    [Fact]
    public async Task CompletionWithClosedLimit_PersistsStopReason()
    {
        var store = new FakeTaskStore();
        store.Seed(Hourly());
        var claim = ClaimFor(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        Assert.True(await store.TryClaimOccurrenceAsync("u1", "task1", 1, claim, 1, Now));
        Assert.True(await store.TryCompleteOccurrenceAsync("u1", "task1", claim.ScheduledUtc, 1,
            claim.ScheduledUtc, null, "max_occurrences", Now, TaskState.Completed, Now));
        var done = await store.GetAsync("u1", "task1");
        Assert.Equal(TaskState.Completed, done!.Status);
        Assert.Equal("max_occurrences", done.StopReason);
        Assert.Equal(Now, done.StopReasonAtUtc);
    }
}
