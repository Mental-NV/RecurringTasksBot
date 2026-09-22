using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class IdsTests
{
    [Fact]
    public void SameUpdate_MapsToSameIds()
    {
        var a = Ids.DeriveOperationId(42, 100, "/create 0 0 9 * * * hi");
        var b = Ids.DeriveOperationId(42, 100, "/create 0 0 9 * * * hi");
        Assert.Equal(a, b);
        Assert.Equal(Ids.DeriveInstanceId(a), Ids.DeriveInstanceId(b));
    }

    [Fact]
    public void DifferentUpdates_MapToDifferentIds()
    {
        var a = Ids.DeriveOperationId(42, 100, "/create 0 0 9 * * * hi");
        var b = Ids.DeriveOperationId(42, 101, "/create 0 0 9 * * * hi");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentUsers_MapToDifferentIds()
    {
        Assert.NotEqual(
            Ids.DeriveOperationId(1, 100, "/create 0 0 9 * * * hi"),
            Ids.DeriveOperationId(2, 100, "/create 0 0 9 * * * hi"));
    }

    [Fact]
    public void DeliveryDedupKey_DistinguishesOccurrences()
    {
        var t = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(Ids.DeliveryDedupKey("op", t), Ids.DeliveryDedupKey("op", t));
        Assert.NotEqual(Ids.DeliveryDedupKey("op", t), Ids.DeliveryDedupKey("op", t.AddDays(1)));
        Assert.NotEqual(Ids.DeliveryDedupKey("op1", t), Ids.DeliveryDedupKey("op2", t));
    }

    [Fact]
    public void RowKeys_FollowSpecPatterns()
    {
        Assert.StartsWith("operation_", Ids.OperationRowKey("abc"));
        Assert.StartsWith("update_", Ids.UpdateReceiptRowKey(7));
        Assert.StartsWith("delivery_op_", Ids.DeliveryReceiptRowKey("op",
            new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)));
    }
}
