// /create execution flow: stable IDs, duplicate suppression, interrupted
// creation resume, and durably-accepted startup. Success is acknowledged
// only after orchestration startup is durably accepted.
namespace RecurringTasksBot.Core;

public enum CreateOutcome
{
    Created,
    Resumed,
    Duplicate,
}

public sealed record CreateResult(CreateOutcome Outcome, string OperationId, string InstanceId);

public sealed class CreateFlow(
    IOperationStore operations,
    IUpdateReceiptStore receipts,
    IOrchestrationClient orchestrations)
{
    public async Task<CreateResult> HandleCreateAsync(
        long userId, long chatId, long updateId,
        string commandText, string cronExpression, string text,
        DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var ownerId = userId.ToString();
        var operationId = Ids.DeriveOperationId(userId, updateId, commandText);
        var instanceId = Ids.DeriveInstanceId(operationId);

        var receipt = await receipts.GetAsync(ownerId, updateId, ct);
        if (receipt is { CommandCompleted: true })
            return new CreateResult(CreateOutcome.Duplicate,
                receipt.OperationId ?? operationId, instanceId);

        var operation = await operations.GetAsync(ownerId, operationId, ct);
        if (operation is { Status: OperationStatus.Failed } or { Status: OperationStatus.Deleted })
            return new CreateResult(CreateOutcome.Duplicate, operationId, instanceId);

        var preexistingOperation = operation is not null;

        if (receipt is null)
        {
            try
            {
                await receipts.InsertAsync(new UpdateReceipt(ownerId, updateId,
                    UpdateReceipts.BoundCommand(commandText),
                    operationId, CommandCompleted: false, ReplyDelivered: false,
                    nowUtc, nowUtc), ct);
            }
            catch (ConcurrencyConflictException)
            {
                // Concurrent duplicate won the receipt write; it owns execution.
                return new CreateResult(CreateOutcome.Duplicate, operationId, instanceId);
            }
        }

        // An incomplete receipt with an existing operation is either an
        // in-flight concurrent duplicate or an interrupted creation. Both go
        // through the idempotent resume path below: the runtime-status check
        // plus stable instance IDs mean no second recurrence is started.

        if (operation is null)
        {
            try
            {
                await operations.InsertStartingAsync(new OperationRecord(ownerId, operationId,
                    chatId, cronExpression, text, OperationStatus.Starting, instanceId,
                    null, nowUtc, nowUtc), ct);
            }
            catch (ConcurrencyConflictException)
            {
                operation = await operations.GetAsync(ownerId, operationId, ct);
            }

            operation ??= await operations.GetAsync(ownerId, operationId, ct);
        }

        if (operation is { Status: OperationStatus.Active })
            return new CreateResult(CreateOutcome.Duplicate, operationId, instanceId);

        if (preexistingOperation)
        {
            // Resume path: startup may already be durably accepted (crash
            // between Start and receipt completion). Never start a second
            // recurrence for the same operation.
            var runtimeStatus = await orchestrations.GetRuntimeStatusAsync(instanceId, ct);
            if (runtimeStatus is null)
                await orchestrations.StartAsync(instanceId, operationId, ownerId, ct);
        }
        else
        {
            // Durably accept startup under the stable instance ID. A throw
            // here leaves receipt incomplete + operation starting, so
            // Telegram redelivery (503) resumes instead of duplicating.
            // Concurrent double-start is safe: the adapter treats an
            // already-existing instance ID as success.
            await orchestrations.StartAsync(instanceId, operationId, ownerId, ct);
        }

        if (operation is not null)
            await operations.CompareAndSwapStatusAsync(ownerId, operationId,
                OperationStatus.Starting, OperationStatus.Active, null, ct);
        await receipts.MarkCompletedAsync(ownerId, updateId, operationId, ct);

        return new CreateResult(
            preexistingOperation ? CreateOutcome.Resumed : CreateOutcome.Created,
            operationId, instanceId);
    }
}
