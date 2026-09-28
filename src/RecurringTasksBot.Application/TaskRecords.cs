// Phase 5 persisted task (docs/Spec.Phase5.md sections 3-4): explicit user
// definition plus system-managed scheduling state. Runtime status, owner,
// waterline, and count stay outside the editable settings contract and both
// get-mode JSON responses.
namespace RecurringTasksBot.Application;

public enum TaskState
{
    Active,
    Completed,
    Failed,
    Deleted,
}

public static class TaskStateNames
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Deleted = "deleted";

    public static string ToName(TaskState state) => state switch
    {
        TaskState.Active => Active,
        TaskState.Completed => Completed,
        TaskState.Failed => Failed,
        TaskState.Deleted => Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static TaskState Parse(string name) => name switch
    {
        Active => TaskState.Active,
        Completed => TaskState.Completed,
        Failed => TaskState.Failed,
        Deleted => TaskState.Deleted,
        _ => throw new ArgumentException($"Unknown task status '{name}'.", nameof(name)),
    };
}

public sealed record TaskRecord(
    string OwnerId,
    string TaskId,
    long ChatId,
    string InstanceId,
    TaskDefinition Definition,
    int Revision,
    TaskState Status,
    string? StopReason,
    DateTime WaterlineUtc,
    int StartedOccurrences,
    DateTime? ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? StopReasonAtUtc = null,
    DateTime? PendingActivationUtc = null,
    OccurrenceClaim? ActiveClaim = null,
    // Command redelivery guard: the updateId whose patch this row already
    // reflects, so a redelivered command acknowledges without re-applying.
    long? LastAppliedUpdateId = null,
    // Actual count-closure instant: set when a claim fills max_occurrences
    // or a limit change does. Stop ordering compares this against the
    // expiration, not the later completion observation time.
    DateTime? CountClosedAtUtc = null);

// One row per applied update command, written atomically with the mutation
// it records. A redelivered command finds its row and acknowledges; a later
// edit carries a different updateId and is never overwritten by a retry.
public sealed record TaskAppliedCommand(
    string OwnerId,
    long UpdateId,
    string TaskId,
    int ResultRevision,
    DateTime AppliedAtUtc);

public interface ITaskStore
{
    // Owner-scoped reads enforce user isolation. Deleted rows are returned so
    // handlers map missing/deleted rows to the same task_not_found outcome;
    // rows owned by nobody else are never visible here.
    Task<TaskRecord?> GetAsync(string ownerId, string taskId, CancellationToken ct = default);
    Task InsertAsync(TaskRecord record, CancellationToken ct = default);
    // Atomic definition write guarded by the read revision (ETag check).
    // Returns false on revision mismatch so the caller can ask for a retry.
    // The expected lifecycle state is guarded too: completions do not bump
    // the revision, so without it an update could land on a newly completed
    // task and leave a replacement schedule that can never execute.
    // Waterline and pending-activation boundaries are computed from the
    // guarded fresh row in the same atomic write: replacement admits future
    // work from the commit time, a retained run holds the advance in
    // pendingActivationUtc, and progress never moves backward. Callers must
    // not pass precomputed boundaries from an older snapshot.
    // The command record commits in the same transaction: redelivery finds
    // it and acknowledges instead of re-applying over a later edit.
    Task<bool> TryUpdateDefinitionAsync(string ownerId, string taskId, int expectedRevision,
        TaskState expectedStatus, TaskDefinition definition, int nextRevision,
        DateTime? expiresAtUtc, bool admitFutureWork, long appliedUpdateId,
        DateTime updatedAtUtc, CancellationToken ct = default);
    Task<TaskAppliedCommand?> GetAppliedCommandAsync(string ownerId, string taskId, long updateId,
        CancellationToken ct = default);
    // Reactivation of a completed task through a replacement schedule or
    // relaxed limits: revision-guarded, clears stop metadata, admits future
    // work from the commit time. Only completed tasks reactivate this way.
    // The command record commits in the same transaction, as for updates.
    Task<bool> TryReactivateTaskAsync(string ownerId, string taskId, int expectedRevision,
        TaskDefinition definition, int nextRevision, DateTime? expiresAtUtc,
        long appliedUpdateId, DateTime updatedAtUtc, CancellationToken ct = default);
    // Nondeleted owner rows for listing and prefix resolution.
    Task<IReadOnlyList<TaskRecord>> ListOwnedAsync(string ownerId, CancellationToken ct = default);
    // Claim a new occurrence: checks operation liveness, task definition
    // revision, waterline, current expiration, and available count together,
    // then reserves one count slot. Returns false when the row is missing,
    // the revision moved, a claim is already active, the instant is not above
    // the waterline, a limit closes admission, or a concurrent writer won.
    // Retries resume the same receipt and never consume another slot.
    Task<bool> TryClaimOccurrenceAsync(string ownerId, string taskId, int expectedRevision,
        OccurrenceClaim claim, int nextStartedCount, DateTime updatedAtUtc,
        CancellationToken ct = default);
    // Retry visibility note on the active claim: guarded by the claim
    // instant, skipped when the note is already identical (status-only
    // writes happen on real change), cleared with (0, null) when the next
    // attempt starts. Returns false when the row is missing or the claim
    // instant no longer matches.
    Task<bool> TryUpdateClaimRetryAsync(string ownerId, string taskId,
        DateTime claimScheduledUtc, int failedAttempts, DateTime? nextRetryUtc,
        DateTime updatedAtUtc, CancellationToken ct = default);
    // Terminal commit for one occurrence: records the outcome, advances the
    // waterline once (never backward), applies a pending activation boundary,
    // and persists the stop reason with its effective time when admission
    // closes. Transient failure leaves the waterline unchanged.
    Task<bool> TryCompleteOccurrenceAsync(string ownerId, string taskId,
        DateTime claimScheduledUtc, int expectedRevision, DateTime newWaterlineUtc,
        DateTime? pendingActivationUtc, string? stopReason, DateTime? stopReasonAtUtc,
        TaskState? nextStatus, DateTime updatedAtUtc, CancellationToken ct = default);
    // Tombstone first so concurrent work observes the deletion. Repeated
    // deletion of an already-deleted row is a harmless no-op returning true;
    // missing rows return false.
    Task<bool> TryMarkDeletedAsync(string ownerId, string taskId, DateTime updatedAtUtc,
        CancellationToken ct = default);
    // Task-level unrecoverable failure (e.g. permanent recipient failure):
    // Active becomes Failed, inspectable but closed to updates and admission.
    Task<bool> TryMarkTaskFailedAsync(string ownerId, string taskId, DateTime updatedAtUtc,
        CancellationToken ct = default);
    // Revision-guarded terminal close without an active claim (expiration or
    // exhaustion observed by the planner). Returns false on revision drift.
    Task<bool> TryFinishTaskAsync(string ownerId, string taskId, int expectedRevision,
        string stopReason, DateTime stopReasonAtUtc, TaskState status, DateTime updatedAtUtc,
        CancellationToken ct = default);
}
