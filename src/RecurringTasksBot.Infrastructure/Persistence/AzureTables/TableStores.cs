// Azure Tables implementations of the Core store contracts. Reads are always
// owner-scoped (PartitionKey = Telegram user ID); conditional writes use
// ETags so concurrent duplicates are detected rather than duplicated.
//
// Operation prompts use the single operation entity codec: the accepted
// prompt is stored as numbered properties on the operation entity via the
// segmented codec. There are no separate text chunk rows or readers.
using System.Net;
using System.Text;
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.Infrastructure.Persistence;

public sealed class TableClients(TableStorageOptions options)
{
    private readonly TableServiceClient _service = new(options.StorageConnectionString);
    private TableClient? _table;

    public TableClient Table => _table ??= Init();

    private TableClient Init()
    {
        var client = _service.GetTableClient(options.TableName);
        client.CreateIfNotExists();
        return client;
    }
}

public abstract class TableStoreBase(TableClients clients)
{
    protected TableClient Table => clients.Table;

    protected static bool IsTransient(RequestFailedException ex) =>
        ex.Status is 408 or 429 or 500 or 502 or 503 or 504;

    protected static Exception Translate(RequestFailedException ex, string what) =>
        IsTransient(ex)
            ? new TransientStoreException($"{what}: storage returned {ex.Status}", ex)
            : ex;

    protected static string Escape(string value) => value.Replace("'", "''");
}

public sealed class TableOperationStore(TableClients clients) : TableStoreBase(clients), IOperationStore
{
    public async Task<OperationRecord?> GetAsync(string ownerId, string operationId,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Operation(operationId), cancellationToken: ct);
            return OperationRowCodec.FromEntity(ownerId, operationId, entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "get operation");
        }
    }

    public async Task InsertStartingAsync(OperationRecord record, CancellationToken ct = default)
    {
        try
        {
            // Single-entity insert: the codec keeps the prompt on the
            // operation row, so the write is atomic without a batch.
            await Table.AddEntityAsync(OperationRowCodec.ToEntity(record), ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new ConcurrencyConflictException("operation already exists");
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "insert operation");
        }
    }

    public async Task<bool> CompareAndSwapStatusAsync(string ownerId, string operationId,
        OperationStatus expected, OperationStatus next, string? failureSummary = null,
        CancellationToken ct = default)
    {
        TableEntity entity;
        try
        {
            entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.Operation(operationId), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "read operation for status swap");
        }

        if (!entity.TryGetValue("Status", out var current) ||
            (string?)current != OperationStatusNames.ToName(expected))
            return false;

        entity["Status"] = OperationStatusNames.ToName(next);
        if (failureSummary is not null)
            entity["FailureSummary"] = failureSummary;
        entity["UpdatedUtc"] = DateTimeOffset.UtcNow;

        try
        {
            await Table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return false;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "swap operation status");
        }
    }

    public async Task<IReadOnlyList<OperationRecord>> ListOwnedAsync(string ownerId,
        CancellationToken ct = default)
    {
        try
        {
            var filter =
                $"PartitionKey eq '{Escape(ownerId)}' and RowKey ge 'operation_' and RowKey lt 'operation`'";
            var result = new List<OperationRecord>();
            await foreach (var entity in Table.QueryAsync<TableEntity>(filter,
                               cancellationToken: ct))
            {
                var record = OperationRowCodec.FromEntity(
                    ownerId, entity.RowKey[TableRowKeys.Operation(string.Empty).Length..], entity);
                if (record.Status != OperationStatus.Deleted)
                    result.Add(record);
            }

            result.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
            return result;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "list operations");
        }
    }
}

public sealed class TableUpdateReceiptStore(TableClients clients)
    : TableStoreBase(clients), IUpdateReceiptStore
{
    public async Task<UpdateReceipt?> GetAsync(string ownerId, long updateId,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.UpdateReceipt(updateId), cancellationToken: ct);
            return ToReceipt(ownerId, updateId, entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "get update receipt");
        }
    }

    public async Task InsertAsync(UpdateReceipt receipt, CancellationToken ct = default)
    {
        try
        {
            await Table.AddEntityAsync(new TableEntity(
                receipt.OwnerId, TableRowKeys.UpdateReceipt(receipt.UpdateId))
            {
                ["EntityKind"] = "update_receipt",
                ["SchemaVersion"] = StorageLimits.SchemaVersion,
                ["Command"] = receipt.Command,
                ["OperationId"] = receipt.OperationId ?? string.Empty,
                ["CommandCompleted"] = receipt.CommandCompleted,
                ["ReplyDelivered"] = receipt.ReplyDelivered,
                ["CreatedUtc"] = receipt.CreatedUtc,
                ["UpdatedUtc"] = receipt.UpdatedUtc,
            }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new ConcurrencyConflictException("update receipt already exists");
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "insert update receipt");
        }
    }

    public async Task MarkCompletedAsync(string ownerId, long updateId, string? operationId,
        CancellationToken ct = default)
    {
        await PatchAsync(ownerId, updateId, e =>
        {
            e["CommandCompleted"] = true;
            if (operationId is not null)
                e["OperationId"] = operationId;
            e["UpdatedUtc"] = DateTimeOffset.UtcNow;
        }, "complete update receipt", ct);
    }

    public async Task MarkReplyDeliveredAsync(string ownerId, long updateId,
        CancellationToken ct = default)
    {
        await PatchAsync(ownerId, updateId,
            e => e["ReplyDelivered"] = true, "mark reply delivered", ct);
    }

    private async Task PatchAsync(string ownerId, long updateId, Action<TableEntity> patch,
        string what, CancellationToken ct)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(
                ownerId, TableRowKeys.UpdateReceipt(updateId), cancellationToken: ct);
            patch(entity.Value);
            await Table.UpdateEntityAsync(entity.Value, ETag.All, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return; // Nothing to mark; redelivery will observe the missing state.
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, what);
        }
    }

    private static UpdateReceipt ToReceipt(string ownerId, long updateId, TableEntity e) =>
        new(
            ownerId,
            updateId,
            (string?)e["Command"] ?? string.Empty,
            string.IsNullOrEmpty((string?)e["OperationId"]) ? null : (string?)e["OperationId"],
            e.TryGetValue("CommandCompleted", out var c) && c is true,
            e.TryGetValue("ReplyDelivered", out var r) && r is true,
            e.TryGetValue("CreatedUtc", out var created) && created is DateTimeOffset co
                ? co : DateTimeOffset.UtcNow,
            e.TryGetValue("UpdatedUtc", out var updated) && updated is DateTimeOffset uo
                ? uo : DateTimeOffset.UtcNow);
}
