using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
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
//
// Replay compatibility: activity names (LoadOperation, DeliverOnce), the
// AttemptRequest shape, and the RecurrenceState shape are unchanged from
// phase one. Existing outcome values retain their numeric values. The new
// WaitingForClaim outcome schedules a durable wait without using a retry.
public sealed class RecurrenceFunctions(
    IOperationStore operations,
    IDeliveryReceiptStore deliveries,
    IOccurrencePayloadStore payloads,
    ITelegramSender sender,
    ILlmPromptExecutor llm,
    LlmOptions llmOptions)
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
            while (OccurrenceExecution.ShouldRetry(attempt, index))
            {
                var wait = attempt.RetryIn ?? TimeSpan.FromSeconds(30);
                await ctx.CreateTimer(ctx.CurrentUtcDateTime + wait, CancellationToken.None);
                index = OccurrenceExecution.NextAttemptIndex(attempt, index);
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

    // Generation and delivery share one activity so the orchestration history
    // shape is unchanged. The handler persists the generated answer before
    // any Telegram send; retries reuse it.
    [Function("DeliverOnce")]
    public async Task<SingleAttemptResult> Deliver(
        [ActivityTrigger] AttemptRequest request, FunctionContext context)
    {
        var logger = context.GetLogger<RecurrenceFunctions>();
        var scheduled = new DateTime(request.ScheduledUtcTicks, DateTimeKind.Utc);
        var started = DateTimeOffset.UtcNow;
        logger.LogInformation(
            "Occurrence attempt {OperationId} {ScheduledUtc:u} #{AttemptIndex} started.",
            request.OperationId, scheduled, request.AttemptIndex);

        var handler = new DeliveryHandler(operations, deliveries, payloads, sender, llm, llmOptions);
        var result = await handler.AttemptOnceAsync(
            request.OwnerId, request.OperationId, scheduled, request.AttemptIndex,
            context.CancellationToken);

        // Outcome logging carries IDs, timings, and available usage only:
        // never prompts, answers, reasoning, raw payloads, or secrets.
        var receipt = await deliveries.GetAsync(request.OwnerId, request.OperationId, scheduled);
        logger.LogInformation(
            "Occurrence attempt {OperationId} {ScheduledUtc:u} #{AttemptIndex} {Outcome} " +
            "in {ElapsedMs}ms (attempts {Attempts}, generation {GenerationAttempts}, " +
            "provider {Provider} model {Model} promptTokens {PromptTokens} " +
            "completionTokens {CompletionTokens} searchUsed {SearchUsed} searchResults {SearchResults}).",
            request.OperationId, scheduled, request.AttemptIndex, result.Outcome,
            (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, result.Attempts,
            receipt?.GenerationAttempts ?? 0, receipt?.Provider ?? "unknown",
            receipt?.ModelName ?? "unknown", receipt?.PromptTokens ?? 0,
            receipt?.CompletionTokens ?? 0, receipt?.SearchUsed ?? false,
            receipt?.SearchResults ?? 0);
        if (receipt?.ExecutionStatus is "generating" or "failed" && receipt.ErrorSummary is { Length: > 0 })
            logger.LogWarning("LLM attempt {OperationId} {ScheduledUtc:u} generation #{GenerationAttempts}: {FailureSummary}",
                request.OperationId, scheduled, receipt.GenerationAttempts, receipt.ErrorSummary);
        return result;
    }
}
