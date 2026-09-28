using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

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
        var sender = new FakeTelegramSender();
        ops.Seed(TestRecords.Operation("alice", "opA"));
        var clock = new FakeClock();
        var h = new ExecuteOccurrenceHandler(ops, new FakeOccurrenceRepository(ops, clock), sender,
            new FakeLlmExecutor(), TestLlm.Execution(), TestLlm.ProviderName, TestLlm.ModelName, clock);

        var result = await h.ExecuteAttemptAsync("mallory", "opA",
            new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), 0);

        Assert.Equal(SingleAttemptOutcome.SkippedStopped, result.Outcome);
        Assert.Empty(sender.Payloads);
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
