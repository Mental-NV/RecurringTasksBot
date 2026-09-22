using System.Net;
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

// Azure Tables implementations of the Core store contracts. Reads are always
// owner-scoped (PartitionKey = Telegram user ID); conditional writes use
// ETags so concurrent duplicates are detected rather than duplicated.
public sealed class TableClients(BotOptions options)
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
                ownerId, Ids.OperationRowKey(operationId), cancellationToken: ct);
            return ToRecord(ownerId, operationId, entity.Value);
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
            await Table.AddEntityAsync(ToEntity(record), ct);
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
                ownerId, Ids.OperationRowKey(operationId), cancellationToken: ct);
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
                var rowKey = entity.RowKey;
                var record = ToRecord(ownerId, rowKey["operation_".Length..], entity);
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

    private static TableEntity ToEntity(OperationRecord record) => new(
        record.OwnerId, Ids.OperationRowKey(record.OperationId))
    {
        ["ChatId"] = record.ChatId,
        ["Cron"] = record.CronExpression,
        ["Text"] = record.Text,
        ["Status"] = OperationStatusNames.ToName(record.Status),
        ["InstanceId"] = record.InstanceId ?? string.Empty,
        ["FailureSummary"] = record.FailureSummary ?? string.Empty,
        ["CreatedUtc"] = record.CreatedUtc,
        ["UpdatedUtc"] = record.UpdatedUtc,
    };

    private static OperationRecord ToRecord(string ownerId, string operationId, TableEntity e) =>
        new(
            ownerId,
            operationId,
            e.TryGetValue("ChatId", out var chat) ? (long)(chat ?? 0L) : 0L,
            (string?)e["Cron"] ?? string.Empty,
            (string?)e["Text"] ?? string.Empty,
            OperationStatusNames.Parse((string?)e["Status"] ?? OperationStatusNames.Starting),
            string.IsNullOrEmpty((string?)e["InstanceId"]) ? null : (string?)e["InstanceId"],
            string.IsNullOrEmpty((string?)e["FailureSummary"]) ? null : (string?)e["FailureSummary"],
            e.TryGetValue("CreatedUtc", out var created) && created is DateTimeOffset c
                ? c : DateTimeOffset.UtcNow,
            e.TryGetValue("UpdatedUtc", out var updated) && updated is DateTimeOffset u
                ? u : DateTimeOffset.UtcNow);
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
                ownerId, Ids.UpdateReceiptRowKey(updateId), cancellationToken: ct);
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
                receipt.OwnerId, Ids.UpdateReceiptRowKey(receipt.UpdateId))
            {
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
                ownerId, Ids.UpdateReceiptRowKey(updateId), cancellationToken: ct);
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

public sealed class TableDeliveryReceiptStore(TableClients clients)
    : TableStoreBase(clients), IDeliveryReceiptStore
{
    public async Task<DeliveryReceipt?> GetAsync(string ownerId, string operationId,
        DateTime scheduledUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(ownerId,
                Ids.DeliveryReceiptRowKey(operationId, scheduledUtc), cancellationToken: ct);
            var e = entity.Value;
            return new DeliveryReceipt(
                ownerId, operationId, scheduledUtc,
                (string?)e["Status"] ?? string.Empty,
                e.TryGetValue("Attempts", out var a) ? Convert.ToInt32(a ?? 0) : 0,
                string.IsNullOrEmpty((string?)e["ErrorSummary"]) ? null : (string?)e["ErrorSummary"],
                e.TryGetValue("TelegramMessageId", out var m) && m is long id ? id : null);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "get delivery receipt");
        }
    }

    public async Task UpsertAsync(DeliveryReceipt receipt, CancellationToken ct = default)
    {
        try
        {
            var entity = new TableEntity(
                receipt.OwnerId,
                Ids.DeliveryReceiptRowKey(receipt.OperationId,
                    receipt.ScheduledUtc.UtcDateTime))
            {
                ["ScheduledUtc"] = receipt.ScheduledUtc,
                ["Status"] = receipt.Status,
                ["Attempts"] = receipt.Attempts,
                ["ErrorSummary"] = receipt.ErrorSummary ?? string.Empty,
            };
            if (receipt.TelegramMessageId.HasValue)
                entity["TelegramMessageId"] = receipt.TelegramMessageId.Value;
            await Table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "upsert delivery receipt");
        }
    }
}
