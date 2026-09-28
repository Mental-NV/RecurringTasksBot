// Table-backed coordinated occurrence repository. Every publication reads
// the current receipt, the active operation, and memory, then commits one
// atomic entity-group transaction under the receipt claim. 409/412
// conflicts reread and retry (at most eight local attempts) before
// surfacing as retryable storage conflicts.
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Persistence;

public sealed class TableOccurrenceRepository(TableClients clients, TimeProvider? clock = null)
    : TableStoreBase(clients), IOccurrenceRepository
{
    private const int MaxTransactionAttempts = 8;

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public async Task<MemoryRecord?> ReadPreviousReplyAsync(
        string ownerId, string operationId, CancellationToken ct = default)
    {
        var entity = await TryGetRowAsync(ownerId, TableRowKeys.Memory(operationId), ct);
        return entity is null ? null : OccurrenceEntityCodec.ParseMemory(entity);
    }

    public async Task<ContextInitResult> InitializeContextAsync(
        FrozenContextRequest request, CancellationToken ct = default)
    {
        var conflicts = 0;
        while (true)
        {
            var (opEntity, op) = await ReadOperationAsync(
                request.OwnerId, request.OperationId, request.ScheduledUtc, ct);
            var receiptEntity = await TryGetRowAsync(request.OwnerId,
                TableRowKeys.DeliveryReceipt(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            if (receipt.ContextVersion is not null)
                return new ContextInitResult(await ReadContextAsync(request, receipt.ContextVersion, ct), false);
            var memoryEntity = await TryGetRowAsync(
                request.OwnerId, TableRowKeys.Memory(request.OperationId), ct);
            var built = OccurrenceTransactions.BuildInit(opEntity, op, receiptEntity, receipt,
                memoryEntity is null ? null : OccurrenceEntityCodec.ParseMemory(memoryEntity),
                request, Now, conflicts);
            try
            {
                await Table.SubmitTransactionAsync(built.Actions, ct);
                return new ContextInitResult(built.Context, true);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                if (++conflicts >= MaxTransactionAttempts)
                    throw new TransientStoreException("context publication conflicted repeatedly", ex);
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, "initialize occurrence context");
            }
        }
    }

    public async Task PersistGenerationAsync(
        PersistGenerationRequest request, CancellationToken ct = default)
    {
        var conflicts = 0;
        while (true)
        {
            var (opEntity, opStatus) = await ReadOperationStatusAsync(request.OwnerId, request.OperationId, ct);
            var receiptEntity = await TryGetRowAsync(request.OwnerId,
                TableRowKeys.DeliveryReceipt(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            var built = OccurrenceTransactions.BuildPersist(opEntity, opStatus, receiptEntity, receipt,
                request, ArtifactVersion.New(), Now, conflicts);
            if (built is null)
                return; // Pointers already committed: never regenerate.
            try
            {
                await Table.SubmitTransactionAsync(built.Actions, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                if (++conflicts >= MaxTransactionAttempts)
                    throw new TransientStoreException("generation publication conflicted repeatedly", ex);
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, "persist generated answer");
            }
        }
    }

    public async Task<DeliveryPlanDoc> ConfirmLeafAsync(
        ConfirmLeafRequest request, CancellationToken ct = default)
    {
        var conflicts = 0;
        while (true)
        {
            var progress = await ReadProgressAsync(request.OwnerId, request.OperationId,
                request.ScheduledUtc, ct);
            var built = OccurrenceTransactions.BuildConfirm(progress.ReceiptEntity, progress.Receipt,
                progress.Answer.Text, progress.PlanEntity, progress.Plan, request, Now, conflicts);
            if (built.Actions.Count == 0)
                return built.Plan;
            try
            {
                await Table.SubmitTransactionAsync(built.Actions, ct);
                return built.Plan;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                if (++conflicts >= MaxTransactionAttempts)
                    throw new TransientStoreException("plan confirmation conflicted repeatedly", ex);
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, "confirm plan leaf");
            }
        }
    }

    public async Task<PlanReplacement> ReplaceLeafAsync(
        ReplaceLeafRequest request, CancellationToken ct = default)
    {
        var conflicts = 0;
        while (true)
        {
            var progress = await ReadProgressAsync(request.OwnerId, request.OperationId,
                request.ScheduledUtc, ct);
            var built = OccurrenceTransactions.BuildReplace(progress.ReceiptEntity, progress.Receipt,
                progress.Answer.Text, progress.PlanEntity, progress.Plan, request, Now, conflicts);
            try
            {
                await Table.SubmitTransactionAsync(built.Actions, ct);
                return new PlanReplacement(built.Plan, built.Fresh);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                if (++conflicts >= MaxTransactionAttempts)
                    throw new TransientStoreException("plan replacement conflicted repeatedly", ex);
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, "replace plan leaf");
            }
        }
    }

    public async Task<ProgressRead> ReadProgressAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        CancellationToken ct = default)
    {
        var receiptEntity = await TryGetRowAsync(ownerId,
            TableRowKeys.DeliveryReceipt(operationId, scheduledUtc), ct)
            ?? throw new ClaimLostException();
        var receipt = ToReceipt(ownerId, operationId, scheduledUtc, receiptEntity);
        if (receipt.ClaimId is null || receipt.ClaimId != claimId)
            throw new ClaimLostException();
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new OccurrenceConsistencyException("plan progress requires committed answer and plan pointers");
        var stored = await ReadAnswerAsync(ownerId, operationId, scheduledUtc, receipt.AnswerVersion, ct);
        var plan = await ReadPlanAsync(ownerId, operationId, scheduledUtc,
            receipt.PlanVersion, receipt.AnswerVersion, stored.Answer.Text, ct);
        return new ProgressRead(plan, stored.Answer);
    }

    public async Task<int> TrackGenerationAsync(
        TrackGenerationRequest request, CancellationToken ct = default) =>
        await TrackAsync(request.OwnerId, request.OperationId, request.ScheduledUtc,
            request.ClaimId, request.Summary, OccurrenceTransactions.BuildTrackGeneration,
            "track generation attempt", ct);

    public async Task<int> TrackDeliveryAsync(
        TrackDeliveryRequest request, CancellationToken ct = default) =>
        await TrackAsync(request.OwnerId, request.OperationId, request.ScheduledUtc,
            request.ClaimId, request.Summary, OccurrenceTransactions.BuildTrackDelivery,
            "track delivery failure", ct);

    private async Task<int> TrackAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, string summary,
        Func<TableEntity, DeliveryReceipt, string, string, DateTimeOffset, int,
            OccurrenceTransactions.BuiltTrack> build,
        string what, CancellationToken ct)
    {
        var conflicts = 0;
        while (true)
        {
            var receiptEntity = await TryGetRowAsync(ownerId,
                TableRowKeys.DeliveryReceipt(operationId, scheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = ToReceipt(ownerId, operationId, scheduledUtc, receiptEntity);
            var built = build(receiptEntity, receipt, claimId, summary, Now, conflicts);
            try
            {
                await Table.SubmitTransactionAsync(built.Actions, ct);
                return built.Total;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                if (++conflicts >= MaxTransactionAttempts)
                    throw new TransientStoreException($"{what} conflicted repeatedly", ex);
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, what);
            }
        }
    }

    public async Task FailAsync(FailRequest request, CancellationToken ct = default)
    {
        var conflicts = 0;
        while (true)
        {
            var receiptEntity = await TryGetRowAsync(request.OwnerId,
                TableRowKeys.DeliveryReceipt(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            var actions = OccurrenceTransactions.BuildFail(receiptEntity, receipt, request.ClaimId,
                request.ErrorSummary, request.HttpAttempts, request.DeliveryFailures,
                Now, conflicts);
            try
            {
                await Table.SubmitTransactionAsync(actions, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                if (++conflicts >= MaxTransactionAttempts)
                    throw new TransientStoreException("occurrence failure conflicted repeatedly", ex);
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, "fail occurrence");
            }
        }
    }

    public async Task CompleteAsync(CompleteRequest request, CancellationToken ct = default)
    {
        var conflicts = 0;
        while (true)
        {
            var (opEntity, opStatus) = await ReadOperationStatusAsync(request.OwnerId, request.OperationId, ct);
            var receiptEntity = await TryGetRowAsync(request.OwnerId,
                TableRowKeys.DeliveryReceipt(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
                throw new OccurrenceConsistencyException("completion requires committed answer and plan pointers");
            var stored = await ReadAnswerAsync(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receipt.AnswerVersion, ct);
            var storedExecutedUtc = stored.SourceExecutedUtc;
            var answer = stored.Answer;
            var plan = await ReadPlanAsync(request.OwnerId, request.OperationId, request.ScheduledUtc,
                receipt.PlanVersion, receipt.AnswerVersion, answer.Text, ct);
            var memoryEntity = await TryGetRowAsync(
                request.OwnerId, TableRowKeys.Memory(request.OperationId), ct);
            var built = OccurrenceTransactions.BuildComplete(receiptEntity, receipt, opEntity, opStatus,
                stored, plan,
                memoryEntity is null ? null : OccurrenceEntityCodec.ParseMemory(memoryEntity),
                memoryEntity, request.ClaimId, request.ScheduledUtc, storedExecutedUtc,
                request.DeliveryFailures, Now, conflicts);
            try
            {
                await Table.SubmitTransactionAsync(built, ct);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                if (++conflicts >= MaxTransactionAttempts)
                    throw new TransientStoreException("occurrence completion conflicted repeatedly", ex);
            }
            catch (RequestFailedException ex)
            {
                throw Translate(ex, "complete occurrence");
            }
        }
    }

    private sealed record ProgressState(
        TableEntity ReceiptEntity, DeliveryReceipt Receipt,
        AnswerArtifact Answer, TableEntity PlanEntity, DeliveryPlanDoc Plan);

    private async Task<ProgressState> ReadProgressAsync(
        string ownerId, string operationId, DateTime scheduledUtc, CancellationToken ct)
    {
        var receiptEntity = await TryGetRowAsync(ownerId,
            TableRowKeys.DeliveryReceipt(operationId, scheduledUtc), ct)
            ?? throw new ClaimLostException();
        var receipt = ToReceipt(ownerId, operationId, scheduledUtc, receiptEntity);
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new OccurrenceConsistencyException("plan progress requires committed answer and plan pointers");
        var stored = await ReadAnswerAsync(
            ownerId, operationId, scheduledUtc, receipt.AnswerVersion, ct);
        var answer = stored.Answer;
        var plan = await ReadPlanAsync(ownerId, operationId, scheduledUtc,
            receipt.PlanVersion, receipt.AnswerVersion, answer.Text, ct);
        var planEntity = (await TryGetRowAsync(ownerId,
            TableRowKeys.Plan(operationId, scheduledUtc, receipt.PlanVersion), ct))
            ?? throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "plan row is missing");
        return new ProgressState(receiptEntity, receipt, answer, planEntity, plan);
    }

    private async Task<FrozenContextRecord> ReadContextAsync(
        FrozenContextRequest request, string contextVersion, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(request.OwnerId,
            TableRowKeys.Context(request.OperationId, request.ScheduledUtc, contextVersion), ct)
            ?? throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "context row is missing");
        return OccurrenceEntityCodec.ParseContext(entity);
    }

    private async Task<OccurrenceEntityCodec.StoredAnswer> ReadAnswerAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string answerVersion,
        CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId,
            TableRowKeys.Answer(operationId, scheduledUtc, answerVersion), ct)
            ?? throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "answer row is missing");
        return OccurrenceEntityCodec.ParseAnswer(entity);
    }

    private async Task<DeliveryPlanDoc> ReadPlanAsync(
        string ownerId, string operationId, DateTime scheduledUtc,
        string planVersion, string answerVersion, string source, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId,
            TableRowKeys.Plan(operationId, scheduledUtc, planVersion), ct)
            ?? throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "plan row is missing");
        var parsed = OccurrenceEntityCodec.ParsePlan(entity, source);
        if (parsed.AnswerVersion != answerVersion)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt, "plan references another answer");
        return parsed;
    }

    private async Task<TableEntity?> TryGetRowAsync(string partition, string row, CancellationToken ct)
    {
        try
        {
            return await Table.GetEntityAsync<TableEntity>(partition, row, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "read occurrence row");
        }
    }

    private async Task<(TableEntity Entity, OperationRecord Record)> ReadOperationAsync(
        string ownerId, string operationId, DateTime scheduledUtc, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId, TableRowKeys.Operation(operationId), ct);
        if (entity is not null)
            return (entity, ToOperationRecord(ownerId, operationId, entity));
        return await ReadTaskOperationAsync(ownerId, operationId, scheduledUtc, ct);
    }

    private async Task<(TableEntity Entity, OperationStatus Status)> ReadOperationStatusAsync(
        string ownerId, string operationId, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId, TableRowKeys.Operation(operationId), ct);
        if (entity is not null)
            return (entity, OperationStatusNames.Parse(
                (string?)entity["Status"] ?? OperationStatusNames.Starting));
        var taskEntity = await TryGetRowAsync(ownerId, TableRowKeys.Task(operationId), ct)
            ?? throw new OperationStoppedException($"operation {operationId} is missing");
        var task = TaskRowCodec.FromEntity(ownerId, operationId, taskEntity);
        return (taskEntity, TaskOperationSynthesis.MapStatus(task.Status));
    }

    // Phase 5 tasks keep no operation rows: the task row fences the
    // transaction and projects prompt, recurrence text, and liveness. The
    // fence touch lands on the task row; definition edits stay guarded by
    // the task revision, and list ordering uses creation time.
    private async Task<(TableEntity Entity, OperationRecord Record)> ReadTaskOperationAsync(
        string ownerId, string taskId, DateTime scheduledUtc, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId, TableRowKeys.Task(taskId), ct)
            ?? throw new OperationStoppedException($"operation {taskId} is missing");
        var task = TaskRowCodec.FromEntity(ownerId, taskId, entity);
        return (entity, TaskOperationSynthesis.ToOperationRecord(
            ownerId, taskId, task.ChatId, task.InstanceId, task, scheduledUtc));
    }

    private static OperationRecord ToOperationRecord(
        string ownerId, string operationId, TableEntity e) =>
        OperationRowCodec.FromEntity(ownerId, operationId, e);

    // ---- Receipt and lease ownership ----

    public async Task<DeliveryReceipt?> GetReceiptAsync(string ownerId, string operationId,
        DateTime scheduledUtc, CancellationToken ct = default)
    {
        try
        {
            var entity = await Table.GetEntityAsync<TableEntity>(ownerId,
                TableRowKeys.DeliveryReceipt(operationId, scheduledUtc), cancellationToken: ct);
            return ToReceipt(ownerId, operationId, scheduledUtc, entity.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw Translate(ex, "get occurrence receipt");
        }
    }

    public async Task<bool> TryClaimAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, CancellationToken ct = default)
    {
        var claim = new DeliveryReceipt(ownerId, operationId, scheduledUtc,
            OccurrenceExecution.StatusGenerating, 0, null,
            UpdatedUtc: Now, ClaimId: claimId);
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
                TableRowKeys.DeliveryReceipt(operationId, scheduledUtc), cancellationToken: ct);
            var entity = response.Value;
            var existing = ToReceipt(ownerId, operationId, scheduledUtc, entity);
            if (OccurrenceExecution.IsTerminal(existing) ||
                existing.ClaimId is not null && !OccurrenceExecution.IsClaimStale(existing, Now))
                return false;
            entity["UpdatedUtc"] = Now;
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

    public async Task MarkUnsupportedVersionAsync(string ownerId, string operationId,
        DateTime scheduledUtc, string claimId, int attempts, CancellationToken ct = default) =>
        await ChangeOwnedAsync(ownerId, operationId, scheduledUtc, claimId, e =>
        {
            e["Status"] = "failed";
            e["ErrorSummary"] = OccurrenceFailureCodes.UnsupportedPayloadVersion;
            e["Attempts"] = attempts;
            return e;
        }, true, ct);

    private async Task ChangeOwnedAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, Func<TableEntity, TableEntity> change, bool requireLive, CancellationToken ct)
    {
        // Heartbeats and progress writes may race each other; retry the ETag
        // conflict, rechecking ownership before every write.
        for (var retry = 0; retry < MaxTransactionAttempts; retry++)
        {
            try
            {
                var response = await Table.GetEntityAsync<TableEntity>(ownerId,
                    TableRowKeys.DeliveryReceipt(operationId, scheduledUtc), cancellationToken: ct);
                var existing = ToReceipt(ownerId, operationId, scheduledUtc, response.Value);
                if (existing.ClaimId != claimId || requireLive &&
                    OccurrenceExecution.IsClaimStale(existing, Now))
                    throw new ClaimLostException();
                var updated = change(response.Value);
                updated["UpdatedUtc"] = Now;
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
            TableRowKeys.DeliveryReceipt(receipt.OperationId, receipt.ScheduledUtc.UtcDateTime))
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
            ["SchemaVersion"] = StorageLimits.SchemaVersion,
            ["ContextInitialized"] = receipt.ContextInitialized,
            ["DeliveryTransientFailures"] = receipt.DeliveryTransientFailures,
            ["StorageConflicts"] = receipt.StorageConflicts,
        };
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
        // Every current row carries SchemaVersion = 1. Rows without it or
        // with another version are rejected: defaults must never paper
        // over unknown or partial state before generation or sending.
        if (!e.TryGetValue("SchemaVersion", out var schema) || schema is null)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                "Occurrence receipt is missing its schema version.");
        if (Convert.ToInt32(schema) != StorageLimits.SchemaVersion)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.UnsupportedPayloadVersion,
                $"Unsupported occurrence receipt schema version {schema}.");
        int Int(string name, int missing = 0) =>
            e.TryGetValue(name, out var v) ? Convert.ToInt32(v ?? 0) : missing;
        long Long(string name) =>
            e.TryGetValue(name, out var v) ? Convert.ToInt64(v ?? 0) : 0;
        string? Str(string name) =>
            string.IsNullOrEmpty((string?)e[name]) ? null : (string?)e[name];
        string? OptStr(string name) =>
            e.TryGetValue(name, out var v) && !string.IsNullOrEmpty((string?)v) ? (string?)v : null;
        int? OptInt(string name) =>
            e.TryGetValue(name, out var v) && v is not null ? Convert.ToInt32(v) : null;
        return new DeliveryReceipt(
            ownerId, operationId, scheduledUtc,
            (string?)e["Status"] ?? string.Empty,
            Int("Attempts"),
            Str("ErrorSummary"),
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
            Str("ClaimId"),
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
