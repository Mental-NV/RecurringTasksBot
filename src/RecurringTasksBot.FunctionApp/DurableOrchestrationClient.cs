using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.FunctionApp;

// IOrchestrationClient over the Durable Task client. Startup uses the stable
// instance ID derived from the Telegram update, so a concurrent duplicate
// start that finds the instance already present is treated as success:
// redelivery resumes instead of duplicating.
public sealed class DurableOrchestrationClient(DurableTaskClient client)
    : IOrchestrationClient, ITaskOrchestrationClient
{
    public async Task StartTaskAsync(string instanceId, string ownerId, string taskId,
        CancellationToken ct = default)
    {
        try
        {
            await ScheduleTaskAsync(instanceId, ownerId, taskId, ct);
            return;
        }
        catch (Exception ex) when (IsAlreadyRunning(ex))
        {
            // A live instance owns the ID; a completed history must be
            // purged before the ID can run again (e.g. reactivation).
            var status = await TryGetStatusAsync(instanceId, ct);
            if (status is not ("Completed" or "Failed" or "Terminated" or null))
            {
                // Starting a running instance never wakes its old timer, so
                // raise the update event: replacement boundaries take effect
                // promptly instead of at the stale wake time.
                await SignalTaskUpdatedAsync(instanceId, ct);
                return;
            }
            try
            {
                await client.PurgeInstanceAsync(instanceId, ct);
            }
            catch
            {
                return;
            }

            try
            {
                await ScheduleTaskAsync(instanceId, ownerId, taskId, ct);
            }
            catch (Exception retry) when (IsAlreadyRunning(retry))
            {
                return;
            }
            catch (Exception retry) when (IsTransient(retry))
            {
                throw new TransientStoreException(
                    $"start task orchestration {instanceId}: {retry.Message}", retry);
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            throw new TransientStoreException($"start task orchestration {instanceId}: {ex.Message}", ex);
        }
    }

    private Task ScheduleTaskAsync(string instanceId, string ownerId, string taskId,
        CancellationToken ct) =>
        client.ScheduleNewOrchestrationInstanceAsync(
            TaskLifecycleNames.Orchestrator,
            new TaskLifecycleInput(ownerId, taskId),
            new StartOrchestrationOptions { InstanceId = instanceId },
            ct);

    private async Task<string?> TryGetStatusAsync(string instanceId, CancellationToken ct)
    {
        try
        {
            var metadata = await client.GetInstanceAsync(instanceId, ct);
            return metadata?.RuntimeStatus.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }

    public async Task SignalTaskUpdatedAsync(string instanceId, CancellationToken ct = default)
    {
        // Wake a timer wait so replacement schedules and relaxed limits take
        // effect promptly. Missing instances fall through to a fresh start,
        // but a failed coordination request must surface: swallowing it
        // loses the wake with nothing persisted to retry it. Callers let the
        // dispatcher map this to a 503 so Telegram redelivery retries.
        try
        {
            await client.RaiseEventAsync(instanceId, TaskLifecycleEvents.TaskUpdated, true, ct);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            throw new TransientStoreException(
                $"signal task orchestration {instanceId}: {ex.Message}", ex);
        }
        catch
        {
            // Best-effort: the start below covers absent instances.
        }
    }

    public async Task TerminateAsync(string instanceId, CancellationToken ct = default)
    {
        try
        {
            await client.TerminateInstanceAsync(instanceId, (object?)null, ct);
        }
        catch
        {
            // Best-effort: the tombstone is already persisted.
        }
    }

    public async Task<string?> GetRuntimeStatusAsync(string instanceId,
        CancellationToken ct = default)
    {
        try
        {
            var metadata = await client.GetInstanceAsync(instanceId, ct);
            return metadata?.RuntimeStatus.ToString();
        }
        catch
        {
            return null;
        }
    }

    public async Task PurgeHistoryAsync(string instanceId, CancellationToken ct = default)
    {
        try
        {
            await client.PurgeInstanceAsync(instanceId, ct);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static bool IsAlreadyRunning(Exception ex) =>
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException or HttpRequestException;
}
