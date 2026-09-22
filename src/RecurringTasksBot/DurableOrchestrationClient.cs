using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

// IOrchestrationClient over the Durable Task client. Startup uses the stable
// instance ID derived from the Telegram update, so a concurrent duplicate
// start that finds the instance already present is treated as success:
// redelivery resumes instead of duplicating.
public sealed class DurableOrchestrationClient(DurableTaskClient client) : IOrchestrationClient
{
    public const string OrchestratorName = "RecurrenceOrchestrator";

    public async Task StartAsync(string instanceId, string operationId, string ownerId,
        CancellationToken ct = default)
    {
        try
        {
            await client.ScheduleNewOrchestrationInstanceAsync(
                OrchestratorName,
                new OrchestratorInput(operationId, ownerId),
                new StartOrchestrationOptions { InstanceId = instanceId },
                ct);
        }
        catch (Exception ex) when (IsAlreadyRunning(ex))
        {
            return;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            throw new TransientStoreException($"start orchestration {instanceId}: {ex.Message}", ex);
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
