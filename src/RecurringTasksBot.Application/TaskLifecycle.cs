// Phase 5 task lifecycle orchestration contract: one orchestration per
// task drives plan, claim, run, and completion through activities.
// Timezone and planning work stays in activities; the orchestrator sees only
// UTC instants, timers, and serializable outcomes, so replay is deterministic.
namespace RecurringTasksBot.Application;

public sealed record TaskLifecycleInput(string OwnerId, string TaskId);

public sealed record TaskRef(string OwnerId, string TaskId);

public static class TaskLifecycleEvents
{
    public const string TaskUpdated = "TaskUpdated";
}

// Concrete plan envelope: Durable serializes activity results without
// polymorphism, so the decision travels as a kind plus payloads.
public sealed record TaskPlanOutcome(
    string Kind,
    OccurrenceClaim? Claim = null,
    StartOccurrenceDecision? Start = null,
    DateTime? WakeAtUtc = null,
    CompleteTaskDecision? Complete = null,
    int? ExpectedRevision = null)
{
    public const string Stop = "stop";
    public const string Wait = "wait";
    public const string Resume = "resume";
    public const string StartOccurrence = "start";
    public const string CompleteTask = "complete";
}

public sealed record TaskRunRequest(
    string OwnerId, string TaskId, OccurrenceClaim Claim, int AttemptIndex);

public sealed record TaskClaimRequest(
    string OwnerId, string TaskId, StartOccurrenceDecision Start);

public sealed record TaskCompleteRequest(
    string OwnerId, string TaskId, DateTime ClaimScheduledUtc);

public sealed record TaskFinishRequest(
    string OwnerId, string TaskId, int ExpectedRevision,
    string StopReason, DateTime StopAtUtc);

// Task-lifecycle orchestration client: stable instance IDs make duplicate
// starts harmless, and update signals wake timer waits. Termination and
// status reuse the shared orchestration contract.
public interface ITaskOrchestrationClient : IOrchestrationClient
{
    Task StartTaskAsync(string instanceId, string ownerId, string taskId,
        CancellationToken ct = default);
    Task SignalTaskUpdatedAsync(string instanceId, CancellationToken ct = default);
}
