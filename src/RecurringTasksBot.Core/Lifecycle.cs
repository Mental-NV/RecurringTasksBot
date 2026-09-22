// Creation recovery, deletion races, and failure-status reporting.
namespace RecurringTasksBot.Core;

public enum CreationAction
{
    StartNew,
    ResumeStarting,
    AckCompleted,
    AckTerminal,
}

public static class CreationRecovery
{
    // Decides how a /create update is handled given stored state:
    // - no receipt/op: start new (stable IDs derived from the update).
    // - op still `starting`, startup never durably accepted: resume with the
    //   same operation/instance IDs (interrupted-creation recovery, including
    //   webhook redelivery); never a second recurrence.
    // - receipt completed: ack without new work (duplicate/concurrent update).
    // - op failed/deleted: ack without restarting (explicit restart only via
    //   the operator script after the cause is resolved).
    public static CreationAction Decide(
        UpdateReceipt? receipt,
        OperationRecord? operation,
        bool orchestrationAccepted)
    {
        if (operation is { Status: OperationStatus.Failed } or { Status: OperationStatus.Deleted })
            return CreationAction.AckTerminal;
        if (receipt is { CommandCompleted: true })
            return CreationAction.AckCompleted;
        if (operation is { Status: OperationStatus.Starting } && !orchestrationAccepted)
            return CreationAction.ResumeStarting;
        if (operation is { Status: OperationStatus.Starting } && orchestrationAccepted)
            return CreationAction.AckCompleted;
        if (operation is { Status: OperationStatus.Active })
            return CreationAction.AckCompleted;
        return CreationAction.StartNew;
    }
}

public static class DeletionHandler
{
    // Order matters: persist deletion BEFORE terminating the orchestration,
    // so concurrent init/delivery observes the tombstone. Repeated deletion
    // is harmless (idempotent no-op returning false when already deleted).
    public static async Task<bool> DeleteAsync(
        IOperationStore operations,
        IOrchestrationClient orchestrations,
        string ownerId,
        string operationId,
        CancellationToken ct = default)
    {
        var op = await operations.GetAsync(ownerId, operationId, ct);
        if (op is null)
            return false;
        if (op.Status == OperationStatus.Deleted)
            return false;

        var swapped = await operations.CompareAndSwapStatusAsync(
            ownerId, operationId, op.Status, OperationStatus.Deleted, null, ct);
        if (!swapped)
            return false;

        if (op.InstanceId is not null)
        {
            try
            {
                await orchestrations.TerminateAsync(op.InstanceId, ct);
            }
            catch
            {
                // Tombstone already persisted; termination is best-effort.
            }
        }

        return true;
    }
}

public static class FailureReporter
{
    public const int MaxSummaryLength = 280;

    public static string Summarise(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "failed";
        var trimmed = detail.Trim();
        return trimmed.Length <= MaxSummaryLength ? trimmed : trimmed[..MaxSummaryLength];
    }

    // Reconciles stored status with Durable runtime status on demand:
    // unexpected orchestration failures surface as failed in /list.
    // Failure of one occurrence is distinct from failure of the operation.
    public static (OperationStatus ListStatus, string? ErrorSummary) ResolveListStatus(
        OperationRecord operation,
        string? durableRuntimeStatus)
    {
        if (operation.Status is OperationStatus.Failed or OperationStatus.Deleted)
            return (operation.Status, Summarise(operation.FailureSummary));

        if (durableRuntimeStatus is "Failed" or "Terminated")
            return (OperationStatus.Failed,
                Summarise(operation.FailureSummary ?? $"orchestration {durableRuntimeStatus}"));

        return (operation.Status, Summarise(operation.FailureSummary));
    }
}
