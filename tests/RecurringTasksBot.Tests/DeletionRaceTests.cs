using Moq;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class DeletionRaceTests
{
    [Fact]
    public async Task Delete_PersistsBeforeTerminating()
    {
        var order = new List<string>();
        var ops = new Mock<IOperationStore>();
        var orch = new Mock<IOrchestrationClient>();
        var op = TestRecords.Operation("42", "op1");
        ops.Setup(o => o.GetAsync("42", "op1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        ops.Setup(o => o.CompareAndSwapStatusAsync("42", "op1", OperationStatus.Active,
                OperationStatus.Deleted, null, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("persist"))
            .ReturnsAsync(true);
        orch.Setup(o => o.TerminateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("terminate"))
            .Returns(Task.CompletedTask);

        var deleted = await DeletionHandler.DeleteAsync(
            ops.Object, orch.Object, "42", "op1");

        Assert.True(deleted);
        Assert.Equal(["persist", "terminate"], order);
    }

    [Fact]
    public async Task Delete_UnknownOperation_ReturnsFalse_NoTerminate()
    {
        var ops = new Mock<IOperationStore>();
        var orch = new Mock<IOrchestrationClient>();
        ops.Setup(o => o.GetAsync("42", "nope", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationRecord?)null);

        Assert.False(await DeletionHandler.DeleteAsync(ops.Object, orch.Object, "42", "nope"));
        orch.Verify(o => o.TerminateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RepeatedDelete_IsHarmless()
    {
        var ops = new FakeOperationStore();
        var orch = new FakeOrchestrations();
        ops.Seed(TestRecords.Operation("42", "op1", OperationStatus.Deleted));

        Assert.False(await DeletionHandler.DeleteAsync(ops, orch, "42", "op1"));
        Assert.Empty(orch.TerminatedInstances);
    }

    [Fact]
    public async Task Delete_WrongOwner_FindsNothing()
    {
        var ops = new FakeOperationStore();
        var orch = new FakeOrchestrations();
        ops.Seed(TestRecords.Operation("42", "op1"));

        Assert.False(await DeletionHandler.DeleteAsync(ops, orch, "other", "op1"));
        Assert.Empty(orch.TerminatedInstances);
        Assert.Equal(OperationStatus.Active, (await ops.GetAsync("42", "op1"))!.Status);
    }

    [Fact]
    public async Task TerminateFailure_StillKeepsTombstone()
    {
        var ops = new FakeOperationStore();
        var orch = new Mock<IOrchestrationClient>();
        orch.Setup(o => o.TerminateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientStoreException("durable down"));
        ops.Seed(TestRecords.Operation("42", "op1"));

        Assert.True(await DeletionHandler.DeleteAsync(ops, orch.Object, "42", "op1"));
        Assert.Equal(OperationStatus.Deleted, (await ops.GetAsync("42", "op1"))!.Status);
    }

    [Fact]
    public async Task Delete_DuringStarting_PreventsActivation()
    {
        // Deletion concurrent with creation: tombstone wins; a later resume
        // must not restart the operation.
        var ops = new FakeOperationStore();
        var orch = new FakeOrchestrations();
        var receipts = new FakeReceiptStore();
        var opId = Ids.DeriveOperationId(42, 100, "/create 0 0 9 * * * hi");
        ops.Seed(new OperationRecord("42", opId, 777, "0 0 9 * * *", "hi",
            OperationStatus.Starting, Ids.DeriveInstanceId(opId), null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        Assert.True(await DeletionHandler.DeleteAsync(ops, orch, "42", opId));

        var flow = new CreateFlow(ops, receipts, orch);
        var resumed = await flow.HandleCreateAsync(42, 777, 100,
            "/create 0 0 9 * * * hi", "0 0 9 * * *", "hi", DateTimeOffset.UtcNow);
        Assert.Equal(CreateOutcome.Duplicate, resumed.Outcome);
        Assert.Empty(orch.StartedInstances);
    }
}
