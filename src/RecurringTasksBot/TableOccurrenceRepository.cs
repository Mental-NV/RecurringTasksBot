// Table-backed coordinated occurrence repository. Every publication reads
// the current receipt, the active operation, and memory, then commits one
// atomic entity-group transaction under the receipt claim. 409/412
// conflicts reread and retry (at most eight local attempts) before
// surfacing as retryable storage conflicts.
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot.Core;

namespace RecurringTasksBot;

public sealed class SystemPhase3Clock : IPhase3Clock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class TableOccurrenceRepository(TableClients clients, IPhase3Clock clock)
    : TableStoreBase(clients), IOccurrenceRepository
{
    private const int MaxTransactionAttempts = 8;

    public async Task<MemoryRecord?> ReadPreviousReplyAsync(
        string ownerId, string operationId, CancellationToken ct = default)
    {
        var entity = await TryGetRowAsync(ownerId, Phase3RowKeys.Memory(operationId), ct);
        return entity is null ? null : Phase3TableBatches.ParseMemory(entity);
    }

    public async Task<ContextInitResult> InitializeContextAsync(
        FrozenContextRequest request, CancellationToken ct = default)
    {
        var conflicts = 0;
        while (true)
        {
            var (opEntity, op) = await ReadOperationAsync(request.OwnerId, request.OperationId, ct);
            var receiptEntity = await TryGetRowAsync(request.OwnerId,
                Ids.DeliveryReceiptRowKey(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = TableDeliveryReceiptStore.ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            if (receipt.ContextVersion is not null)
                return new ContextInitResult(await ReadContextAsync(request, receipt.ContextVersion, ct), false);
            var memoryEntity = await TryGetRowAsync(
                request.OwnerId, Phase3RowKeys.Memory(request.OperationId), ct);
            var built = Phase3TableBatches.BuildInit(opEntity, op, receiptEntity, receipt,
                memoryEntity is null ? null : Phase3TableBatches.ParseMemory(memoryEntity),
                request, clock.UtcNow, conflicts);
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
                Ids.DeliveryReceiptRowKey(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = TableDeliveryReceiptStore.ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            var built = Phase3TableBatches.BuildPersist(opEntity, opStatus, receiptEntity, receipt,
                request, Phase3Versions.New(), clock.UtcNow, conflicts);
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
            var built = Phase3TableBatches.BuildConfirm(progress.ReceiptEntity, progress.Receipt,
                progress.Answer.Text, progress.PlanEntity, progress.Plan, request, clock.UtcNow, conflicts);
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
            var built = Phase3TableBatches.BuildReplace(progress.ReceiptEntity, progress.Receipt,
                progress.Answer.Text, progress.PlanEntity, progress.Plan, request, clock.UtcNow, conflicts);
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
            Ids.DeliveryReceiptRowKey(operationId, scheduledUtc), ct)
            ?? throw new ClaimLostException();
        var receipt = TableDeliveryReceiptStore.ToReceipt(ownerId, operationId, scheduledUtc, receiptEntity);
        if (receipt.ClaimId is null || receipt.ClaimId != claimId)
            throw new ClaimLostException();
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new Phase3ConsistencyException("plan progress requires committed answer and plan pointers");
        var stored = await ReadAnswerAsync(ownerId, operationId, scheduledUtc, receipt.AnswerVersion, ct);
        var plan = await ReadPlanAsync(ownerId, operationId, scheduledUtc,
            receipt.PlanVersion, receipt.AnswerVersion, stored.Answer.Text, ct);
        return new ProgressRead(plan, stored.Answer);
    }

    public async Task<int> TrackGenerationAsync(
        TrackGenerationRequest request, CancellationToken ct = default) =>
        await TrackAsync(request.OwnerId, request.OperationId, request.ScheduledUtc,
            request.ClaimId, request.Summary, Phase3TableBatches.BuildTrackGeneration,
            "track generation attempt", ct);

    public async Task<int> TrackDeliveryAsync(
        TrackDeliveryRequest request, CancellationToken ct = default) =>
        await TrackAsync(request.OwnerId, request.OperationId, request.ScheduledUtc,
            request.ClaimId, request.Summary, Phase3TableBatches.BuildTrackDelivery,
            "track delivery failure", ct);

    private async Task<int> TrackAsync(string ownerId, string operationId, DateTime scheduledUtc,
        string claimId, string summary,
        Func<TableEntity, DeliveryReceipt, string, string, DateTimeOffset, int,
            Phase3TableBatches.BuiltTrack> build,
        string what, CancellationToken ct)
    {
        var conflicts = 0;
        while (true)
        {
            var receiptEntity = await TryGetRowAsync(ownerId,
                Ids.DeliveryReceiptRowKey(operationId, scheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = TableDeliveryReceiptStore.ToReceipt(ownerId, operationId, scheduledUtc, receiptEntity);
            var built = build(receiptEntity, receipt, claimId, summary, clock.UtcNow, conflicts);
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
                Ids.DeliveryReceiptRowKey(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = TableDeliveryReceiptStore.ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            var actions = Phase3TableBatches.BuildFail(receiptEntity, receipt, request.ClaimId,
                request.ErrorSummary, request.HttpAttempts, request.DeliveryFailures,
                clock.UtcNow, conflicts);
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
                Ids.DeliveryReceiptRowKey(request.OperationId, request.ScheduledUtc), ct)
                ?? throw new ClaimLostException();
            var receipt = TableDeliveryReceiptStore.ToReceipt(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receiptEntity);
            if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
                throw new Phase3ConsistencyException("completion requires committed answer and plan pointers");
            var stored = await ReadAnswerAsync(
                request.OwnerId, request.OperationId, request.ScheduledUtc, receipt.AnswerVersion, ct);
            var storedExecutedUtc = stored.SourceExecutedUtc;
            var answer = stored.Answer;
            var plan = await ReadPlanAsync(request.OwnerId, request.OperationId, request.ScheduledUtc,
                receipt.PlanVersion, receipt.AnswerVersion, answer.Text, ct);
            var memoryEntity = await TryGetRowAsync(
                request.OwnerId, Phase3RowKeys.Memory(request.OperationId), ct);
            var built = Phase3TableBatches.BuildComplete(receiptEntity, receipt, opEntity, opStatus,
                stored, plan,
                memoryEntity is null ? null : Phase3TableBatches.ParseMemory(memoryEntity),
                memoryEntity, request.ClaimId, request.ScheduledUtc, storedExecutedUtc,
                request.DeliveryFailures, clock.UtcNow, conflicts);
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
            Ids.DeliveryReceiptRowKey(operationId, scheduledUtc), ct)
            ?? throw new ClaimLostException();
        var receipt = TableDeliveryReceiptStore.ToReceipt(ownerId, operationId, scheduledUtc, receiptEntity);
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new Phase3ConsistencyException("plan progress requires committed answer and plan pointers");
        var stored = await ReadAnswerAsync(
            ownerId, operationId, scheduledUtc, receipt.AnswerVersion, ct);
        var answer = stored.Answer;
        var plan = await ReadPlanAsync(ownerId, operationId, scheduledUtc,
            receipt.PlanVersion, receipt.AnswerVersion, answer.Text, ct);
        var planEntity = (await TryGetRowAsync(ownerId,
            Phase3RowKeys.Plan(operationId, scheduledUtc, receipt.PlanVersion), ct))
            ?? throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "plan row is missing");
        return new ProgressState(receiptEntity, receipt, answer, planEntity, plan);
    }

    private async Task<FrozenContextRecord> ReadContextAsync(
        FrozenContextRequest request, string contextVersion, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(request.OwnerId,
            Phase3RowKeys.Context(request.OperationId, request.ScheduledUtc, contextVersion), ct)
            ?? throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "context row is missing");
        return Phase3TableBatches.ParseContext(entity);
    }

    private async Task<Phase3TableBatches.StoredAnswer> ReadAnswerAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string answerVersion,
        CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId,
            Phase3RowKeys.Answer(operationId, scheduledUtc, answerVersion), ct)
            ?? throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "answer row is missing");
        return Phase3TableBatches.ParseAnswer(entity);
    }

    private async Task<DeliveryPlanDoc> ReadPlanAsync(
        string ownerId, string operationId, DateTime scheduledUtc,
        string planVersion, string answerVersion, string source, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId,
            Phase3RowKeys.Plan(operationId, scheduledUtc, planVersion), ct)
            ?? throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "plan row is missing");
        var parsed = Phase3TableBatches.ParsePlan(entity, source);
        if (parsed.AnswerVersion != answerVersion)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt, "plan references another answer");
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
        string ownerId, string operationId, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId, Ids.OperationRowKey(operationId), ct)
            ?? throw new Phase3OperationStoppedException($"operation {operationId} is missing");
        return (entity, await ToOperationRecordAsync(ownerId, operationId, entity, ct));
    }

    private async Task<(TableEntity Entity, OperationStatus Status)> ReadOperationStatusAsync(
        string ownerId, string operationId, CancellationToken ct)
    {
        var entity = await TryGetRowAsync(ownerId, Ids.OperationRowKey(operationId), ct)
            ?? throw new Phase3OperationStoppedException($"operation {operationId} is missing");
        return (entity, OperationStatusNames.Parse(
            (string?)entity["Status"] ?? OperationStatusNames.Starting));
    }

    private async Task<OperationRecord> ToOperationRecordAsync(
        string ownerId, string operationId, TableEntity e, CancellationToken ct)
    {
        var text = (string?)e["Text"] ?? string.Empty;
        if (e.TryGetValue("HasLongText", out var flag) && flag is true)
            text = await ReadChunkedTextAsync(ownerId, operationId, text, ct);
        return new OperationRecord(
            ownerId, operationId,
            e.TryGetValue("ChatId", out var chat) ? Convert.ToInt64(chat ?? 0L) : 0L,
            (string?)e["Cron"] ?? string.Empty,
            text,
            OperationStatusNames.Parse((string?)e["Status"] ?? OperationStatusNames.Starting),
            string.IsNullOrEmpty((string?)e["InstanceId"]) ? null : (string?)e["InstanceId"],
            string.IsNullOrEmpty((string?)e["FailureSummary"]) ? null : (string?)e["FailureSummary"],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
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

// Pure batch construction over read snapshots: every transaction below
// touches one partition, sets real ETags (never wildcards), and keeps the
// operation fence on all context/result publications. Verified directly
// with real TableEntity/TableTransactionAction members.
public static class Phase3TableBatches
{
    public const string FenceProperty = "Phase3WriteFence";

    public const string KindMemory = "memory";
    public const string KindContext = "context";
    public const string KindAnswer = "answer";
    public const string KindPlan = "plan";

    public sealed record StoredAnswer(AnswerArtifact Answer, DateTime SourceExecutedUtc);

    public sealed record BuiltInit(
        IReadOnlyList<TableTransactionAction> Actions, FrozenContextRecord Context);

    public sealed record BuiltPersist(
        IReadOnlyList<TableTransactionAction> Actions, string AnswerVersion, string PlanVersion);

    public sealed record BuiltProgress(
        IReadOnlyList<TableTransactionAction> Actions, DeliveryPlanDoc Plan);

    public sealed record BuiltReplacement(
        IReadOnlyList<TableTransactionAction> Actions, DeliveryPlanDoc Plan, IReadOnlyList<PlanLeaf> Fresh);

    public static BuiltInit BuildInit(
        TableEntity opEntity, OperationRecord op,
        TableEntity receiptEntity, DeliveryReceipt receipt,
        MemoryRecord? memory, FrozenContextRequest request, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, request.ClaimId, now);
        RequireActive(op.Status, op.OperationId);
        var scheduled = Phase3Times.Utc(request.ScheduledUtc);
        var eligible = memory is not null && Phase3Times.Utc(memory.SourceScheduledUtc) < scheduled;
        var contextVersion = Phase3Versions.New();
        var context = new FrozenContextRecord(
            contextVersion, request.OwnerId, request.OperationId, scheduled,
            op.Text, op.CronExpression,
            Phase3ContextEnvelope.OccurrenceIdFor(request.OperationId, scheduled),
            Phase3ContextEnvelope.ToIso8601(scheduled),
            Phase3ContextEnvelope.ToIso8601(request.Inputs.ExecutionStartedUtc),
            eligible,
            eligible ? Phase3ContextEnvelope.ToIso8601(memory!.SourceScheduledUtc) : null,
            eligible ? Phase3ContextEnvelope.ToIso8601(memory!.SourceExecutedUtc) : null,
            eligible ? memory!.Answer : null,
            eligible ? null : memory is null ? "no_memory_row" : "no_eligible_previous_reply",
            request.Inputs.EffectiveSystemInstruction,
            request.Inputs.TargetAnswerTextChars,
            request.Inputs.MaxRichMessageChars,
            request.Inputs.MemoryMode,
            Phase3Limits.EnabledCapabilities,
            Phase3Limits.InstructionVersion,
            now);
        var contextProps = ContextProps(context);
        var updated = receipt with
        {
            ContextVersion = contextVersion,
            ContextInitialized = true,
            InstructionVersion = Phase3Limits.InstructionVersion,
            PayloadSchemaVersion = Phase3Limits.SchemaVersion,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            new(TableTransactionActionType.Add,
                NewEntity(request.OwnerId,
                    Phase3RowKeys.Context(request.OperationId, scheduled, contextVersion), contextProps)),
            ReplaceReceipt(receiptEntity, updated),
            FenceOperation(opEntity, now),
        };
        RequireSamePartition(actions);
        Phase3StorageBounds.CheckBatchFits(
            [contextProps, PropsOf(actions[1]), PropsOf(actions[2])], "context publication");
        return new BuiltInit(actions, context);
    }

    public static BuiltPersist? BuildPersist(
        TableEntity opEntity, OperationStatus opStatus,
        TableEntity receiptEntity, DeliveryReceipt receipt,
        PersistGenerationRequest request, string planVersion, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, request.ClaimId, now);
        RequireActive(opStatus, request.OperationId);
        if (receipt.AnswerVersion is not null || receipt.PlanVersion is not null)
            return null; // Pointers already committed: never regenerate.
        if (request.InitialPlan.AnswerVersion != request.Answer.AnswerVersion ||
            request.InitialPlan.SourceSha256 != request.Answer.SourceSha256)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "initial plan does not match the generated answer");
        DeliveryPlan.Validate(request.InitialPlan, request.Answer.Text);
        var scheduled = Phase3Times.Utc(request.ScheduledUtc);
        var executed = Phase3Times.Utc(request.SourceExecutedUtc);
        var answerProps = AnswerProps(request.Answer, scheduled, executed, now);
        var planProps = PlanProps(planVersion, request.InitialPlan);
        var updated = receipt with
        {
            ExecutionStatus = request.Answer.Kind == AnswerKind.Answer ? "generated" : "failed",
            ErrorSummary = request.ErrorSummary ?? receipt.ErrorSummary,
            Provider = request.Usage.Provider,
            ModelName = request.Usage.ModelName,
            PromptTokens = request.Usage.PromptTokens,
            CompletionTokens = request.Usage.CompletionTokens,
            SearchResults = request.Usage.SearchResults,
            SearchUsed = request.Usage.SearchUsed,
            AnswerVersion = request.Answer.AnswerVersion,
            PlanVersion = planVersion,
            PayloadSchemaVersion = Phase3Limits.SchemaVersion,
            SentParts = 0,
            TotalParts = request.InitialPlan.Leaves.Count,
            MessageIds = string.Empty,
            FailureNotice = request.Answer.Kind == AnswerKind.FailureNotice
                ? request.Answer.Text : receipt.FailureNotice,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            new(TableTransactionActionType.Add,
                NewEntity(request.OwnerId,
                    Phase3RowKeys.Answer(request.OperationId, scheduled, request.Answer.AnswerVersion),
                    answerProps)),
            new(TableTransactionActionType.Add,
                NewEntity(request.OwnerId,
                    Phase3RowKeys.Plan(request.OperationId, scheduled, planVersion), planProps)),
            ReplaceReceipt(receiptEntity, updated),
            FenceOperation(opEntity, now),
        };
        RequireSamePartition(actions);
        Phase3StorageBounds.CheckBatchFits(
            [answerProps, planProps, PropsOf(actions[2]), PropsOf(actions[3])], "generation publication");
        return new BuiltPersist(actions, request.Answer.AnswerVersion, planVersion);
    }

    public static BuiltProgress BuildConfirm(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        string source, TableEntity planEntity, DeliveryPlanDoc plan,
        ConfirmLeafRequest request, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, request.ClaimId, now);
        RequirePointers(receipt);
        RequirePlanIdentity(plan, receipt);
        var confirmed = DeliveryPlan.Confirm(plan, request.LeafId, request.MessageId);
        if (ReferenceEquals(confirmed, plan))
            return new BuiltProgress([], plan); // Idempotent replay.
        return ReplacePlan(receiptEntity, receipt, planEntity, confirmed, request.DeliveryFailures, now, conflicts);
    }

    public static BuiltReplacement BuildReplace(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        string source, TableEntity planEntity, DeliveryPlanDoc plan,
        ReplaceLeafRequest request, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, request.ClaimId, now);
        RequirePointers(receipt);
        RequirePlanIdentity(plan, receipt);
        var (replaced, fresh) = request.Fallback switch
        {
            PlanFallbackKind.ToLiteralRich =>
                DeliveryPlan.ReplaceWithLiteralRich(plan, source, request.LeafId),
            PlanFallbackKind.ToSmallLiteral =>
                DeliveryPlan.ReplaceWithSmallLiteral(plan, source, request.LeafId),
            PlanFallbackKind.ToPlain =>
                DeliveryPlan.ReplaceWithPlain(plan, source, request.LeafId),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var built = ReplacePlan(receiptEntity, receipt, planEntity, replaced,
            request.DeliveryFailures, now, conflicts);
        DeliveryPlan.Validate(replaced, source);
        return new BuiltReplacement(built.Actions, replaced, fresh);
    }

    public static IReadOnlyList<TableTransactionAction> BuildComplete(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        TableEntity opEntity, OperationStatus opStatus,
        StoredAnswer stored, DeliveryPlanDoc plan,
        MemoryRecord? memory, TableEntity? memoryEntity,
        string claimId, DateTime scheduledUtc, DateTime executedUtc,
        int deliveryFailures, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, claimId, now);
        RequireActive(opStatus, receipt.OperationId);
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new Phase3ConsistencyException("completion requires committed answer and plan pointers");
        if (plan.Leaves.Any(l => !l.Confirmed))
            throw new Phase3ConsistencyException("completion requires all leaves confirmed");
        if (plan.AnswerVersion != stored.Answer.AnswerVersion ||
            plan.SourceSha256 != stored.Answer.SourceSha256)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "plan does not match the committed answer");
        var scheduled = Phase3Times.Utc(scheduledUtc);
        var updated = receipt with
        {
            Status = "sent",
            DeliveryTransientFailures = receipt.DeliveryTransientFailures + deliveryFailures,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            ReplaceReceipt(receiptEntity, updated),
            FenceOperation(opEntity, now),
        };
        var fitted = new List<IReadOnlyDictionary<string, object?>>
            { PropsOf(actions[0]), PropsOf(actions[1]) };
        if (stored.Answer.Kind == AnswerKind.Answer)
        {
            var publish = DecideMemoryPublication(memory, receipt.OwnerId, receipt.OperationId,
                scheduled, Phase3Times.Utc(executedUtc), stored.Answer, now);
            if (publish is not null)
            {
                var memoryProps = MemoryProps(publish);
                TableEntity memoryRow = memoryEntity is null
                    ? NewEntity(receipt.OwnerId, Phase3RowKeys.Memory(receipt.OperationId), memoryProps)
                    : ReplaceFresh(memoryEntity, memoryProps);
                // Memory replacement is concurrency-guarded: a newer stored
                // reply must win, so the update carries the read ETag while
                // first-time publication stays an unconditional Add.
                actions.Add(memoryEntity is null
                    ? new TableTransactionAction(TableTransactionActionType.Add, memoryRow)
                    : new TableTransactionAction(TableTransactionActionType.UpdateReplace,
                        memoryRow, memoryRow.ETag));
                fitted.Add(memoryProps);
            }
        }
        RequireSamePartition(actions);
        Phase3StorageBounds.CheckBatchFits(fitted, "occurrence completion");
        return actions;
    }

    // A same-occurrence/same-hash memory row is idempotent; a newer stored
    // reply wins over an older receipt; anything else publishes.
    public static MemoryRecord? DecideMemoryPublication(
        MemoryRecord? memory, string ownerId, string operationId,
        DateTime scheduled, DateTime executed, AnswerArtifact answer, DateTimeOffset now) =>
        MemoryPublication.Decide(memory, ownerId, operationId, scheduled, executed, answer, now);

    private static void RequireClaim(DeliveryReceipt receipt, string claimId, DateTimeOffset now)
    {
        if (receipt.ClaimId is null || receipt.ClaimId != claimId ||
            OccurrenceExecution.IsClaimStale(receipt, now))
            throw new ClaimLostException();
    }

    private static void RequireActive(OperationStatus status, string operationId)
    {
        if (status is not (OperationStatus.Active or OperationStatus.Starting))
            throw new Phase3OperationStoppedException($"operation {operationId} is {status}");
    }

    private static void RequirePointers(DeliveryReceipt receipt)
    {
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new Phase3ConsistencyException("plan progress requires committed answer and plan pointers");
    }

    private static void RequirePlanIdentity(DeliveryPlanDoc plan, DeliveryReceipt receipt)
    {
        if (plan.AnswerVersion != receipt.AnswerVersion)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                "plan references another answer");
    }

    public sealed record BuiltTrack(IReadOnlyList<TableTransactionAction> Actions, int Total);

    public static BuiltTrack BuildTrackGeneration(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        string claimId, string summary, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, claimId, now);
        var updated = receipt with
        {
            GenerationAttempts = receipt.GenerationAttempts + 1,
            ErrorSummary = summary,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            ReplaceReceipt(receiptEntity, updated),
        };
        RequireSamePartition(actions);
        Phase3StorageBounds.CheckBatchFits([PropsOf(actions[0])], "generation tracking");
        return new BuiltTrack(actions, updated.GenerationAttempts);
    }

    public static BuiltTrack BuildTrackDelivery(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        string claimId, string summary, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, claimId, now);
        var updated = receipt with
        {
            Attempts = receipt.Attempts + 1,
            DeliveryTransientFailures = receipt.DeliveryTransientFailures + 1,
            ErrorSummary = summary,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            ReplaceReceipt(receiptEntity, updated),
        };
        RequireSamePartition(actions);
        Phase3StorageBounds.CheckBatchFits([PropsOf(actions[0])], "delivery tracking");
        return new BuiltTrack(actions, updated.DeliveryTransientFailures);
    }

    public static IReadOnlyList<TableTransactionAction> BuildFail(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        string claimId, string summary, int httpAttempts, int deliveryFailures,
        DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, claimId, now);
        var updated = receipt with
        {
            Status = "failed",
            ErrorSummary = summary,
            Attempts = receipt.Attempts + httpAttempts,
            DeliveryTransientFailures = receipt.DeliveryTransientFailures + deliveryFailures,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            ReplaceReceipt(receiptEntity, updated),
        };
        RequireSamePartition(actions);
        Phase3StorageBounds.CheckBatchFits([PropsOf(actions[0])], "occurrence failure");
        return actions;
    }

    private static BuiltProgress ReplacePlan(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        TableEntity planEntity, DeliveryPlanDoc plan,
        int deliveryFailures, DateTimeOffset now, int conflicts)
    {
        var planProps = PlanProps(receipt.PlanVersion!, plan);
        var updated = DeliveryPlanProgress.ApplyToReceipt(receipt, plan) with
        {
            DeliveryTransientFailures = receipt.DeliveryTransientFailures + deliveryFailures,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            ReplacePlanRow(planEntity, planProps),
            ReplaceReceipt(receiptEntity, updated),
        };
        RequireSamePartition(actions);
        Phase3StorageBounds.CheckBatchFits([planProps, PropsOf(actions[1])], "plan progress");
        return new BuiltProgress(actions, plan);
    }

    private static TableEntity NewEntity(
        string partition, string row, IDictionary<string, object?> props)
    {
        var entity = new TableEntity(partition, row);
        foreach (var (key, value) in props)
            entity[key] = value;
        return entity;
    }

    // Conditional writes reuse the read ETag and preserve unrelated entity
    // properties; wildcard ETags are forbidden for ownership, publication,
    // and the active-operation guard.
    private static TableEntity ReplaceWith(TableEntity read, DeliveryReceipt receipt)
    {
        var fresh = TableDeliveryReceiptStore.ToEntity(receipt);
        foreach (var (key, value) in fresh)
            read[key] = value;
        return read;
    }

    private static readonly HashSet<string> SystemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PartitionKey", "RowKey", "Timestamp",
    };

    // Full Replace drops stale segmented tails; system keys and the read
    // ETag survive (they are not part of the property set).
    private static TableEntity ReplaceFresh(TableEntity read, IDictionary<string, object?> props)
    {
        var etag = read.ETag;
        var keys = read.Keys.Where(k => !SystemKeys.Contains(k)).ToList();
        foreach (var (key, value) in props)
        {
            read[key] = value;
            keys.Remove(key);
        }
        foreach (var stale in keys)
            read.Remove(stale);
        read.ETag = etag;
        return read;
    }

    private static TableEntity FenceMerge(TableEntity opEntity, DateTimeOffset now) =>
        new(opEntity.PartitionKey, opEntity.RowKey)
        {
            ETag = opEntity.ETag,
            [FenceProperty] = now,
        };

    // Conditional actions must carry the read ETag explicitly: the
    // two-argument TableTransactionAction constructor sends no If-Match,
    // which would silently turn claim, fence, and memory guards into blind
    // writes. Add actions stay unconditional (entity creation).
    private static TableTransactionAction ReplaceReceipt(TableEntity receiptEntity, DeliveryReceipt receipt)
    {
        var entity = ReplaceWith(receiptEntity, receipt);
        return new(TableTransactionActionType.UpdateReplace, entity, entity.ETag);
    }

    private static TableTransactionAction FenceOperation(TableEntity opEntity, DateTimeOffset now)
    {
        var entity = FenceMerge(opEntity, now);
        return new(TableTransactionActionType.UpdateMerge, entity, entity.ETag);
    }

    private static TableTransactionAction ReplacePlanRow(
        TableEntity planEntity, IDictionary<string, object?> props)
    {
        var entity = ReplaceFresh(planEntity, props);
        return new(TableTransactionActionType.UpdateReplace, entity, entity.ETag);
    }

    private static void RequireSamePartition(IReadOnlyList<TableTransactionAction> actions)
    {
        var partition = ((TableEntity)actions[0].Entity!).PartitionKey;
        if (actions.Any(a => ((TableEntity)a.Entity!).PartitionKey != partition))
            throw new InvalidOperationException("phase 3 transaction spans partitions");
    }

    private static IReadOnlyDictionary<string, object?> PropsOf(TableTransactionAction action) =>
        new Dictionary<string, object?>((TableEntity)action.Entity!);

    private static void RequireRow(IDictionary<string, object?> props, string rowKey, string kind)
    {
        if (!props.TryGetValue("EntityKind", out var k) || (string?)k != kind)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"{rowKey} is not a {kind} row");
        if (!props.TryGetValue("SchemaVersion", out var v) ||
            !SegmentedProperties.TryToInt(v, out var schema))
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"{rowKey} is missing its schema version");
        if (schema != Phase3Limits.SchemaVersion)
            throw new Phase3PayloadException(Phase3FailureCodes.UnsupportedPayloadVersion,
                $"{rowKey} has schema version {schema}");
    }

    private static DateTimeOffset Dto(IDictionary<string, object?> props, string name, string rowKey)
    {
        if (props.TryGetValue(name, out var value))
        {
            if (value is DateTimeOffset dto)
                return dto;
            if (value is DateTime dt)
                return new DateTimeOffset(Phase3Times.Utc(dt));
        }
        throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
            $"{rowKey} is missing '{name}'");
    }

    private static string Str(IDictionary<string, object?> props, string name, string rowKey)
    {
        if (props.TryGetValue(name, out var value) && value is string text)
            return text;
        throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
            $"{rowKey} is missing '{name}'");
    }

    private static int Int(IDictionary<string, object?> props, string name, string rowKey)
    {
        if (props.TryGetValue(name, out var value) && SegmentedProperties.TryToInt(value, out var i))
            return i;
        throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
            $"{rowKey} is missing '{name}'");
    }

    public static Dictionary<string, object?> ContextProps(FrozenContextRecord c)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindContext,
            ["SchemaVersion"] = Phase3Limits.SchemaVersion,
            ["OwnerId"] = c.OwnerId,
            ["OperationId"] = c.OperationId,
            ["ScheduledUtc"] = new DateTimeOffset(Phase3Times.Utc(c.ScheduledUtc)),
            ["ContextVersion"] = c.ContextVersion,
            ["CreatedUtc"] = c.CreatedUtc,
            ["ScheduleCron"] = c.ScheduleCron,
            ["OccurrenceId"] = c.OccurrenceId,
            ["ScheduledAtUtc"] = c.ScheduledAtUtc,
            ["ExecutionStartedAtUtc"] = c.ExecutionStartedAtUtc,
            ["PreviousReplyPresent"] = c.PreviousReplyPresent,
            ["PreviousReplyScheduledAtUtc"] = c.PreviousReplyScheduledAtUtc ?? string.Empty,
            ["PreviousReplyExecutedAtUtc"] = c.PreviousReplyExecutedAtUtc ?? string.Empty,
            ["NoMemoryReason"] = c.NoMemoryReason ?? string.Empty,
            ["TargetAnswerTextChars"] = c.TargetAnswerTextChars,
            ["MaxRichMessageChars"] = c.MaxRichMessageChars,
            ["MemoryMode"] = c.MemoryMode,
            ["EnabledCapabilities"] = c.EnabledCapabilities,
            ["InstructionVersion"] = c.InstructionVersion,
        };
        SegmentedProperties.Write(props, "Task", c.TaskText);
        SegmentedProperties.Write(props, "Instruction", c.EffectiveSystemInstruction);
        if (c.PreviousReplyAnswer is not null)
            SegmentedProperties.Write(props, "Previous", c.PreviousReplyAnswer);
        return props;
    }

    public static FrozenContextRecord ParseContext(TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        RequireRow(props, entity.RowKey, KindContext);
        var present = props.TryGetValue("PreviousReplyPresent", out var p) && p is true;
        return new FrozenContextRecord(
            Str(props, "ContextVersion", entity.RowKey),
            Str(props, "OwnerId", entity.RowKey),
            Str(props, "OperationId", entity.RowKey),
            Dto(props, "ScheduledUtc", entity.RowKey).UtcDateTime,
            SegmentedProperties.Read(props, "Task"),
            Str(props, "ScheduleCron", entity.RowKey),
            Str(props, "OccurrenceId", entity.RowKey),
            Str(props, "ScheduledAtUtc", entity.RowKey),
            Str(props, "ExecutionStartedAtUtc", entity.RowKey),
            present,
            NullIfEmpty(Str(props, "PreviousReplyScheduledAtUtc", entity.RowKey)),
            NullIfEmpty(Str(props, "PreviousReplyExecutedAtUtc", entity.RowKey)),
            present ? SegmentedProperties.Read(props, "Previous") : null,
            NullIfEmpty(Str(props, "NoMemoryReason", entity.RowKey)),
            SegmentedProperties.Read(props, "Instruction"),
            Int(props, "TargetAnswerTextChars", entity.RowKey),
            Int(props, "MaxRichMessageChars", entity.RowKey),
            Str(props, "MemoryMode", entity.RowKey),
            Str(props, "EnabledCapabilities", entity.RowKey),
            Str(props, "InstructionVersion", entity.RowKey),
            Dto(props, "CreatedUtc", entity.RowKey));
    }

    public static Dictionary<string, object?> AnswerProps(
        AnswerArtifact answer, DateTime scheduledUtc, DateTime executedUtc, DateTimeOffset createdUtc)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindAnswer,
            ["SchemaVersion"] = Phase3Limits.SchemaVersion,
            ["AnswerVersion"] = answer.AnswerVersion,
            ["Kind"] = answer.Kind == AnswerKind.Answer ? "answer" : "failure_notice",
            ["ScheduledUtc"] = new DateTimeOffset(Phase3Times.Utc(scheduledUtc)),
            ["SourceExecutedUtc"] = new DateTimeOffset(Phase3Times.Utc(executedUtc)),
            ["CreatedUtc"] = createdUtc,
        };
        SegmentedProperties.Write(props, "Answer", answer.Text);
        return props;
    }

    public static StoredAnswer ParseAnswer(TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        RequireRow(props, entity.RowKey, KindAnswer);
        var kind = Str(props, "Kind", entity.RowKey) switch
        {
            "answer" => AnswerKind.Answer,
            "failure_notice" => AnswerKind.FailureNotice,
            var other => throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"{entity.RowKey} has unknown answer kind '{other}'"),
        };
        var text = SegmentedProperties.Read(props, "Answer");
        var executed = Dto(props, "SourceExecutedUtc", entity.RowKey).UtcDateTime;
        return new StoredAnswer(
            new AnswerArtifact(
                Str(props, "AnswerVersion", entity.RowKey), kind, text,
                DeliveryPlan.HashSource(text), AnswerSourceBound.CountScalars(text)),
            executed);
    }

    public static Dictionary<string, object?> PlanProps(string planVersion, DeliveryPlanDoc plan)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindPlan,
            ["SchemaVersion"] = Phase3Limits.SchemaVersion,
            ["PlanVersion"] = planVersion,
            ["AnswerVersion"] = plan.AnswerVersion,
            ["Revision"] = plan.Revision,
            ["SourceSha256"] = plan.SourceSha256,
        };
        SegmentedProperties.Write(props, "Plan", PlanRowCodec.Serialize(plan));
        return props;
    }

    public static DeliveryPlanDoc ParsePlan(TableEntity entity, string source)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        RequireRow(props, entity.RowKey, KindPlan);
        var plan = PlanRowCodec.Deserialize(SegmentedProperties.Read(props, "Plan"), source);
        if (plan.AnswerVersion != Str(props, "AnswerVersion", entity.RowKey) ||
            plan.Revision != Int(props, "Revision", entity.RowKey) ||
            plan.SourceSha256 != Str(props, "SourceSha256", entity.RowKey))
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"{entity.RowKey} plan metadata disagrees with its leaves");
        return plan;
    }

    public static Dictionary<string, object?> MemoryProps(MemoryRecord memory)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = KindMemory,
            ["SchemaVersion"] = Phase3Limits.SchemaVersion,
            ["OwnerId"] = memory.OwnerId,
            ["OperationId"] = memory.OperationId,
            ["AnswerVersion"] = memory.AnswerVersion,
            ["SourceScheduledUtc"] = new DateTimeOffset(Phase3Times.Utc(memory.SourceScheduledUtc)),
            ["SourceExecutedUtc"] = new DateTimeOffset(Phase3Times.Utc(memory.SourceExecutedUtc)),
            ["PublishedUtc"] = memory.PublishedUtc,
        };
        SegmentedProperties.Write(props, "Answer", memory.Answer);
        return props;
    }

    public static MemoryRecord ParseMemory(TableEntity entity)
    {
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        RequireRow(props, entity.RowKey, KindMemory);
        var answer = SegmentedProperties.Read(props, "Answer");
        return new MemoryRecord(
            Str(props, "OwnerId", entity.RowKey),
            Str(props, "OperationId", entity.RowKey),
            Dto(props, "SourceScheduledUtc", entity.RowKey).UtcDateTime,
            Dto(props, "SourceExecutedUtc", entity.RowKey).UtcDateTime,
            Dto(props, "PublishedUtc", entity.RowKey),
            Str(props, "AnswerVersion", entity.RowKey),
            answer,
            DeliveryPlan.HashSource(answer),
            AnswerSourceBound.CountScalars(answer));
    }

    private static string? NullIfEmpty(string text) =>
        string.IsNullOrEmpty(text) ? null : text;
}

