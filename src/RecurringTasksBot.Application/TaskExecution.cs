// Phase 5 combined schedule, one waterline, and stop limits
// (docs/Spec.Phase5.md section 3): the union of cron occurrences and explicit
// UTC dates under one waterline; first-reached stop limit wins.
namespace RecurringTasksBot.Application;

public static class TaskStopReasons
{
    public const string Expired = "expired";
    public const string MaxOccurrences = "max_occurrences";
    public const string ScheduleExhausted = "schedule_exhausted";
}

// Frozen at the first successful claim; retries reuse it, including after a
// crash before generation. No claimed occurrence lacks its settings snapshot.
// The prompt is frozen too: a newer definition never rewrites retained work.
public sealed record OccurrenceClaim(
    DateTime ScheduledUtc,
    int TaskRevision,
    string Prompt,
    string MemoryMode,
    string ReasoningEffort,
    bool WebSearch,
    string DefaultsRevision,
    DateTime ClaimedAtUtc,
    // Durable retry visibility: set when an attempt fails with a scheduled
    // retry, cleared when the next attempt starts or the occurrence
    // completes. Powers the /list "retrying" state across restarts.
    int FailedAttempts = 0,
    DateTime? NextRetryUtc = null,
    // Frozen schedule snapshot: the retained run keeps the claimed cron,
    // explicit dates, and zone even when the definition is replaced later.
    string? ScheduleCron = null,
    string? Timezone = null,
    IReadOnlyList<string>? ScheduleOnce = null);

public abstract record TaskExecutionDecision;
public sealed record ResumeClaimDecision(OccurrenceClaim Claim) : TaskExecutionDecision;
public sealed record StartOccurrenceDecision(
    DateTime ScheduledUtc,
    EffectiveTaskSettings Effective,
    int TaskRevision,
    string DefaultsRevision) : TaskExecutionDecision;
public sealed record WaitDecision(DateTime WakeAtUtc) : TaskExecutionDecision;
public sealed record CompleteTaskDecision(string StopReason, DateTime StopAtUtc) : TaskExecutionDecision;

public static class TaskExecutionPlanner
{
    // First stop limit wins against actual closure times: when the count
    // closed before the expiration instant, it closes as max_occurrences
    // even if expiration is observed by the time a later plan runs. A null
    // result defers to ordinary planning.
    public static CompleteTaskDecision? CountFilledStop(
        int startedOccurrences, int? maxOccurrences,
        DateTime? countClosedAtUtc, DateTime? expiresAtUtc)
    {
        if (maxOccurrences is null || startedOccurrences < maxOccurrences.Value)
            return null;
        if (countClosedAtUtc is null)
            return null;
        if (expiresAtUtc is not null && countClosedAtUtc.Value >= expiresAtUtc.Value)
            return null;
        return new CompleteTaskDecision(TaskStopReasons.MaxOccurrences, countClosedAtUtc.Value);
    }

    // One planning pass. Resume claimed work first; otherwise check stop
    // limits, run only the latest due instant (older due instants are
    // consumed with it), or wait for the earliest future instant or
    // expiration. Closed limits are checked before natural exhaustion so a
    // final count-limited run reports max_occurrences.
    public static TaskExecutionDecision Plan(
        TaskRecord record, TaskDefaults globals, DateTime nowUtc)
    {
        if (record.Status != TaskState.Active)
            throw new InvalidOperationException("Only active tasks admit execution planning.");
        if (!TaskTimezones.TryResolve(record.Definition.Timezone, out var zone) || zone is null)
            throw new InvalidOperationException($"Stored timezone '{record.Definition.Timezone}' is not usable.");

        if (record.ActiveClaim is not null)
            return new ResumeClaimDecision(record.ActiveClaim);

        // A persisted count closure beats expiration observed later: the
        // count closed first at its saved instant. Rows without one fall
        // through to the legacy observation ordering below.
        if (CountFilledStop(record.StartedOccurrences,
            record.Definition.Parameters.MaxOccurrences,
            record.CountClosedAtUtc, record.ExpiresAtUtc) is { } closed)
            return closed;
        if (record.ExpiresAtUtc is not null && nowUtc >= record.ExpiresAtUtc.Value)
            return new CompleteTaskDecision(TaskStopReasons.Expired, record.ExpiresAtUtc.Value);

        var maxOccurrences = record.Definition.Parameters.MaxOccurrences;
        if (maxOccurrences is not null && record.StartedOccurrences >= maxOccurrences.Value)
            return new CompleteTaskDecision(TaskStopReasons.MaxOccurrences, nowUtc);

        var latestDue = TaskScheduleResolver.GetLatestDueMergedUtc(
            record.Definition.Schedule.Cron, record.Definition.Schedule.Once,
            zone, nowUtc, record.WaterlineUtc);
        if (latestDue is not null &&
            (record.ExpiresAtUtc is null || latestDue.Value < record.ExpiresAtUtc.Value))
        {
            var effective = TaskDefinitionParser.ResolveEffective(record.Definition, globals);
            ValidateEffective(effective);
            return new StartOccurrenceDecision(
                latestDue.Value, effective, record.Revision, globals.Revision);
        }

        var boundary = Max(record.WaterlineUtc, nowUtc);
        var next = TaskScheduleResolver.GetNextMergedUtc(
            record.Definition.Schedule.Cron, record.Definition.Schedule.Once, zone, boundary);
        if (next is null)
            return new CompleteTaskDecision(TaskStopReasons.ScheduleExhausted, nowUtc);
        if (record.ExpiresAtUtc is not null && next.Value >= record.ExpiresAtUtc.Value)
            return new WaitDecision(record.ExpiresAtUtc.Value);
        return new WaitDecision(next.Value);
    }

    // Effective settings are validated before a new occurrence starts; the
    // adapter receives them and must not override them again.
    public static void ValidateEffective(EffectiveTaskSettings effective)
    {
        if (!effective.MemoryMode.Equals(TaskMemoryModes.IncludePreviousMessage, StringComparison.Ordinal) &&
            !effective.MemoryMode.Equals(TaskMemoryModes.None, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown effective memoryMode '{effective.MemoryMode}'.");
        if (!TaskReasoningEfforts.All.Contains(effective.ReasoningEffort))
            throw new InvalidOperationException(
                $"Unknown effective reasoningEffort '{effective.ReasoningEffort}'.");
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
