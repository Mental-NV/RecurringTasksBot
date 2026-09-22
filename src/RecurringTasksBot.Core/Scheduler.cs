// Orchestration scheduling policy (pure, deterministic; no I/O).
// The orchestration itself only computes times and fires Durable timers;
// Table Storage and Telegram I/O happen in activities/webhook handlers.
namespace RecurringTasksBot.Core;

public sealed record RecurrenceState(
    string InstanceId,
    string OperationId,
    string OwnerId,
    string CronExpression,
    DateTime NextScheduledUtc,
    int OccurrenceIndex);

public static class Scheduler
{
    // Activation rule: the first occurrence is strictly after activation.
    public static DateTime FirstAfterActivation(NcrontabSchedule schedule, DateTime activationUtc)
    {
        var next = schedule.GetNextOccurrence(activationUtc);
        if (next is null)
            throw new InvalidOperationException("Schedule has no future occurrence after activation.");
        return next.Value;
    }

    // After downtime: at most one late notification; skip the rest.
    // Returns the single catch-up time (latest missed occurrence) or null.
    public static DateTime? SingleCatchUp(
        NcrontabSchedule schedule, DateTime previousScheduledUtc, DateTime nowUtc)
    {
        DateTime? latestMissed = null;
        var cursor = previousScheduledUtc;
        while (true)
        {
            var next = schedule.GetNextOccurrence(cursor);
            if (next is null || next.Value > nowUtc)
                break;
            latestMissed = next.Value;
            cursor = next.Value;
            if ((cursor - previousScheduledUtc).TotalDays > NcrontabSchedule.MaxSearchDays + 1)
                break;
        }

        return latestMissed;
    }

    // Advance after a delivered (or skipped) occurrence. The returned state is
    // passed to ContinueAsNew: history is bounded while the instance ID and
    // scheduling state are retained.
    public static RecurrenceState AdvanceAfterOccurrence(
        RecurrenceState state, NcrontabSchedule schedule, DateTime occurrenceUtc, DateTime nowUtc)
    {
        var reference = occurrenceUtc > nowUtc ? occurrenceUtc : nowUtc;
        var next = schedule.GetNextOccurrence(reference)
            ?? throw new InvalidOperationException("Schedule has no further occurrence.");
        return state with
        {
            InstanceId = state.InstanceId,
            NextScheduledUtc = next,
            OccurrenceIndex = state.OccurrenceIndex + 1,
        };
    }

    public static bool IsDue(RecurrenceState state, DateTime nowUtc) =>
        state.NextScheduledUtc <= nowUtc;
}
