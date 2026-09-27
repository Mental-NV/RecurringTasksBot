// Pure transaction construction over read snapshots: every transaction
// below touches one partition, sets real ETags (never wildcards), and
// keeps the operation fence on all context/result publications. Verified
// directly with real TableEntity/TableTransactionAction members.
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Persistence;

public static class OccurrenceTransactions
{
    public const string FenceProperty = "PublicationFence";

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
        var scheduled = UtcTime.Utc(request.ScheduledUtc);
        var eligible = memory is not null && UtcTime.Utc(memory.SourceScheduledUtc) < scheduled;
        var contextVersion = ArtifactVersion.New();
        var context = new FrozenContextRecord(
            contextVersion, request.OwnerId, request.OperationId, scheduled,
            op.Text, op.CronExpression,
            ExecutionMessageBuilder.OccurrenceIdFor(request.OperationId, scheduled),
            ExecutionMessageBuilder.ToIso8601(scheduled),
            ExecutionMessageBuilder.ToIso8601(request.Inputs.ExecutionStartedUtc),
            eligible,
            eligible ? ExecutionMessageBuilder.ToIso8601(memory!.SourceScheduledUtc) : null,
            eligible ? ExecutionMessageBuilder.ToIso8601(memory!.SourceExecutedUtc) : null,
            eligible ? memory!.Answer : null,
            eligible ? null : memory is null ? "no_memory_row" : "no_eligible_previous_reply",
            request.Inputs.EffectiveSystemInstruction,
            request.Inputs.TargetAnswerTextChars,
            request.Inputs.MaxRichMessageChars,
            request.Inputs.MemoryMode,
            ExecutionLimits.EnabledCapabilities,
            ExecutionLimits.InstructionVersion,
            now);
        var contextProps = OccurrenceEntityCodec.ContextProps(context);
        var updated = receipt with
        {
            ContextVersion = contextVersion,
            ContextInitialized = true,
            InstructionVersion = ExecutionLimits.InstructionVersion,
            PayloadSchemaVersion = StorageLimits.SchemaVersion,
            StorageConflicts = receipt.StorageConflicts + conflicts,
            UpdatedUtc = now,
        };
        var actions = new List<TableTransactionAction>
        {
            new(TableTransactionActionType.Add,
                NewEntity(request.OwnerId,
                    TableRowKeys.Context(request.OperationId, scheduled, contextVersion), contextProps)),
            ReplaceReceipt(receiptEntity, updated),
            FenceOperation(opEntity, now),
        };
        RequireSamePartition(actions);
        TableStorageLimits.CheckBatchFits(
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
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                "initial plan does not match the generated answer");
        DeliveryPlan.Validate(request.InitialPlan, request.Answer.Text);
        var scheduled = UtcTime.Utc(request.ScheduledUtc);
        var executed = UtcTime.Utc(request.SourceExecutedUtc);
        var answerProps = OccurrenceEntityCodec.AnswerProps(request.Answer, scheduled, executed, now);
        var planProps = OccurrenceEntityCodec.PlanProps(planVersion, request.InitialPlan);
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
            PayloadSchemaVersion = StorageLimits.SchemaVersion,
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
                    TableRowKeys.Answer(request.OperationId, scheduled, request.Answer.AnswerVersion),
                    answerProps)),
            new(TableTransactionActionType.Add,
                NewEntity(request.OwnerId,
                    TableRowKeys.Plan(request.OperationId, scheduled, planVersion), planProps)),
            ReplaceReceipt(receiptEntity, updated),
            FenceOperation(opEntity, now),
        };
        RequireSamePartition(actions);
        TableStorageLimits.CheckBatchFits(
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
        OccurrenceEntityCodec.StoredAnswer stored, DeliveryPlanDoc plan,
        MemoryRecord? memory, TableEntity? memoryEntity,
        string claimId, DateTime scheduledUtc, DateTime executedUtc,
        int deliveryFailures, DateTimeOffset now, int conflicts)
    {
        RequireClaim(receipt, claimId, now);
        RequireActive(opStatus, receipt.OperationId);
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new OccurrenceConsistencyException("completion requires committed answer and plan pointers");
        if (plan.Leaves.Any(l => !l.Confirmed))
            throw new OccurrenceConsistencyException("completion requires all leaves confirmed");
        if (plan.AnswerVersion != stored.Answer.AnswerVersion ||
            plan.SourceSha256 != stored.Answer.SourceSha256)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                "plan does not match the committed answer");
        var scheduled = UtcTime.Utc(scheduledUtc);
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
            var publish = MemoryPublication.Decide(memory, receipt.OwnerId, receipt.OperationId,
                scheduled, UtcTime.Utc(executedUtc), stored.Answer, now);
            if (publish is not null)
            {
                var memoryProps = OccurrenceEntityCodec.MemoryProps(publish);
                TableEntity memoryRow = memoryEntity is null
                    ? NewEntity(receipt.OwnerId, TableRowKeys.Memory(receipt.OperationId), memoryProps)
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
        TableStorageLimits.CheckBatchFits(fitted, "occurrence completion");
        return actions;
    }

    private static void RequireClaim(DeliveryReceipt receipt, string claimId, DateTimeOffset now)
    {
        if (receipt.ClaimId is null || receipt.ClaimId != claimId ||
            OccurrenceExecution.IsClaimStale(receipt, now))
            throw new ClaimLostException();
    }

    private static void RequireActive(OperationStatus status, string operationId)
    {
        if (status is not (OperationStatus.Active or OperationStatus.Starting))
            throw new OperationStoppedException($"operation {operationId} is {status}");
    }

    private static void RequirePointers(DeliveryReceipt receipt)
    {
        if (receipt.AnswerVersion is null || receipt.PlanVersion is null)
            throw new OccurrenceConsistencyException("plan progress requires committed answer and plan pointers");
    }

    private static void RequirePlanIdentity(DeliveryPlanDoc plan, DeliveryReceipt receipt)
    {
        if (plan.AnswerVersion != receipt.AnswerVersion)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
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
        TableStorageLimits.CheckBatchFits([PropsOf(actions[0])], "generation tracking");
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
        TableStorageLimits.CheckBatchFits([PropsOf(actions[0])], "delivery tracking");
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
        TableStorageLimits.CheckBatchFits([PropsOf(actions[0])], "occurrence failure");
        return actions;
    }

    private static BuiltProgress ReplacePlan(
        TableEntity receiptEntity, DeliveryReceipt receipt,
        TableEntity planEntity, DeliveryPlanDoc plan,
        int deliveryFailures, DateTimeOffset now, int conflicts)
    {
        var planProps = OccurrenceEntityCodec.PlanProps(receipt.PlanVersion!, plan);
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
        TableStorageLimits.CheckBatchFits([planProps, PropsOf(actions[1])], "plan progress");
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
        var fresh = TableOccurrenceRepository.ToEntity(receipt);
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
            throw new InvalidOperationException("occurrence transaction spans partitions");
    }

    private static IReadOnlyDictionary<string, object?> PropsOf(TableTransactionAction action) =>
        new Dictionary<string, object?>((TableEntity)action.Entity!);
}
