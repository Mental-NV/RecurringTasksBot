// Azure Tables implementation of the Phase 5 task store contract.
// Owner-scoped reads (PartitionKey = Telegram user ID); conditional writes
// carry the read ETag so a concurrent writer surfaces as a 412 conflict and
// the caller retries instead of duplicating work.
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Persistence;

public sealed class TableTaskStore(TableClients clients) : TableStoreBase(clients), ITaskStore
{
    public async Task<TaskRecord?> GetAsync(string ownerId, string taskId,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            return TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "get task");
        }
    }

    public async Task InsertAsync(TaskRecord record, CancellationToken ct = default)
    {
        try
        {
            await Table.AddEntityAsync(TaskRowCodec.ToEntity(record), ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new ConcurrencyConflictException("task already exists");
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "insert task");
        }
    }

    public async Task<TaskAppliedCommand?> GetAppliedCommandAsync(string ownerId, string taskId,
        long updateId, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.TaskCommand(taskId, updateId), cancellationToken: ct);
            return TaskRowCodec.FromCommandEntity(ownerId, taskId, updateId, entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "get applied command");
        }
    }

    public async Task<bool> TryUpdateDefinitionAsync(string ownerId, string taskId,
        int expectedRevision, TaskState expectedStatus, TaskDefinition definition, int nextRevision,
        DateTime? expiresAtUtc, bool admitFutureWork, long appliedUpdateId,
        DateTime updatedAtUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
            if (record.Revision != expectedRevision || record.Status != expectedStatus)
                return false;
            // Boundaries come from the guarded fresh row: replacement admits
            // future work from the commit time, a retained run holds the
            // advance pending, and progress never moves backward.
            var boundary = admitFutureWork
                ? Max(record.WaterlineUtc, updatedAtUtc)
                : record.WaterlineUtc;
            DateTime newWaterline;
            DateTime? newPending;
            if (record.ActiveClaim is null)
            {
                newWaterline = boundary;
                newPending = null;
            }
            else
            {
                newWaterline = record.WaterlineUtc;
                newPending = record.PendingActivationUtc;
                if (admitFutureWork && (newPending is null || boundary > newPending.Value))
                    newPending = boundary;
            }
            // A limit change that fills the count closes it at commit time;
            // relaxing the limit reopens it. The earliest closure wins.
            var updateMax = definition.Parameters.MaxOccurrences;
            var updateFilled = updateMax is not null &&
                updateMax.Value <= record.StartedOccurrences;
            var updated = record with
            {
                Definition = definition,
                Revision = nextRevision,
                ExpiresAtUtc = expiresAtUtc,
                WaterlineUtc = newWaterline,
                PendingActivationUtc = newPending,
                LastAppliedUpdateId = appliedUpdateId,
                CountClosedAtUtc = updateFilled
                    ? record.CountClosedAtUtc ?? DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc)
                    : null,
                UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
            };
            // One transaction: the mutation plus its command record. A
            // redelivered command finds the record and acknowledges instead
            // of re-applying over a later edit.
            var command = new TaskAppliedCommand(ownerId, appliedUpdateId, taskId,
                nextRevision, DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc));
            await Table.SubmitTransactionAsync(
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace,
                    TaskRowCodec.ToEntity(updated), entity.Value.ETag),
                new TableTransactionAction(TableTransactionActionType.Add,
                    TaskRowCodec.ToCommandEntity(command)),
            ], ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "update task definition");
        }
    }

    public async Task<bool> TryReactivateTaskAsync(string ownerId, string taskId,
        int expectedRevision, TaskDefinition definition, int nextRevision,
        DateTime? expiresAtUtc, long appliedUpdateId,
        DateTime updatedAtUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
            if (record.Revision != expectedRevision || record.Status != TaskState.Completed)
                return false;
            // Reactivation always admits future work from the commit time,
            // computed from the guarded fresh row; the pending advance is
            // superseded by the fresh admission boundary.
            var boundary = Max(record.WaterlineUtc, updatedAtUtc);
            var reactivateMax = definition.Parameters.MaxOccurrences;
            var updated = record with
            {
                Definition = definition,
                Revision = nextRevision,
                Status = TaskState.Active,
                StopReason = null,
                StopReasonAtUtc = null,
                PendingActivationUtc = null,
                ExpiresAtUtc = expiresAtUtc,
                WaterlineUtc = record.ActiveClaim is null ? boundary : record.WaterlineUtc,
                LastAppliedUpdateId = appliedUpdateId,
                CountClosedAtUtc = reactivateMax is not null &&
                    reactivateMax.Value <= record.StartedOccurrences
                    ? DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc)
                    : null,
                UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
            };
            var command = new TaskAppliedCommand(ownerId, appliedUpdateId, taskId,
                nextRevision, DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc));
            await Table.SubmitTransactionAsync(
            [
                new TableTransactionAction(TableTransactionActionType.UpdateReplace,
                    TaskRowCodec.ToEntity(updated), entity.Value.ETag),
                new TableTransactionAction(TableTransactionActionType.Add,
                    TaskRowCodec.ToCommandEntity(command)),
            ], ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "reactivate task");
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    public async Task<IReadOnlyList<TaskRecord>> ListOwnedAsync(string ownerId,
        CancellationToken ct = default)
    {
        try
        {
            var filter =
                $"PartitionKey eq '{Escape(ownerId)}' and RowKey ge 'task_' and RowKey lt 'task`'";
            var result = new List<TaskRecord>();
            await foreach (var entity in Table.QueryAsync<TableEntity>(filter,
                               cancellationToken: ct))
            {
                var record = TaskRowCodec.FromEntity(
                    ownerId, entity.RowKey[TableRowKeys.Task(string.Empty).Length..], entity);
                if (record.Status != TaskState.Deleted)
                    result.Add(record);
            }

            return result;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "list tasks");
        }
    }

    public async Task<bool> TryMarkDeletedAsync(string ownerId, string taskId,
        DateTime updatedAtUtc, CancellationToken ct = default)
    {
        // A blind tombstone: a concurrent writer winning the ETag race must
        // not lose the deletion, so re-read and retry a bounded number of
        // times before reporting failure.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var entity = await Table.GetEntityAsync<TableEntity>(
                    ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
                var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
                if (record.Status == TaskState.Deleted)
                    return true;
                var updated = record with
                {
                    Status = TaskState.Deleted,
                    UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
                };
                await Table.UpdateEntityAsync(
                    TaskRowCodec.ToEntity(updated), entity.Value.ETag, TableUpdateMode.Replace, ct);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 && attempt < 2)
            {
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                return false;
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, "delete task");
            }
        }
    }

    public async Task<bool> TryMarkTaskFailedAsync(string ownerId, string taskId,
        DateTime updatedAtUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
            if (record.Status != TaskState.Active)
                return false;
            var updated = record with
            {
                Status = TaskState.Failed,
                UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
            };
            await Table.UpdateEntityAsync(
                TaskRowCodec.ToEntity(updated), entity.Value.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "mark task failed");
        }
    }

    public async Task<bool> TryFinishTaskAsync(string ownerId, string taskId,
        int expectedRevision, string stopReason, DateTime stopReasonAtUtc, TaskState status,
        DateTime updatedAtUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
            if (record.Revision != expectedRevision || record.Status != TaskState.Active)
                return false;
            var updated = record with
            {
                Status = status,
                StopReason = stopReason,
                StopReasonAtUtc = DateTime.SpecifyKind(stopReasonAtUtc, DateTimeKind.Utc),
                UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
            };
            await Table.UpdateEntityAsync(
                TaskRowCodec.ToEntity(updated), entity.Value.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "finish task");
        }
    }

    public async Task<bool> TryClaimOccurrenceAsync(string ownerId, string taskId,
        int expectedRevision, OccurrenceClaim claim, int nextStartedCount,
        DateTime updatedAtUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
            if (!ClaimAdmits(record, expectedRevision, claim, nextStartedCount,
                DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc)))
                return false;
            // A claim that fills the count closes it now: stop ordering
            // later compares this instant against the expiration.
            var max = record.Definition.Parameters.MaxOccurrences;
            var updated = record with
            {
                ActiveClaim = claim,
                StartedOccurrences = nextStartedCount,
                CountClosedAtUtc = max is not null && nextStartedCount >= max.Value
                    ? record.CountClosedAtUtc ?? DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc)
                    : record.CountClosedAtUtc,
                UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
            };
            await Table.UpdateEntityAsync(
                TaskRowCodec.ToEntity(updated), entity.Value.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "claim occurrence");
        }
    }

    public async Task<bool> TryUpdateClaimRetryAsync(string ownerId, string taskId,
        DateTime claimScheduledUtc, int failedAttempts, DateTime? nextRetryUtc,
        DateTime updatedAtUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
            if (record.ActiveClaim?.ScheduledUtc != claimScheduledUtc)
                return false;
            if (record.ActiveClaim.FailedAttempts == failedAttempts &&
                record.ActiveClaim.NextRetryUtc == nextRetryUtc)
                return true;
            var updated = record with
            {
                ActiveClaim = record.ActiveClaim with
                {
                    FailedAttempts = failedAttempts,
                    NextRetryUtc = nextRetryUtc,
                },
                UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
            };
            await Table.UpdateEntityAsync(
                TaskRowCodec.ToEntity(updated), entity.Value.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "update claim retry note");
        }
    }

    public async Task<bool> TryCompleteOccurrenceAsync(string ownerId, string taskId,
        DateTime claimScheduledUtc, int expectedRevision, DateTime newWaterlineUtc,
        DateTime? pendingActivationUtc, string? stopReason, DateTime? stopReasonAtUtc,
        TaskState? nextStatus, DateTime updatedAtUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Task(taskId), cancellationToken: ct);
            var record = TaskRowCodec.FromEntity(ownerId, taskId, entity.Value);
            // The completion commits only against the guarded state the
            // activity planned from: same claim instant, same definition
            // revision, still active. A newer activation boundary, stop
            // decision, or deletion wins over this stale commit.
            if (record.ActiveClaim?.ScheduledUtc != claimScheduledUtc ||
                record.Revision != expectedRevision ||
                record.Status != TaskState.Active)
                return false;
            var waterline = record.WaterlineUtc;
            if (newWaterlineUtc > waterline)
                waterline = newWaterlineUtc;
            if (pendingActivationUtc is not null && pendingActivationUtc.Value > waterline)
                waterline = pendingActivationUtc.Value;
            var updated = record with
            {
                ActiveClaim = null,
                WaterlineUtc = waterline,
                PendingActivationUtc = null,
                StopReason = stopReason ?? record.StopReason,
                StopReasonAtUtc = stopReasonAtUtc ?? record.StopReasonAtUtc,
                Status = nextStatus ?? record.Status,
                UpdatedAtUtc = DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc),
            };
            await Table.UpdateEntityAsync(
                TaskRowCodec.ToEntity(updated), entity.Value.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "complete occurrence");
        }
    }

    private static bool ClaimAdmits(TaskRecord record, int expectedRevision,
        OccurrenceClaim claim, int nextStartedCount, DateTime claimTimeUtc) =>
        record.Revision == expectedRevision &&
        record.Status == TaskState.Active &&
        record.ActiveClaim is null &&
        claim.ScheduledUtc > record.WaterlineUtc &&
        nextStartedCount == record.StartedOccurrences + 1 &&
        (record.ExpiresAtUtc is null || claim.ScheduledUtc < record.ExpiresAtUtc.Value) &&
        // The plan may predate the deadline; the claim itself must not be
        // created at or after it. The deadline is exclusive for new work.
        (record.ExpiresAtUtc is null || claimTimeUtc < record.ExpiresAtUtc.Value) &&
        (record.Definition.Parameters.MaxOccurrences is null ||
            nextStartedCount <= record.Definition.Parameters.MaxOccurrences.Value);
}
