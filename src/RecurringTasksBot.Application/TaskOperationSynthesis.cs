// Shared task-to-operation projection: the occurrence pipeline keys work
// by (owner, id) and reads prompt, recurrence text, and liveness from the
// operation row. Tasks have no operation rows; the repository and the
// command-side source project the task row instead, with the frozen claim
// prompt winning over the live definition for its own instant.
namespace RecurringTasksBot.Application;

public static class TaskOperationSynthesis
{
    // Non-active tasks stop pipeline work, same as deleted/failed operations.
    public static OperationStatus MapStatus(TaskState state) => state switch
    {
        TaskState.Active => OperationStatus.Active,
        TaskState.Deleted => OperationStatus.Deleted,
        TaskState.Failed => OperationStatus.Failed,
        TaskState.Completed => OperationStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string PromptFor(TaskRecord task, DateTime scheduledUtc) =>
        task.ActiveClaim?.ScheduledUtc == scheduledUtc && task.ActiveClaim.Prompt.Length > 0
            ? task.ActiveClaim.Prompt
            : task.Definition.Prompt ?? string.Empty;

    private static bool IsClaimedInstant(TaskRecord task, DateTime scheduledUtc) =>
        task.ActiveClaim?.ScheduledUtc == scheduledUtc;

    public static string CronDisplay(TaskRecord task) =>
        task.Definition.Schedule.Cron ?? "once";

    public static string CronFor(TaskRecord task, DateTime scheduledUtc) =>
        IsClaimedInstant(task, scheduledUtc) && task.ActiveClaim!.ScheduleCron is not null
            ? task.ActiveClaim.ScheduleCron
            : CronDisplay(task);

    public static string TimezoneFor(TaskRecord task, DateTime scheduledUtc) =>
        IsClaimedInstant(task, scheduledUtc) && task.ActiveClaim!.Timezone is not null
            ? task.ActiveClaim.Timezone
            : task.Definition.Timezone;

    public static OperationRecord ToOperationRecord(
        string ownerId, string taskId, long chatId, string instanceId,
        TaskRecord task, DateTime scheduledUtc) =>
        new(
            ownerId,
            taskId,
            chatId,
            CronFor(task, scheduledUtc),
            PromptFor(task, scheduledUtc),
            MapStatus(task.Status),
            instanceId,
            null,
            new DateTimeOffset(DateTime.SpecifyKind(task.CreatedAtUtc, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(task.UpdatedAtUtc, DateTimeKind.Utc)),
            TimezoneFor(task, scheduledUtc));
}
