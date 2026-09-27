// Test-only attempt driver: mirrors the orchestrator retry loop over the
// single production entry point without Durable timers.
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public static class AttemptLoop
{
    public static async Task<SingleAttemptResult> RunUntilDone(
        ExecuteOccurrenceHandler handler,
        string ownerId, string operationId, DateTime scheduledUtc,
        int maxAttempts = 10)
    {
        var attempt = new SingleAttemptResult(SingleAttemptOutcome.NeedRetry, 0, null, null);
        var index = 0;
        while (true)
        {
            attempt = await handler.ExecuteAttemptAsync(ownerId, operationId, scheduledUtc, index);
            if (!OccurrenceExecution.ShouldRetry(attempt, index) || index >= maxAttempts)
                return attempt;
            index = OccurrenceExecution.NextAttemptIndex(attempt, index);
        }
    }
}
