using Moq;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class CreateFlowTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

    private static (CreateFlow Flow, FakeOperationStore Ops, FakeReceiptStore Receipts, FakeOrchestrations Orch) New()
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeReceiptStore();
        var orch = new FakeOrchestrations();
        return (new CreateFlow(ops, receipts, orch), ops, receipts, orch);
    }

    private const string Cmd = "/create 0 0 9 * * * hi";

    [Fact]
    public async Task FreshCreate_StartsOnce_AndCompletesReceipt()
    {
        var (flow, ops, receipts, orch) = New();
        var result = await flow.HandleCreateAsync(42, 777, 100, Cmd,
            "0 0 9 * * *", "hi", Now);

        Assert.Equal(CreateOutcome.Created, result.Outcome);
        Assert.Single(orch.StartedInstances);
        Assert.Equal(Ids.DeriveInstanceId(result.OperationId), orch.StartedInstances[0]);

        var op = await ops.GetAsync("42", result.OperationId);
        Assert.Equal(OperationStatus.Active, op!.Status);
        var receipt = await receipts.GetAsync("42", 100);
        Assert.True(receipt!.CommandCompleted);
        Assert.Equal(result.OperationId, receipt.OperationId);
    }

    [Fact]
    public async Task Redelivery_AfterCompletion_IsDuplicate_NoSecondStart()
    {
        var (flow, _, _, orch) = New();
        var first = await flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now);
        var second = await flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now);

        Assert.Equal(CreateOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Single(orch.StartedInstances);
    }

    [Fact]
    public async Task ConcurrentDuplicates_OneWinner_OneStart()
    {
        var (flow, _, _, orch) = New();
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now));
        var results = await Task.WhenAll(tasks);

        Assert.Single(orch.StartedInstances);
        var firstId = results[0].OperationId;
        Assert.All(results, r => Assert.Equal(firstId, r.OperationId));
        Assert.Contains(results, r => r.Outcome is CreateOutcome.Created or CreateOutcome.Resumed);
        Assert.Contains(results, r => r.Outcome == CreateOutcome.Duplicate);
    }

    [Fact]
    public async Task InterruptedCreation_ResumesWithSameIds()
    {
        var (flow, ops, receipts, orch) = New();
        // Crash between business-data write and orchestration startup.
        var opId = Ids.DeriveOperationId(42, 100, Cmd);
        await receipts.InsertAsync(new UpdateReceipt("42", 100, Cmd, opId, false, false, Now, Now));
        await ops.InsertStartingAsync(new OperationRecord("42", opId, 777, "0 0 9 * * *",
            "hi", OperationStatus.Starting, Ids.DeriveInstanceId(opId), null, Now, Now));

        var result = await flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now);

        Assert.Equal(opId, result.OperationId);
        Assert.Single(orch.StartedInstances);
        Assert.Equal(Ids.DeriveInstanceId(opId), orch.StartedInstances[0]);
        var op = await ops.GetAsync("42", opId);
        Assert.Equal(OperationStatus.Active, op!.Status);
    }

    [Fact]
    public async Task Resume_WhenStartupAlreadyAccepted_DoesNotStartAgain()
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeReceiptStore();
        var orch = new Mock<IOrchestrationClient>();
        var opId = Ids.DeriveOperationId(42, 100, Cmd);
        var instanceId = Ids.DeriveInstanceId(opId);
        await receipts.InsertAsync(new UpdateReceipt("42", 100, Cmd, opId, false, false, Now, Now));
        await ops.InsertStartingAsync(new OperationRecord("42", opId, 777, "0 0 9 * * *",
            "hi", OperationStatus.Starting, instanceId, null, Now, Now));
        orch.Setup(o => o.GetRuntimeStatusAsync(instanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Running");

        var flow = new CreateFlow(ops, receipts, orch.Object);
        var result = await flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now);

        orch.Verify(o => o.StartAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var op = await ops.GetAsync("42", opId);
        Assert.Equal(OperationStatus.Active, op!.Status);
        Assert.Equal(CreateOutcome.Resumed, result.Outcome);
    }

    [Fact]
    public async Task FailedOperation_IsNeverRestartedByRedelivery()
    {
        var (flow, ops, _, orch) = New();
        var opId = Ids.DeriveOperationId(42, 100, Cmd);
        ops.Seed(TestRecords.Operation("42", opId, OperationStatus.Failed));

        var result = await flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now);

        Assert.Equal(CreateOutcome.Duplicate, result.Outcome);
        Assert.Empty(orch.StartedInstances);
    }

    [Fact]
    public async Task DeletedOperation_IsNeverRestartedByRedelivery()
    {
        var (flow, ops, _, orch) = New();
        var opId = Ids.DeriveOperationId(42, 100, Cmd);
        ops.Seed(TestRecords.Operation("42", opId, OperationStatus.Deleted));

        var result = await flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now);

        Assert.Equal(CreateOutcome.Duplicate, result.Outcome);
        Assert.Empty(orch.StartedInstances);
    }

    [Fact]
    public async Task TransientStartFailure_LeavesWorkResumable()
    {
        var ops = new FakeOperationStore();
        var receipts = new FakeReceiptStore();
        var orch = new Mock<IOrchestrationClient>();
        orch.Setup(o => o.StartAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("durable down"));

        var flow = new CreateFlow(ops, receipts, orch.Object);
        var opId = Ids.DeriveOperationId(42, 100, Cmd);
        await Assert.ThrowsAsync<TransientStoreException>(() =>
            flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now));

        // Webhook maps this to 503; nothing is marked complete.
        var receipt = await receipts.GetAsync("42", 100);
        Assert.NotNull(receipt);
        Assert.False(receipt.CommandCompleted);
        var op = await ops.GetAsync("42", opId);
        Assert.Equal(OperationStatus.Starting, op!.Status);
    }

    [Fact]
    public async Task ConcurrentReceiptWinner_LoserSeesDuplicate_NoSecondStart()
    {
        var ops = new FakeOperationStore();
        var receipts = new Mock<IUpdateReceiptStore>();
        receipts.Setup(r => r.GetAsync("42", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpdateReceipt?)null);
        receipts.Setup(r => r.InsertAsync(It.IsAny<UpdateReceipt>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyConflictException("receipt exists"));
        var orch = new Mock<IOrchestrationClient>();

        var flow = new CreateFlow(ops, receipts.Object, orch.Object);
        var result = await flow.HandleCreateAsync(42, 777, 100, Cmd, "0 0 9 * * *", "hi", Now);

        Assert.Equal(CreateOutcome.Duplicate, result.Outcome);
        orch.Verify(o => o.StartAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void RecoveryDecision_Matrix()
    {
        var op = TestRecords.Operation("42", "op1", OperationStatus.Starting);
        Assert.Equal(CreationAction.ResumeStarting,
            CreationRecovery.Decide(null, op, orchestrationAccepted: false));
        Assert.Equal(CreationAction.AckCompleted,
            CreationRecovery.Decide(null, op, orchestrationAccepted: true));
        Assert.Equal(CreationAction.StartNew,
            CreationRecovery.Decide(null, null, orchestrationAccepted: false));
        Assert.Equal(CreationAction.AckTerminal,
            CreationRecovery.Decide(null,
                TestRecords.Operation("42", "op1", OperationStatus.Failed), false));
        Assert.Equal(CreationAction.AckTerminal,
            CreationRecovery.Decide(null,
                TestRecords.Operation("42", "op1", OperationStatus.Deleted), false));
        var done = new UpdateReceipt("42", 1, Cmd, "op1", true, false, Now, Now);
        Assert.Equal(CreationAction.AckCompleted,
            CreationRecovery.Decide(done, TestRecords.Operation("42", "op1"), false));
    }
}
