// Receipts: bounded command metadata, claim freshness, pre-upgrade decoding, and plan-derived summaries.
using RecurringTasksBot.Application;
using System.Text;
using System.Text.Json;

namespace RecurringTasksBot.Tests;

public sealed class ReceiptTests
{
    [Fact]
    public void FullLengthCreate_StoresBoundedReceiptCommand()
        {
        var full = "/create 0 0 9 * * * " + new string('x', 32768);
        var bounded = UpdateReceipts.BoundCommand(full);
        Assert.StartsWith("/create", bounded);
        Assert.True(TextLimits.CountChars(bounded) <= UpdateReceipts.MaxCommandChars);
        Assert.True(Encoding.UTF8.GetByteCount(bounded) <= 65536);
    }

    [Fact]
    public void BoundCommand_PreservesShortCommands_Verbatim()
        {
        Assert.Equal("/list", UpdateReceipts.BoundCommand("/list"));
    }

    [Fact]
    public void StaleGeneratingClaim_IsRecoverable()
        {
        var stale = new DeliveryReceipt("o", "op", DateTime.UtcNow, "generating",
            0, null, UpdatedUtc: DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        Assert.True(OccurrenceExecution.IsClaimStale(stale, DateTimeOffset.UtcNow));
        var fresh = stale with { UpdatedUtc = DateTimeOffset.UtcNow };
        Assert.False(OccurrenceExecution.IsClaimStale(fresh, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ShortReceiptConstruction_DefaultsCounters()
        {
        // Old short receipt construction still compiles and behaves.
        var receipt = new DeliveryReceipt("o", "op", DateTime.UtcNow, "sent", 1, null);
        Assert.Equal(0, receipt.GenerationAttempts);
        Assert.Equal(0, receipt.TotalParts);
    }



    [Fact]
    public void ReceiptProgress_DerivesSummaryFromPlan()
    {
        var source = new string('m', 40000);
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var (replaced, _) = DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0");
        var receipt = new DeliveryReceipt("u1", "op1",
            new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc),
            "generating", 0, null, PlanVersion: "pv1");
        var derived = DeliveryPlanProgress.ApplyToReceipt(receipt, replaced);
        Assert.Equal(0, derived.SentParts);
        Assert.Equal(replaced.Leaves.Count, derived.TotalParts);
        Assert.Equal("pv1", derived.PlanVersion);
        var confirmed = DeliveryPlan.Confirm(replaced, replaced.Leaves[0].Id, 901);
        derived = DeliveryPlanProgress.ApplyToReceipt(receipt, confirmed);
        Assert.Equal(1, derived.SentParts);
        Assert.Equal("901", derived.MessageIds);
    }
}
