// Azure Tables implementations of the Core store contracts. Reads are always
// owner-scoped (PartitionKey = Telegram user ID); conditional writes use
// ETags so concurrent duplicates are detected rather than duplicated.
//
// Phase 2 long content: prompts and answers up to 32,768 characters can
// exceed a single table property, so they are segmented into bounded chunk
// entities. Chunk RowKeys sort inside the existing operation_/delivery_
// ranges, so cleanup and list queries need no new ranges.
using System.Net;
using System.Text;
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

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

    // Chunk keys live inside the existing ranges: "operation_<id>__text_N"
    // sorts between "operation_<id>" and "operation`" ('_' < '`').
    protected static string TextChunkRowKey(string operationId, int index) =>
        $"operation_{operationId}__text_{index:D4}";

    protected static string TextChunkPrefix(string operationId) =>
        $"operation_{operationId}__text_";

    protected static string PayloadChunkRowKey(string operationId, DateTime scheduledUtc, int index) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__part_{index:D4}";

    protected static string PayloadChunkPrefix(string operationId, DateTime scheduledUtc, string? version = null) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__" + (version is null ? "part_" : $"payload_{version}_part_");
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
            return await ToRecordAsync(ownerId, operationId, entity.Value, ct);
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
        var entity = ToEntity(record);
        var chunks = ChunkText(record);
        try
        {
            if (chunks.Count == 0)
            {
                await Table.AddEntityAsync(entity, ct);
                return;
            }

            // Same-partition batch: the write is atomic.
            var batch = new List<TableTransactionAction> { new(TableTransactionActionType.Add, entity) };
            batch.AddRange(chunks.Select(c => new TableTransactionAction(TableTransactionActionType.Add, c)));
            await Table.SubmitTransactionAsync(batch, ct);
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
                if (rowKey.Contains("__text_", StringComparison.Ordinal))
                    continue;
                if (rowKey.Contains("__part_", StringComparison.Ordinal))
                    continue;
                var record = await ToRecordAsync(ownerId, rowKey["operation_".Length..], entity, ct);
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

    private static TableEntity ToEntity(OperationRecord record)
    {
        var entity = new TableEntity(record.OwnerId, Ids.OperationRowKey(record.OperationId))
        {
            ["ChatId"] = record.ChatId,
            ["Cron"] = record.CronExpression,
            ["Status"] = OperationStatusNames.ToName(record.Status),
            ["InstanceId"] = record.InstanceId ?? string.Empty,
            ["FailureSummary"] = record.FailureSummary ?? string.Empty,
            ["CreatedUtc"] = record.CreatedUtc,
            ["UpdatedUtc"] = record.UpdatedUtc,
        };
        // Compatibility: Text always holds at least the leading segment so
        // existing readers see the start of the prompt.
        var chunks = TextLimits.ToStorageChunks(record.Text);
        entity["Text"] = chunks.Count == 0 ? string.Empty : chunks[0];
        entity["HasLongText"] = chunks.Count > 1;
        return entity;
    }

    private static List<TableEntity> ChunkText(OperationRecord record)
    {
        var chunks = TextLimits.ToStorageChunks(record.Text);
        var entities = new List<TableEntity>();
        for (var i = 1; i < chunks.Count; i++)
        {
            entities.Add(new TableEntity(record.OwnerId, TextChunkRowKey(record.OperationId, i))
            {
                ["Data"] = chunks[i],
            });
        }

        return entities;
    }

    private async Task<OperationRecord> ToRecordAsync(
        string ownerId, string operationId, TableEntity e, CancellationToken ct)
    {
        var text = (string?)e["Text"] ?? string.Empty;
        if (e.TryGetValue("HasLongText", out var flag) && flag is true)
            text = await ReadChunkedTextAsync(ownerId, operationId, text, ct);
        return new OperationRecord(
            ownerId,
            operationId,
            e.TryGetValue("ChatId", out var chat) ? (long)(chat ?? 0L) : 0L,
            (string?)e["Cron"] ?? string.Empty,
            text,
            OperationStatusNames.Parse((string?)e["Status"] ?? OperationStatusNames.Starting),
            string.IsNullOrEmpty((string?)e["InstanceId"]) ? null : (string?)e["InstanceId"],
            string.IsNullOrEmpty((string?)e["FailureSummary"]) ? null : (string?)e["FailureSummary"],
            e.TryGetValue("CreatedUtc", out var created) && created is DateTimeOffset c
                ? c : DateTimeOffset.UtcNow,
            e.TryGetValue("UpdatedUtc", out var updated) && updated is DateTimeOffset u
                ? u : DateTimeOffset.UtcNow);
    }

    private async Task<string> ReadChunkedTextAsync(
        string ownerId, string operationId, string first, CancellationToken ct)
    {
        try
        {
            var prefix = TextChunkPrefix(operationId);
            var filter = $"PartitionKey eq '{Escape(ownerId)}' and " +
                $"RowKey ge '{Escape(prefix)}' and RowKey lt '{Escape(prefix)}`'";
            var rest = new SortedDictionary<string, string>();
            await foreach (var entity in Table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
                rest[entity.RowKey] = (string?)entity["Data"] ?? string.Empty;
            return first + string.Concat(rest.Values);
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "read operation text chunks");
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
            return ToReceipt(ownerId, operationId, scheduledUtc, entity.Value);
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
        if (receipt.ClaimId is null) throw new ClaimLostException();
        await ChangeOwnedAsync(receipt.OwnerId, receipt.OperationId, receipt.ScheduledUtc.UtcDateTime,
            receipt.ClaimId, _ => ToEntity(receipt), true, ct);
    }

    public async Task<bool> TryClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        var claim = new DeliveryReceipt(ownerId, operationId, scheduledUtc,
            OccurrenceExecution.StatusGenerating, 0, null, null,
            UpdatedUtc: DateTimeOffset.UtcNow, ClaimId: claimId);
        try
        {
            await Table.AddEntityAsync(ToEntity(claim), ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409) { }
        catch (RequestFailedException ex) { throw Translate(ex, "claim occurrence"); }

        try
        {
            var response = await Table.GetEntityAsync<TableEntity>(ownerId,
                Ids.DeliveryReceiptRowKey(operationId, scheduledUtc), cancellationToken: ct);
            var entity = response.Value;
            var existing = ToReceipt(ownerId, operationId, scheduledUtc, entity);
            if (OccurrenceExecution.IsTerminal(existing) ||
                existing.ClaimId is not null && !OccurrenceExecution.IsClaimStale(existing, DateTimeOffset.UtcNow))
                return false;
            entity["UpdatedUtc"] = DateTimeOffset.UtcNow;
            entity["ClaimId"] = claimId;
            await Table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412) { return false; }
        catch (RequestFailedException ex) { throw Translate(ex, "claim occurrence"); }
    }

    public async Task<bool> RenewClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        try
        {
            await ChangeOwnedAsync(ownerId, operationId, scheduledUtc, claimId, e => e, true, ct);
            return true;
        }
        catch (ClaimLostException) { return false; }
    }

    public async Task ReleaseClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        try
        {
            await ChangeOwnedAsync(ownerId, operationId, scheduledUtc, claimId, e =>
            {
                e.Remove("ClaimId");
                return e;
            }, false, ct);
        }
        catch (ClaimLostException) { } // Never release another worker's claim.
    }

    private async Task ChangeOwnedAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, Func<TableEntity, TableEntity> change, bool requireLive, CancellationToken ct)
    {
        // Heartbeats and progress writes may race each other; retry the ETag
        // conflict, rechecking ownership before every write.
        for (var retry = 0; retry < 8; retry++)
        {
            try
            {
                var response = await Table.GetEntityAsync<TableEntity>(ownerId,
                    Ids.DeliveryReceiptRowKey(operationId, scheduledUtc), cancellationToken: ct);
                var existing = ToReceipt(ownerId, operationId, scheduledUtc, response.Value);
                if (existing.ClaimId != claimId || requireLive &&
                    OccurrenceExecution.IsClaimStale(existing, DateTimeOffset.UtcNow))
                    throw new ClaimLostException();
                var updated = change(response.Value);
                updated["UpdatedUtc"] = DateTimeOffset.UtcNow;
                await Table.UpdateEntityAsync(updated, response.Value.ETag, TableUpdateMode.Replace, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { throw new ClaimLostException(); }
            catch (RequestFailedException ex) when (ex.Status == 412) { }
            catch (RequestFailedException ex) { throw Translate(ex, "update owned occurrence"); }
        }
        throw new TransientStoreException("Occurrence changed repeatedly during lease update.");
    }

    public static TableEntity ToEntity(DeliveryReceipt receipt)
    {
        var entity = new TableEntity(
            receipt.OwnerId,
            Ids.DeliveryReceiptRowKey(receipt.OperationId, receipt.ScheduledUtc.UtcDateTime))
        {
            ["ScheduledUtc"] = receipt.ScheduledUtc,
            ["Status"] = receipt.Status,
            ["Attempts"] = receipt.Attempts,
            ["ErrorSummary"] = receipt.ErrorSummary ?? string.Empty,
            ["GenerationAttempts"] = receipt.GenerationAttempts,
            ["ExecutionStatus"] = receipt.ExecutionStatus ?? string.Empty,
            ["Provider"] = receipt.Provider ?? string.Empty,
            ["ModelName"] = receipt.ModelName ?? string.Empty,
            ["PromptTokens"] = receipt.PromptTokens,
            ["CompletionTokens"] = receipt.CompletionTokens,
            ["SearchResults"] = receipt.SearchResults,
            ["SearchUsed"] = receipt.SearchUsed,
            ["SentParts"] = receipt.SentParts,
            ["TotalParts"] = receipt.TotalParts,
            ["MessageIds"] = receipt.MessageIds ?? string.Empty,
            ["FailureNotice"] = receipt.FailureNotice ?? string.Empty,
            ["UpdatedUtc"] = receipt.UpdatedUtc == default ? DateTimeOffset.UtcNow : receipt.UpdatedUtc,
            ["ClaimId"] = receipt.ClaimId ?? string.Empty,
            ["PayloadVersion"] = receipt.PayloadVersion ?? string.Empty,
            ["ContextInitialized"] = receipt.ContextInitialized,
            ["DeliveryTransientFailures"] = receipt.DeliveryTransientFailures,
            ["StorageConflicts"] = receipt.StorageConflicts,
        };
        if (receipt.TelegramMessageId.HasValue)
            entity["TelegramMessageId"] = receipt.TelegramMessageId.Value;
        if (receipt.PayloadSchemaVersion.HasValue)
            entity["PayloadSchemaVersion"] = receipt.PayloadSchemaVersion.Value;
        if (receipt.ContextVersion is not null)
            entity["ContextVersion"] = receipt.ContextVersion;
        if (receipt.AnswerVersion is not null)
            entity["AnswerVersion"] = receipt.AnswerVersion;
        if (receipt.PlanVersion is not null)
            entity["PlanVersion"] = receipt.PlanVersion;
        if (receipt.InstructionVersion is not null)
            entity["InstructionVersion"] = receipt.InstructionVersion;
        return entity;
    }

    public static DeliveryReceipt ToReceipt(
        string ownerId, string operationId, DateTime scheduledUtc, TableEntity e)
    {
        int Int(string name, int missing = 0) =>
            e.TryGetValue(name, out var v) ? Convert.ToInt32(v ?? 0) : missing;
        long Long(string name) =>
            e.TryGetValue(name, out var v) ? Convert.ToInt64(v ?? 0) : 0;
        string? Str(string name) =>
            string.IsNullOrEmpty((string?)e[name]) ? null : (string?)e[name];
        // New Phase 3 fields decode safely for old receipts: TryGetValue
        // keeps missing properties at their defaults.
        string? OptStr(string name) =>
            e.TryGetValue(name, out var v) && !string.IsNullOrEmpty((string?)v) ? (string?)v : null;
        int? OptInt(string name) =>
            e.TryGetValue(name, out var v) && v is not null ? Convert.ToInt32(v) : null;
        return new DeliveryReceipt(
            ownerId, operationId, scheduledUtc,
            (string?)e["Status"] ?? string.Empty,
            Int("Attempts"),
            Str("ErrorSummary"),
            e.TryGetValue("TelegramMessageId", out var m) && m is long id ? id : null,
            Int("GenerationAttempts"),
            Str("ExecutionStatus"),
            Str("Provider"),
            Str("ModelName"),
            Long("PromptTokens"),
            Long("CompletionTokens"),
            Int("SearchResults"),
            e.TryGetValue("SearchUsed", out var su) && su is true,
            Int("SentParts"),
            Int("TotalParts"),
            Str("MessageIds"),
            Str("FailureNotice"),
            e.TryGetValue("UpdatedUtc", out var updated) && updated is DateTimeOffset u
                ? u : DateTimeOffset.UtcNow,
            // Pre-upgrade rows have no lease or payload version.
            Str("ClaimId"), Str("PayloadVersion"),
            OptInt("PayloadSchemaVersion"),
            OptStr("ContextVersion"),
            e.TryGetValue("ContextInitialized", out var ci) && ci is true,
            OptStr("AnswerVersion"),
            OptStr("PlanVersion"),
            OptStr("InstructionVersion"),
            Int("DeliveryTransientFailures"),
            Int("StorageConflicts"));
    }
}

public sealed class TableOccurrencePayloadStore(TableClients clients)
    : TableStoreBase(clients), IOccurrencePayloadStore
{
    public async Task PersistAsync(string ownerId, string operationId, DateTime scheduledUtc,
        IReadOnlyList<string> parts, CancellationToken ct = default, string? version = null)
    {
        try
        {
            // Idempotent: chunk keys are deterministic, so a retried persist
            // overwrites the same entities.
            foreach (var entity in ChunkEntities(ownerId, operationId, scheduledUtc, parts, version))
                await Table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "persist occurrence payload");
        }
    }

    public async Task<IReadOnlyList<string>?> LoadAsync(string ownerId, string operationId,
        DateTime scheduledUtc, CancellationToken ct = default, string? version = null)
    {
        try
        {
            var prefix = PayloadChunkPrefix(operationId, scheduledUtc, version);
            var filter = $"PartitionKey eq '{Escape(ownerId)}' and " +
                $"RowKey ge '{Escape(prefix)}' and RowKey lt '{Escape(prefix)}`'";
            var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
            await foreach (var entity in Table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
                rows[entity.RowKey] = (string?)entity["Data"] ?? string.Empty;

            if (rows.Count == 0)
                return null;

            // Row keys order lexicographically by (part, segment); group
            // consecutive rows of the same part back into message parts.
            var parts = new List<string>();
            var currentPart = -1;
            var current = new StringBuilder();
            foreach (var (rowKey, data) in rows)
            {
                var (part, _) = ParseChunkKey(rowKey, prefix);
                if (part != currentPart && currentPart >= 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                currentPart = part;
                current.Append(data);
            }

            parts.Add(current.ToString());
            return parts;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "load occurrence payload");
        }
    }

    private static List<TableEntity> ChunkEntities(string ownerId, string operationId,
        DateTime scheduledUtc, IReadOnlyList<string> parts, string? version)
    {
        var entities = new List<TableEntity>();
        for (var i = 0; i < parts.Count; i++)
        {
            var segments = TextLimits.ToStorageChunks(parts[i]);
            for (var j = 0; j < segments.Count; j++)
            {
                entities.Add(new TableEntity(ownerId,
                    PayloadChunkPrefix(operationId, scheduledUtc, version) + $"{i:D4}_{j:D4}")
                {
                    ["Data"] = segments[j],
                });
            }
        }

        return entities;
    }

    private static (int Part, int Segment) ParseChunkKey(string rowKey, string prefix)
    {
        // <prefix><part:4>_<seg:4>
        var tail = rowKey.StartsWith(prefix, StringComparison.Ordinal)
            ? rowKey[prefix.Length..]
            : rowKey;
        var part = tail.Length >= 4 && int.TryParse(tail[..4], out var p) ? p : 0;
        var segment = tail.Length >= 9 && int.TryParse(tail[5..9], out var s) ? s : 0;
        return (part, segment);
    }
}
