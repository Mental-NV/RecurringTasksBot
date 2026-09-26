// Phase 3 adapter tests: real TableEntity/TableTransactionAction members,
// ETag conditions, operation fence, receipt derivation, and memory rules.
// The linked production batch builders run against real SDK types; no live
// service is needed because transactions are verified before submission.
using Azure;
using Azure.Data.Tables;
using RecurringTasksBot;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class Phase3TableBatchTests
{
    private static readonly DateTime Scheduled = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(Scheduled, TimeSpan.Zero);

    private static DeliveryReceipt Receipt(string claimId = "c1") => new(
        "u1", "op1", Scheduled, OccurrenceExecution.StatusGenerating, 0, null, null,
        UpdatedUtc: Now, ClaimId: claimId);

    private static TableEntity ReceiptEntity(DeliveryReceipt receipt, string etag = "etag-receipt")
    {
        var entity = TableDeliveryReceiptStore.ToEntity(receipt);
        entity.ETag = new ETag(etag);
        return entity;
    }

    private static TableEntity OpEntity(
        OperationStatus status = OperationStatus.Active, string etag = "etag-op") =>
        new("u1", "operation_op1")
        {
            ETag = new ETag(etag),
            ["Status"] = OperationStatusNames.ToName(status),
        };

    private static FrozenContextRequest InitRequest(string claimId = "c1") => new(
        "u1", "op1", Scheduled, claimId,
        new FrozenContextInputs("effective instruction", 24000, 32768,
            Phase3Options.PreviousSuccessfulReply, Scheduled));

    private static TableEntity ActionEntity(
        IReadOnlyList<TableTransactionAction> actions, int index, TableTransactionActionType type)
    {
        Assert.Equal(type, actions[index].ActionType);
        return Assert.IsType<TableEntity>(actions[index].Entity);
    }

    private static void AssertRealETags(IReadOnlyList<TableTransactionAction> actions)
    {
        foreach (var action in actions)
        {
            var entity = Assert.IsType<TableEntity>(action.Entity);
            if (action.ActionType == TableTransactionActionType.Add)
            {
                Assert.Equal(default(ETag), action.ETag);
                continue;
            }
            // The serialized request carries action.ETag as If-Match: it must
            // be the read ETag, never empty or a wildcard.
            Assert.Equal(entity.ETag, action.ETag);
            Assert.False(string.IsNullOrEmpty(action.ETag.ToString()));
            Assert.NotEqual(ETag.All, action.ETag);
        }
    }

    // ---- Initialize context ----

    [Fact]
    public void InitBatch_AddsSnapshotUpdatesReceiptAndFencesOperation()
    {
        var receipt = Receipt();
        var built = Phase3TableBatches.BuildInit(OpEntity(), TestRecords.Operation(),
            ReceiptEntity(receipt), receipt, null, InitRequest(), Now, 0);
        Assert.Equal(3, built.Actions.Count);
        AssertRealETags(built.Actions);

        var context = ActionEntity(built.Actions, 0, TableTransactionActionType.Add);
        Assert.Equal("u1", context.PartitionKey);
        Assert.StartsWith($"delivery_op1_{Scheduled.Ticks}__v3context_", context.RowKey);
        Assert.Equal("context", context["EntityKind"]);
        Assert.Equal(3, context["SchemaVersion"]);

        var updated = ActionEntity(built.Actions, 1, TableTransactionActionType.UpdateReplace);
        Assert.Equal(new ETag("etag-receipt"), updated.ETag);
        var parsed = TableDeliveryReceiptStore.ToReceipt("u1", "op1", Scheduled, updated);
        Assert.Equal((string)context["ContextVersion"]!, parsed.ContextVersion);
        Assert.True(parsed.ContextInitialized);
        Assert.Equal(3, parsed.PayloadSchemaVersion);
        Assert.Equal("phase3-v1", parsed.InstructionVersion);

        var fence = ActionEntity(built.Actions, 2, TableTransactionActionType.UpdateMerge);
        Assert.Equal(new ETag("etag-op"), fence.ETag);
        Assert.Equal(Now, fence[Phase3TableBatches.FenceProperty]);

        var record = Phase3TableBatches.ParseContext(context);
        Assert.Equal("Water the plants", record.TaskText);
        Assert.False(record.PreviousReplyPresent);
        Assert.Equal("no_memory_row", record.NoMemoryReason);
        Assert.Equal(built.Context, record);
    }

    [Fact]
    public void InitBatch_CopiesEligiblePreviousReply()
    {
        var memory = new MemoryRecord("u1", "op1",
            Scheduled.AddDays(-1), Scheduled.AddDays(-1), Now, "old", "## Prior\nDone", "hash", 12);
        var receipt = Receipt();
        var built = Phase3TableBatches.BuildInit(OpEntity(), TestRecords.Operation(),
            ReceiptEntity(receipt), receipt, memory, InitRequest(), Now, 2);
        var record = Phase3TableBatches.ParseContext(
            ActionEntity(built.Actions, 0, TableTransactionActionType.Add));
        Assert.True(record.PreviousReplyPresent);
        Assert.Equal("## Prior\nDone", record.PreviousReplyAnswer);
        Assert.Null(record.NoMemoryReason);
        var parsed = TableDeliveryReceiptStore.ToReceipt("u1", "op1", Scheduled,
            ActionEntity(built.Actions, 1, TableTransactionActionType.UpdateReplace));
        Assert.Equal(2, parsed.StorageConflicts);
    }

    [Fact]
    public void InitBatch_RejectsBadClaimsAndStoppedOperations()
    {
        var receipt = Receipt();
        Assert.Throws<ClaimLostException>(() => Phase3TableBatches.BuildInit(
            OpEntity(), TestRecords.Operation(), ReceiptEntity(receipt), receipt,
            null, InitRequest("other"), Now, 0));
        var stale = receipt with { UpdatedUtc = Now - TimeSpan.FromHours(1) };
        Assert.Throws<ClaimLostException>(() => Phase3TableBatches.BuildInit(
            OpEntity(), TestRecords.Operation(), ReceiptEntity(stale), stale,
            null, InitRequest(), Now, 0));
        Assert.Throws<Phase3OperationStoppedException>(() => Phase3TableBatches.BuildInit(
            OpEntity(OperationStatus.Deleted), TestRecords.Operation("u1", "op1", OperationStatus.Deleted),
            ReceiptEntity(receipt), receipt, null, InitRequest(), Now, 0));
    }

    // ---- Persist generation ----

    [Fact]
    public void PersistBatch_AddsAnswerAndPlanWithPointers()
    {
        var answer = AnswerArtifact.Create("av1", AnswerKind.Answer, "## Status\nReady");
        var plan = DeliveryPlan.CreateInitial("av1", "## Status\nReady");
        var receipt = Receipt() with { ContextVersion = "ctx", ContextInitialized = true };
        var usage = new ReceiptUsage("OpenRouter", "m", 10, 20, 0, false);
        var built = Phase3TableBatches.BuildPersist(OpEntity(), OperationStatus.Active,
            ReceiptEntity(receipt), receipt,
            new PersistGenerationRequest("u1", "op1", Scheduled, "c1", answer, plan, Scheduled, usage),
            "pv1", Now, 0);
        Assert.NotNull(built);
        Assert.Equal(4, built!.Actions.Count);
        AssertRealETags(built.Actions);

        var answerRow = ActionEntity(built.Actions, 0, TableTransactionActionType.Add);
        Assert.Equal($"delivery_op1_{Scheduled.Ticks}__v3answer_av1", answerRow.RowKey);
        var stored = Phase3TableBatches.ParseAnswer(answerRow);
        Assert.Equal(answer, stored.Answer);

        var planRow = ActionEntity(built.Actions, 1, TableTransactionActionType.Add);
        Assert.Equal($"delivery_op1_{Scheduled.Ticks}__v3plan_pv1", planRow.RowKey);
        Assert.Equal(
            PlanRowCodec.Serialize(plan),
            PlanRowCodec.Serialize(Phase3TableBatches.ParsePlan(planRow, "## Status\nReady")));

        var parsed = TableDeliveryReceiptStore.ToReceipt("u1", "op1", Scheduled,
            ActionEntity(built.Actions, 2, TableTransactionActionType.UpdateReplace));
        Assert.Equal("generated", parsed.ExecutionStatus);
        Assert.Equal("av1", parsed.AnswerVersion);
        Assert.Equal("pv1", parsed.PlanVersion);
        Assert.Equal(1, parsed.TotalParts);
        Assert.Equal(0, parsed.SentParts);
    }

    [Fact]
    public void PersistBatch_NeverRegeneratesAfterPointersCommit()
    {
        var answer = AnswerArtifact.Create("av1", AnswerKind.Answer, "## Status\nReady");
        var plan = DeliveryPlan.CreateInitial("av1", "## Status\nReady");
        var receipt = Receipt() with { AnswerVersion = "av1", PlanVersion = "pv1" };
        var usage = new ReceiptUsage("OpenRouter", "m", 10, 20, 0, false);
        Assert.Null(Phase3TableBatches.BuildPersist(OpEntity(), OperationStatus.Active,
            ReceiptEntity(receipt), receipt,
            new PersistGenerationRequest("u1", "op1", Scheduled, "c1", answer, plan, Scheduled, usage),
            "pv2", Now, 0));

        var other = AnswerArtifact.Create("av1", AnswerKind.Answer, "different");
        Assert.Throws<Phase3PayloadException>(() => Phase3TableBatches.BuildPersist(
            OpEntity(), OperationStatus.Active, ReceiptEntity(Receipt()), Receipt(),
            new PersistGenerationRequest("u1", "op1", Scheduled, "c1", other, plan, Scheduled, usage),
            "pv2", Now, 0));
    }

    // ---- Confirm / replace ----

    private static (DeliveryReceipt Receipt, TableEntity ReceiptEntity, TableEntity PlanEntity, DeliveryPlanDoc Plan, string Source)
        Progress()
    {
        const string source = "## Status\nReady";
        var answer = AnswerArtifact.Create("av1", AnswerKind.Answer, source);
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var receipt = Receipt() with
        {
            ContextVersion = "ctx",
            AnswerVersion = "av1",
            PlanVersion = "pv1",
            ExecutionStatus = "generated",
        };
        var planEntity = new TableEntity("u1", Phase3RowKeys.Plan("op1", Scheduled, "pv1"))
        {
            ETag = new ETag("etag-plan"),
        };
        foreach (var (key, value) in Phase3TableBatches.PlanProps("pv1", plan))
            planEntity[key] = value;
        return (receipt, ReceiptEntity(receipt), planEntity, plan, source);
    }

    [Fact]
    public void ConfirmBatch_PersistsProgressBeforeNextSend()
    {
        var (receipt, receiptEntity, planEntity, plan, _) = Progress();
        var built = Phase3TableBatches.BuildConfirm(receiptEntity, receipt, "## Status\nReady",
            planEntity, plan, new ConfirmLeafRequest("u1", "op1", Scheduled, "c1", "p0", 901), Now, 0);
        Assert.Equal(2, built.Actions.Count);
        AssertRealETags(built.Actions);
        var parsed = TableDeliveryReceiptStore.ToReceipt("u1", "op1", Scheduled,
            ActionEntity(built.Actions, 1, TableTransactionActionType.UpdateReplace));
        Assert.Equal(1, parsed.SentParts);
        Assert.Equal("901", parsed.MessageIds);
        Assert.Equal(901, parsed.TelegramMessageId);

        // Idempotent replay writes nothing.
        var replay = Phase3TableBatches.BuildConfirm(receiptEntity, parsed, "## Status\nReady",
            ActionEntity(built.Actions, 0, TableTransactionActionType.UpdateReplace),
            built.Plan, new ConfirmLeafRequest("u1", "op1", Scheduled, "c1", "p0", 901), Now, 0);
        Assert.Empty(replay.Actions);

        Assert.Throws<Phase3PayloadException>(() => Phase3TableBatches.BuildConfirm(
            receiptEntity, receipt, "## Status\nReady", planEntity, plan,
            new ConfirmLeafRequest("u1", "op1", Scheduled, "c1", "nope", 902), Now, 0));
    }

    [Fact]
    public void ReplaceBatch_AdvancesRevisionAndReceiptSummary()
    {
        var source = new string('m', 40000);
        var answer = AnswerArtifact.Create("av1", AnswerKind.Answer, source);
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var receipt = Receipt() with { AnswerVersion = "av1", PlanVersion = "pv1" };
        var planEntity = new TableEntity("u1", Phase3RowKeys.Plan("op1", Scheduled, "pv1"))
        {
            ETag = new ETag("etag-plan"),
        };
        foreach (var (key, value) in Phase3TableBatches.PlanProps("pv1", plan))
            planEntity[key] = value;
        var built = Phase3TableBatches.BuildReplace(receiptEntity: ReceiptEntity(receipt), receipt,
            source, planEntity, plan,
            new ReplaceLeafRequest("u1", "op1", Scheduled, "c1", "p0", PlanFallbackKind.ToLiteralRich),
            Now, 0);
        Assert.Equal(2, built.Plan.Revision);
        Assert.All(built.Fresh, l => Assert.StartsWith("p0#2:", l.Id));
        var parsed = TableDeliveryReceiptStore.ToReceipt("u1", "op1", Scheduled,
            ActionEntity(built.Actions, 1, TableTransactionActionType.UpdateReplace));
        Assert.Equal(built.Plan.Leaves.Count, parsed.TotalParts);
        Assert.Equal(0, parsed.SentParts);
        Assert.Equal(source, string.Concat(built.Plan.Leaves
            .Select(l => source.Substring(l.StartUtf16, l.LengthUtf16))));
    }

    // ---- Complete ----

    private static (TableEntity ReceiptEntity, DeliveryReceipt Receipt, Phase3TableBatches.StoredAnswer Stored,
        DeliveryPlanDoc Plan) Completion(string text = "## Status\nReady", AnswerKind kind = AnswerKind.Answer)
    {
        var answer = AnswerArtifact.Create("av1", kind, text);
        var plan = DeliveryPlan.CreateInitial("av1", text);
        plan = DeliveryPlan.Confirm(plan, "p0", 901);
        var receipt = Receipt() with
        {
            ContextVersion = "ctx",
            AnswerVersion = "av1",
            PlanVersion = "pv1",
            ExecutionStatus = kind == AnswerKind.Answer ? "generated" : "failed",
            SentParts = 1,
            TotalParts = 1,
            MessageIds = "901",
        };
        return (ReceiptEntity(receipt), receipt,
            new Phase3TableBatches.StoredAnswer(answer, Scheduled), plan);
    }

    [Fact]
    public void CompleteBatch_PublishesMemoryAtomically()
    {
        var (receiptEntity, receipt, stored, plan) = Completion();
        var actions = Phase3TableBatches.BuildComplete(receiptEntity, receipt,
            OpEntity(), OperationStatus.Active, stored, plan,
            null, null, "c1", Scheduled, Scheduled, 0, Now, 0);
        Assert.Equal(3, actions.Count);
        AssertRealETags(actions);
        var parsed = TableDeliveryReceiptStore.ToReceipt("u1", "op1", Scheduled,
            ActionEntity(actions, 0, TableTransactionActionType.UpdateReplace));
        Assert.Equal("sent", parsed.Status);
        var memory = ActionEntity(actions, 2, TableTransactionActionType.Add);
        Assert.Equal("memory_op1", memory.RowKey);
        Assert.Equal("## Status\nReady", Phase3TableBatches.ParseMemory(memory).Answer);
    }

    [Fact]
    public void CompleteBatch_FailureNoticeSkipsMemory()
    {
        var (receiptEntity, receipt, stored, plan) = Completion(
            "This run failed. Future runs remain scheduled.", AnswerKind.FailureNotice);
        var actions = Phase3TableBatches.BuildComplete(receiptEntity, receipt,
            OpEntity(), OperationStatus.Active, stored, plan,
            null, null, "c1", Scheduled, Scheduled, 0, Now, 0);
        Assert.Equal(2, actions.Count);
    }

    private static TableEntity MemoryEntity(MemoryRecord memory)
    {
        var entity = new TableEntity("u1", Phase3RowKeys.Memory(memory.OperationId))
        {
            ETag = new ETag("etag-memory"),
        };
        foreach (var (key, value) in Phase3TableBatches.MemoryProps(memory))
            entity[key] = value;
        return entity;
    }

    [Fact]
    public void CompleteBatch_NewerMemoryWins()
    {
        var (receiptEntity, receipt, stored, plan) = Completion();
        var newer = new MemoryRecord("u1", "op1", Scheduled.AddDays(1), Scheduled.AddDays(1),
            Now, "new", "newer answer", "hash", 12);
        var actions = Phase3TableBatches.BuildComplete(receiptEntity, receipt,
            OpEntity(), OperationStatus.Active, stored, plan,
            newer, MemoryEntity(newer), "c1", Scheduled, Scheduled, 0, Now, 0);
        Assert.Equal(2, actions.Count);
    }

    [Fact]
    public void CompleteBatch_SameHashIsIdempotentDifferentHashFails()
    {
        var (receiptEntity, receipt, stored, plan) = Completion();
        var same = new MemoryRecord("u1", "op1", Scheduled, Scheduled, Now,
            "av1", "## Status\nReady", stored.Answer.SourceSha256, stored.Answer.ScalarCount);
        Assert.Equal(2, Phase3TableBatches.BuildComplete(receiptEntity, receipt,
            OpEntity(), OperationStatus.Active, stored, plan,
            same, MemoryEntity(same), "c1", Scheduled, Scheduled, 0, Now, 0).Count);
        var different = same with { Answer = "other", SourceSha256 = "other" };
        Assert.Throws<Phase3ConsistencyException>(() => Phase3TableBatches.BuildComplete(
            receiptEntity, receipt, OpEntity(), OperationStatus.Active, stored, plan,
            different, MemoryEntity(different), "c1", Scheduled, Scheduled, 0, Now, 0));
    }

    [Fact]
    public void CompleteBatch_RejectsUnconfirmedWrongClaimAndStoppedOp()
    {
        var (receiptEntity, receipt, stored, _) = Completion();
        var unconfirmed = DeliveryPlan.CreateInitial("av1", "## Status\nReady");
        Assert.Throws<Phase3ConsistencyException>(() => Phase3TableBatches.BuildComplete(
            receiptEntity, receipt, OpEntity(), OperationStatus.Active, stored, unconfirmed,
            null, null, "c1", Scheduled, Scheduled, 0, Now, 0));
        var (_, _, storedPlan, plan) = Completion();
        Assert.Throws<ClaimLostException>(() => Phase3TableBatches.BuildComplete(
            receiptEntity, receipt, OpEntity(), OperationStatus.Active, storedPlan, plan,
            null, null, "other", Scheduled, Scheduled, 0, Now, 0));
        Assert.Throws<Phase3OperationStoppedException>(() => Phase3TableBatches.BuildComplete(
            receiptEntity, receipt, OpEntity(OperationStatus.Deleted), OperationStatus.Deleted,
            storedPlan, plan, null, null, "c1", Scheduled, Scheduled, 0, Now, 0));
    }

    // ---- Legacy receipt compatibility ----

    [Fact]
    public void OldReceiptRows_DecodeSafelyWithDefaults()
    {
        var legacy = new TableEntity("u1", "delivery_op1_123")
        {
            ["Status"] = "generating",
            ["Attempts"] = 2,
        };
        var parsed = TableDeliveryReceiptStore.ToReceipt("u1", "op1", Scheduled, legacy);
        Assert.Null(parsed.PayloadSchemaVersion);
        Assert.Null(parsed.ContextVersion);
        Assert.False(parsed.ContextInitialized);
        Assert.Null(parsed.AnswerVersion);
        Assert.Null(parsed.PlanVersion);
        Assert.Equal(0, parsed.StorageConflicts);
    }
}
