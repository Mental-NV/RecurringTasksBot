using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

public sealed record OrchestratorInput(string OperationId, string OwnerId);

public sealed record LoadRequest(string OwnerId, string OperationId);

public sealed record LoadedOperation(string Status, string CronExpression);

public sealed record AttemptRequest(
    string OwnerId, string OperationId, long ScheduledUtcTicks, int AttemptIndex);

// One durable orchestration per operation: compute the next UTC NCRONTAB
// occurrence, await a durable timer, invoke the delivery activity, then
// advance and ContinueAsNew to bound history while retaining the instance
// ID and scheduling state. Orchestration code is deterministic: only the
// context clock, pure Core scheduling, timers, and activities.
public sealed class RecurrenceFunctions(
    IOperationStore operations,
    IDeliveryReceiptStore deliveries,
    ITelegramSender sender)
{
    [Function("RecurrenceOrchestrator")]
    public async Task Run(
        [OrchestrationTrigger] TaskOrchestrationContext ctx)
    {
        var input = ctx.GetInput<OrchestratorInput>()!;
        var loaded = await ctx.CallActivityAsync<LoadedOperation?>(
            "LoadOperation", new LoadRequest(input.OwnerId, input.OperationId));
        if (loaded is null ||
            loaded.Status == OperationStatusNames.Deleted ||
            loaded.Status == OperationStatusNames.Failed)
            return;

        var schedule = NcrontabSchedule.Parse(loaded.CronExpression);
        var state = new RecurrenceState(
            ctx.InstanceId,
            input.OperationId,
            input.OwnerId,
            loaded.CronExpression,
            Scheduler.FirstAfterActivation(schedule, ctx.CurrentUtcDateTime),
            0);

        while (true)
        {
            var now = ctx.CurrentUtcDateTime;
            if (state.NextScheduledUtc > now)
            {
                await ctx.CreateTimer(state.NextScheduledUtc, CancellationToken.None);
                now = ctx.CurrentUtcDateTime;
            }

            // After downtime, deliver at most one late occurrence.
            var occurrence = state.NextScheduledUtc;
            if (occurrence <= now)
                occurrence = Scheduler.SingleCatchUp(schedule, occurrence, now) ?? occurrence;

            var attempt = await ctx.CallActivityAsync<SingleAttemptResult>(
                "DeliverOnce",
                new AttemptRequest(state.OwnerId, state.OperationId,
                    occurrence.Ticks, 0));
            var index = 0;
            while (attempt.Outcome == SingleAttemptOutcome.NeedRetry &&
                   index < DeliveryPolicy.MaxRetriesAfterInitial)
            {
                var wait = attempt.RetryIn ?? TimeSpan.FromSeconds(30);
                await ctx.CreateTimer(ctx.CurrentUtcDateTime + wait, CancellationToken.None);
                index++;
                attempt = await ctx.CallActivityAsync<SingleAttemptResult>(
                    "DeliverOnce",
                    new AttemptRequest(state.OwnerId, state.OperationId,
                        occurrence.Ticks, index));
            }

            if (attempt.Outcome is SingleAttemptOutcome.OperationFailed
                or SingleAttemptOutcome.SkippedStopped)
                return;

            now = ctx.CurrentUtcDateTime;
            state = Scheduler.AdvanceAfterOccurrence(state, schedule, occurrence, now);
            ctx.ContinueAsNew(state);
        }
    }

    [Function("LoadOperation")]
    public async Task<LoadedOperation?> Load(
        [ActivityTrigger] LoadRequest request, FunctionContext context)
    {
        var op = await operations.GetAsync(request.OwnerId, request.OperationId);
        return op is null
            ? null
            : new LoadedOperation(OperationStatusNames.ToName(op.Status), op.CronExpression);
    }

    [Function("DeliverOnce")]
    public async Task<SingleAttemptResult> Deliver(
        [ActivityTrigger] AttemptRequest request, FunctionContext context)
    {
        var handler = new DeliveryHandler(operations, deliveries, sender);
        return await handler.AttemptOnceAsync(
            request.OwnerId,
            request.OperationId,
            new DateTime(request.ScheduledUtcTicks, DateTimeKind.Utc),
            request.AttemptIndex);
    }
}
