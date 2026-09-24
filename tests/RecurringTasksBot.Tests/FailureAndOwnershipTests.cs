using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class FailureReportingTests
{
    [Fact]
    public void ActiveWithRunningOrchestration_ListsActive()
    {
        var (status, summary) = FailureReporter.ResolveListStatus(
            TestRecords.Operation("u", "op", OperationStatus.Active), "Running");
        Assert.Equal(OperationStatus.Active, status);
        Assert.Equal("failed", summary); // default when no detail stored
    }

    [Fact]
    public void OrchestrationFailure_SurfacesAsFailed()
    {
        var op = TestRecords.Operation("u", "op", OperationStatus.Active)
            with { FailureSummary = "delivery failed: timeout" };
        var (status, summary) = FailureReporter.ResolveListStatus(op, "Failed");
        Assert.Equal(OperationStatus.Failed, status);
        Assert.Equal("delivery failed: timeout", summary);
    }

    [Fact]
    public void TerminatedOrchestration_SurfacesAsFailed()
    {
        var (status, _) = FailureReporter.ResolveListStatus(
            TestRecords.Operation("u", "op", OperationStatus.Active), "Terminated");
        Assert.Equal(OperationStatus.Failed, status);
    }

    [Fact]
    public void StoredFailed_StaysFailed_RegardlessOfRuntime()
    {
        var op = TestRecords.Operation("u", "op", OperationStatus.Failed)
            with { FailureSummary = "permanent recipient failure: blocked" };
        var (status, summary) = FailureReporter.ResolveListStatus(op, "Running");
        Assert.Equal(OperationStatus.Failed, status);
        Assert.Contains("blocked", summary);
    }

    [Fact]
    public void StoredDeleted_StaysDeleted()
    {
        var (status, _) = FailureReporter.ResolveListStatus(
            TestRecords.Operation("u", "op", OperationStatus.Deleted), "Failed");
        Assert.Equal(OperationStatus.Deleted, status);
    }

    [Fact]
    public void Summary_IsTruncatedToShortError()
    {
        var long_ = new string('e', 500);
        var summary = FailureReporter.Summarise(long_);
        Assert.Equal(280, summary.Length);
    }

    [Fact]
    public void Summary_EmptyDetail_FallsBack()
    {
        Assert.Equal("failed", FailureReporter.Summarise(null));
        Assert.Equal("failed", FailureReporter.Summarise("   "));
    }
}

public sealed class OwnershipTests
{
    [Fact]
    public async Task Users_CannotReadEachOthersOperations()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("alice", "opA"));

        Assert.Null(await ops.GetAsync("bob", "opA"));
        Assert.NotNull(await ops.GetAsync("alice", "opA"));
    }

    [Fact]
    public async Task List_OnlyReturnsCallersOperations()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("alice", "opA"));
        ops.Seed(TestRecords.Operation("bob", "opB"));

        var alice = await ops.ListOwnedAsync("alice");
        Assert.Single(alice);
        Assert.Equal("opA", alice[0].OperationId);
    }

    [Fact]
    public async Task List_ExcludesDeleted_OwnOperations()
    {
        var ops = new FakeOperationStore();
        ops.Seed(TestRecords.Operation("alice", "opA"));
        ops.Seed(TestRecords.Operation("alice", "opB", OperationStatus.Deleted));

        var list = await ops.ListOwnedAsync("alice");
        Assert.Single(list);
    }

    [Fact]
    public async Task Delivery_IsOwnerScoped()
    {
        var ops = new FakeOperationStore();
        var d = new FakeDeliveryStore();
        var sender = new FakeTelegramSender();
        ops.Seed(TestRecords.Operation("alice", "opA"));
        var h = new DeliveryHandler(ops, d, new FakePayloadStore(), sender,
            new FakeLlmExecutor(), TestLlm.Options());

        var result = await h.DeliverAsync("mallory", "opA",
            new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DeliveryOutcome.SkippedDeletedOrFailed, result.Outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Receipts_AreOwnerScoped()
    {
        var receipts = new FakeReceiptStore();
        var now = DateTimeOffset.UtcNow;
        await receipts.InsertAsync(new UpdateReceipt("alice", 1, "/list", null, true, true, now, now));
        Assert.Null(await receipts.GetAsync("bob", 1));
    }
}
