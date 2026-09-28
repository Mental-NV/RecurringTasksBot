// Phase 5 task lifecycle: one orchestration per task drives the planner
// loop — plan in an activity, claim and run the latest due occurrence with
// the orchestrator-owned retry waits, commit once, and wait on a durable
// timer that update signals can wake early. History stays bounded because
// each pass reloads fresh state instead of threading it through.
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.FunctionApp;

public static class TaskLifecycleNames
{
    public const string Orchestrator = "TaskLifecycle";
    public const string PlanTask = "PlanTask";
    public const string ClaimTask = "ClaimTask";
    public const string RunTask = "RunTask";
    public const string CompleteTask = "CompleteTask";
    public const string FinishTask = "FinishTask";
    // Bounded history: restart the orchestration after this many passes.
    public const int MaxPassesPerExecution = 100;
}

public sealed class TaskLifecycleFunctions(
    ITaskStore tasks,
    TaskDefaults defaults,
    ITaskOccurrenceRunner runner)
{
    [Function(TaskLifecycleNames.Orchestrator)]
    public async Task RunLifecycleAsync(
        [OrchestrationTrigger] TaskOrchestrationContext ctx)
    {
        var input = ctx.GetInput<TaskLifecycleInput>()!;
        var taskRef = new TaskRef(input.OwnerId, input.TaskId);
        var passes = 0;
        while (true)
        {
            // History stays bounded: replay re-executes this counter
            // deterministically, so the 101st completed pass restarts the
            // orchestration with the same input instead of growing history.
            if (passes >= TaskLifecycleNames.MaxPassesPerExecution)
            {
                ctx.ContinueAsNew(input);
                return;
            }
            passes++;
            var plan = await ctx.CallActivityAsync<TaskPlanOutcome>(
                TaskLifecycleNames.PlanTask, taskRef);
            switch (plan.Kind)
            {
                case TaskPlanOutcome.Stop:
                    return;
                case TaskPlanOutcome.Wait:
                {
                    // Cancel the losing wait: an abandoned event waiter
                    // could consume a later update meant for the next wait,
                    // and an abandoned timer stays outstanding past it.
                    using var cts = new CancellationTokenSource();
                    var timer = ctx.CreateTimer(plan.WakeAtUtc!.Value, cts.Token);
                    var updated = ctx.WaitForExternalEvent<bool>(
                        TaskLifecycleEvents.TaskUpdated, cts.Token);
                    await Task.WhenAny(timer, updated);
                    cts.Cancel();
                    break;
                }
                case TaskPlanOutcome.Resume:
                case TaskPlanOutcome.StartOccurrence:
                {
                    var claim = plan.Claim;
                    if (plan.Kind == TaskPlanOutcome.StartOccurrence)
                    {
                        claim = await ctx.CallActivityAsync<OccurrenceClaim?>(
                            TaskLifecycleNames.ClaimTask,
                            new TaskClaimRequest(input.OwnerId, input.TaskId, plan.Start!));
                        if (claim is null)
                        {
                            await BackoffAsync(ctx);
                            break;
                        }
                    }

                    var run = new TaskRunRequest(input.OwnerId, input.TaskId, claim!, 0);
                    var attempt = await ctx.CallActivityAsync<SingleAttemptResult>(
                        TaskLifecycleNames.RunTask, run);
                    var index = 0;
                    while (OccurrenceExecution.ShouldRetry(attempt, index))
                    {
                        var wait = attempt.RetryIn ?? TimeSpan.FromSeconds(30);
                        await ctx.CreateTimer(ctx.CurrentUtcDateTime + wait, CancellationToken.None);
                        index = OccurrenceExecution.NextAttemptIndex(attempt, index);
                        attempt = await ctx.CallActivityAsync<SingleAttemptResult>(
                            TaskLifecycleNames.RunTask,
                            run with { AttemptIndex = index });
                    }

                    if (attempt.Outcome is SingleAttemptOutcome.OperationFailed)
                        return;
                    if (attempt.Outcome is SingleAttemptOutcome.WaitingForClaim)
                    {
                        await BackoffAsync(ctx);
                        break;
                    }

                    await ctx.CallActivityAsync<bool>(TaskLifecycleNames.CompleteTask,
                        new TaskCompleteRequest(input.OwnerId, input.TaskId, claim!.ScheduledUtc));
                    break;
                }
                case TaskPlanOutcome.CompleteTask:
                {
                    var complete = plan.Complete!;
                    var finished = await ctx.CallActivityAsync<bool>(TaskLifecycleNames.FinishTask,
                        new TaskFinishRequest(input.OwnerId, input.TaskId,
                            plan.ExpectedRevision!.Value, complete.StopReason, complete.StopAtUtc));
                    if (!finished)
                    {
                        // The terminal transition was not committed (storage
                        // failure or a racing edit): replan with a backoff
                        // instead of stranding the task half-closed.
                        await BackoffAsync(ctx);
                        break;
                    }
                    return;
                }
                default:
                    return;
            }
        }
    }

    [Function(TaskLifecycleNames.PlanTask)]
    public async Task<TaskPlanOutcome> Plan(
        [ActivityTrigger] TaskRef request, FunctionContext context)
    {
        TaskRecord? record;
        try
        {
            record = await tasks.GetAsync(request.OwnerId, request.TaskId);
        }
        catch (TransientStoreException)
        {
            // A transient read failure must not strand the lifecycle: wait
            // briefly and replan instead of throwing out of the activity.
            return new TaskPlanOutcome(TaskPlanOutcome.Wait,
                WakeAtUtc: DateTime.UtcNow.AddMinutes(1));
        }
        if (record is null || record.Status != TaskState.Active)
            return new TaskPlanOutcome(TaskPlanOutcome.Stop);
        TaskExecutionDecision decision;
        try
        {
            decision = TaskExecutionPlanner.Plan(record, defaults, DateTime.UtcNow);
        }
        catch (InvalidOperationException)
        {
            // Permanently unplannable on an active task (e.g. an unusable
            // stored timezone): park it as Failed so it stays inspectable
            // instead of stranding the lifecycle on Stop.
            try
            {
                if (await tasks.TryMarkTaskFailedAsync(
                    request.OwnerId, request.TaskId, DateTime.UtcNow))
                    return new TaskPlanOutcome(TaskPlanOutcome.Stop);
            }
            catch (TransientStoreException)
            {
            }
            return new TaskPlanOutcome(TaskPlanOutcome.Wait,
                WakeAtUtc: DateTime.UtcNow.AddMinutes(1));
        }

        return decision switch
        {
            ResumeClaimDecision resume => new TaskPlanOutcome(TaskPlanOutcome.Resume, Claim: resume.Claim),
            StartOccurrenceDecision start => new TaskPlanOutcome(
                TaskPlanOutcome.StartOccurrence, Start: start),
            WaitDecision wait => new TaskPlanOutcome(
                TaskPlanOutcome.Wait, WakeAtUtc: wait.WakeAtUtc),
            CompleteTaskDecision complete => new TaskPlanOutcome(
                TaskPlanOutcome.CompleteTask, Complete: complete, ExpectedRevision: record.Revision),
            _ => new TaskPlanOutcome(TaskPlanOutcome.Stop),
        };
    }

    [Function(TaskLifecycleNames.ClaimTask)]
    public async Task<OccurrenceClaim?> Claim(
        [ActivityTrigger] TaskClaimRequest request, FunctionContext context)
    {
        TaskRecord? record;
        try
        {
            record = await tasks.GetAsync(request.OwnerId, request.TaskId);
        }
        catch (TransientStoreException)
        {
            return null;
        }

        return await ClaimAsync(request.OwnerId, record, request.Start);
    }

    [Function(TaskLifecycleNames.RunTask)]
    public async Task<SingleAttemptResult> RunOccurrenceAsync(
        [ActivityTrigger] TaskRunRequest request, FunctionContext context)
    {
        try
        {
            return await runner.RunAsync(request.OwnerId, request.TaskId,
                request.Claim, request.AttemptIndex, context.CancellationToken);
        }
        catch (TransientStoreException)
        {
            return new SingleAttemptResult(SingleAttemptOutcome.WaitingForClaim, 0,
                OccurrenceExecution.ClaimRecheckDelay, null);
        }
    }

    [Function(TaskLifecycleNames.CompleteTask)]
    public async Task<bool> Complete(
        [ActivityTrigger] TaskCompleteRequest request, FunctionContext context)
    {
        TaskRecord? record;
        try
        {
            record = await tasks.GetAsync(request.OwnerId, request.TaskId);
        }
        catch (TransientStoreException)
        {
            return false;
        }

        if (record?.ActiveClaim?.ScheduledUtc != request.ClaimScheduledUtc)
            return false;
        var completionNow = DateTime.UtcNow;
        var prospective = record with
        {
            ActiveClaim = null,
            WaterlineUtc = Max(record.WaterlineUtc, request.ClaimScheduledUtc,
                record.PendingActivationUtc),
        };
        // First stop limit wins against actual closure times: the persisted
        // count-closure instant (claim or limit change) beats a completion
        // observed after expiration. Legacy rows without one fall back to
        // the completion instant.
        CompleteTaskDecision? stop = TaskExecutionPlanner.CountFilledStop(
            prospective.StartedOccurrences,
            prospective.Definition.Parameters.MaxOccurrences,
            record.CountClosedAtUtc ?? completionNow, prospective.ExpiresAtUtc);
        if (stop is null)
        {
            try
            {
                if (TaskExecutionPlanner.Plan(prospective, defaults, completionNow)
                    is CompleteTaskDecision complete)
                    stop = complete;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        try
        {
            return await tasks.TryCompleteOccurrenceAsync(request.OwnerId, request.TaskId,
                request.ClaimScheduledUtc, record.Revision, request.ClaimScheduledUtc,
                record.PendingActivationUtc, stop?.StopReason, stop?.StopAtUtc,
                stop is null ? null : TaskState.Completed, completionNow);
        }
        catch (TransientStoreException)
        {
            return false;
        }
    }

    [Function(TaskLifecycleNames.FinishTask)]
    public async Task<bool> Finish(
        [ActivityTrigger] TaskFinishRequest request, FunctionContext context)
    {
        try
        {
            return await tasks.TryFinishTaskAsync(request.OwnerId, request.TaskId,
                request.ExpectedRevision, request.StopReason, request.StopAtUtc,
                TaskState.Completed, DateTime.UtcNow);
        }
        catch (TransientStoreException)
        {
            return false;
        }
    }

    private async Task<OccurrenceClaim?> ClaimAsync(
        string ownerId, TaskRecord? record, StartOccurrenceDecision start)
    {
        if (record is null || record.Status != TaskState.Active)
            return null;
        var recordSchedule = record.Definition.Schedule;
        var claim = new OccurrenceClaim(
            start.ScheduledUtc, start.TaskRevision, start.Effective.Prompt,
            start.Effective.MemoryMode, start.Effective.ReasoningEffort,
            start.Effective.WebSearch, start.DefaultsRevision, DateTime.UtcNow,
            ScheduleCron: recordSchedule.Cron,
            Timezone: record.Definition.Timezone,
            ScheduleOnce: recordSchedule.Once is null ? null : [.. recordSchedule.Once]);
        try
        {
            var claimed = await tasks.TryClaimOccurrenceAsync(ownerId, record.TaskId,
                start.TaskRevision, claim, record.StartedOccurrences + 1, DateTime.UtcNow);
            return claimed ? claim : null;
        }
        catch (TransientStoreException)
        {
            return null;
        }
    }

    private static DateTime Max(DateTime a, DateTime b, DateTime? c)
    {
        var max = a >= b ? a : b;
        return c is not null && c.Value > max ? c.Value : max;
    }

    private static Task BackoffAsync(TaskOrchestrationContext ctx) =>
        ctx.CreateTimer(ctx.CurrentUtcDateTime + TimeSpan.FromMinutes(1), CancellationToken.None);
}
