using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.FunctionApp;

public sealed record LoadRequest(string OwnerId, string OperationId);

public sealed record LoadedOperation(string Status, string CronExpression);

public sealed record AttemptRequest(
    string OwnerId, string OperationId, long ScheduledUtcTicks, int AttemptIndex);

// Responsibility-based Durable names, used consistently by the host, the
// orchestration client, and tests. Renaming a function invalidates
// in-flight histories; live instances from older code are not supported.
public static class DurableNames
{
    public const string Orchestrator = "RecurrenceLifecycle";
    public const string LoadOperation = "LoadOperation";
    public const string DeliverOccurrence = "DeliverOccurrence";
}

// One durable orchestration per operation: compute the next UTC NCRONTAB
// occurrence, await a durable timer, invoke the delivery activity, then
// advance and ContinueAsNew to bound history while retaining the instance
// ID and scheduling state. Orchestration code is deterministic: only the
// context clock, pure scheduling, timers, and activities.
//
// One RecurrenceState shape serves as both the initial input and the
// ContinueAsNew payload. A null NextScheduledUtc marks a new operation:
// only that first invocation loads the operation and computes the first
// due time; every continuation consumes the persisted scheduling state.
// Existing outcome values retain their numeric values. The WaitingForClaim
// outcome schedules a durable wait without using a retry.
public sealed class RecurrenceFunctions(
    IOperationStore operations,
    IOccurrenceRepository occurrences,
    ExecuteOccurrenceHandler handler)
{
    [Function(DurableNames.Orchestrator)]
    public async Task Run(
        [OrchestrationTrigger] TaskOrchestrationContext ctx)
    {
        var state = ctx.GetInput<RecurrenceState>()!;
        if (state.NextScheduledUtc is null)
        {
            var loaded = await ctx.CallActivityAsync<LoadedOperation?>(
                DurableNames.LoadOperation, new LoadRequest(state.OwnerId, state.OperationId));
            if (loaded is null ||
                loaded.Status == OperationStatusNames.Deleted ||
                loaded.Status == OperationStatusNames.Failed)
                return;

            var initial = NcrontabSchedule.Parse(loaded.CronExpression);
            state = state with
            {
                CronExpression = loaded.CronExpression,
                NextScheduledUtc = Scheduler.FirstAfterActivation(initial, ctx.CurrentUtcDateTime),
            };
        }

        var schedule = NcrontabSchedule.Parse(state.CronExpression);
        while (true)
        {
            var next = state.NextScheduledUtc!.Value;
            var now = ctx.CurrentUtcDateTime;
            if (next > now)
            {
                await ctx.CreateTimer(next, CancellationToken.None);
                now = ctx.CurrentUtcDateTime;
            }

            // After downtime, deliver at most one late occurrence.
            var occurrence = next;
            if (occurrence <= now)
                occurrence = Scheduler.SingleCatchUp(schedule, occurrence, now) ?? occurrence;

            var attempt = await ctx.CallActivityAsync<SingleAttemptResult>(
                DurableNames.DeliverOccurrence,
                new AttemptRequest(state.OwnerId, state.OperationId,
                    occurrence.Ticks, 0));
            var index = 0;
            while (OccurrenceExecution.ShouldRetry(attempt, index))
            {
                var wait = attempt.RetryIn ?? TimeSpan.FromSeconds(30);
                await ctx.CreateTimer(ctx.CurrentUtcDateTime + wait, CancellationToken.None);
                index = OccurrenceExecution.NextAttemptIndex(attempt, index);
                attempt = await ctx.CallActivityAsync<SingleAttemptResult>(
                    DurableNames.DeliverOccurrence,
                    new AttemptRequest(state.OwnerId, state.OperationId,
                        occurrence.Ticks, index));
            }

            if (attempt.Outcome is SingleAttemptOutcome.OperationFailed
                or SingleAttemptOutcome.SkippedStopped)
                return;

            now = ctx.CurrentUtcDateTime;
            state = Scheduler.AdvanceAfterOccurrence(state, schedule, occurrence, now);
            ctx.ContinueAsNew(state);
            return;
        }
    }

    [Function(DurableNames.LoadOperation)]
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
    [Function(DurableNames.DeliverOccurrence)]
    public async Task<SingleAttemptResult> Deliver(
        [ActivityTrigger] AttemptRequest request, FunctionContext context)
    {
        var logger = context.GetLogger<RecurrenceFunctions>();
        var scheduled = new DateTime(request.ScheduledUtcTicks, DateTimeKind.Utc);
        var started = DateTimeOffset.UtcNow;
        logger.LogInformation(
            "Occurrence attempt {OperationId} {ScheduledUtc:u} #{AttemptIndex} started.",
            request.OperationId, scheduled, request.AttemptIndex);

        var result = await handler.ExecuteAttemptAsync(
            request.OwnerId, request.OperationId, scheduled, request.AttemptIndex,
            context.CancellationToken);

        // Outcome logging carries IDs, timings, and available usage only:
        // never prompts, answers, reasoning, raw payloads, or secrets.
        // Telemetry only: a logging read must not fail a completed attempt.
        DeliveryReceipt? receipt;
        try
        {
            receipt = await occurrences.GetReceiptAsync(request.OwnerId, request.OperationId, scheduled);
        }
        catch (Exception)
        {
            receipt = null;
        }
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
        // Content-free occurrence telemetry: versions, plan progress, and
        // separate retry counters. Never prompts, answers, or payloads.
        if (receipt?.PayloadSchemaVersion == StorageLimits.SchemaVersion)
            logger.LogInformation(
                "Occurrence progress {OperationId} {ScheduledUtc:u} {Outcome} answer {AnswerVersion} " +
                "plan {PlanVersion} parts {SentParts}/{TotalParts} instruction {InstructionVersion} " +
                "deliveryFailures {DeliveryFailures} storageConflicts {StorageConflicts}.",
                request.OperationId, scheduled, result.Outcome,
                receipt.AnswerVersion ?? "none", receipt.PlanVersion ?? "none",
                receipt.SentParts, receipt.TotalParts,
                receipt.InstructionVersion ?? "none",
                receipt.DeliveryTransientFailures, receipt.StorageConflicts);
        return result;
    }
}
