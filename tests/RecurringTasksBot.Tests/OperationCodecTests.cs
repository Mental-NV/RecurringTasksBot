// Checkpoint D2 tests: the single operation entity codec, mandatory
// SchemaVersion = 1 on fresh rows, removal of legacy payload/version
// fields, and the plan row carrying ranges only (never answer text).
using Azure.Data.Tables;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Persistence;

namespace RecurringTasksBot.Tests;

public sealed class OperationCodecTests
{
    private static readonly DateTime Created = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset CreatedDto = new(Created, TimeSpan.Zero);

    private static OperationRecord Record(string text = "0 0 9 * * * standup reminder") =>
        new("u1", "op1", 111L, "0 0 9 * * *", text, OperationStatus.Active,
            "recurring-op1", null, CreatedDto, CreatedDto);

    [Fact]
    public void OperationCodec_RoundTrips()
    {
        var entity = OperationRowCodec.ToEntity(Record());
        Assert.Equal("u1", entity.PartitionKey);
        Assert.Equal("operation_op1", entity.RowKey);
        var parsed = OperationRowCodec.FromEntity("u1", "op1", entity);
        Assert.Equal(Record(), parsed);
    }

    [Fact]
    public void OperationCodec_WritesSchemaVersion1()
    {
        var entity = OperationRowCodec.ToEntity(Record());
        Assert.Equal("operation", entity["EntityKind"]);
        Assert.Equal(1, entity["SchemaVersion"]);
    }

    [Fact]
    public void OperationCodec_WorstCaseSupplementaryPromptFitsEntityBudget()
    {
        // Maximum prompt, all supplementary characters: segmented text
        // properties stay under the 64-KiB property limit and the entity
        // stays under the 900-KiB / 128-property policy.
        var prompt = string.Concat(Enumerable.Repeat("\U0001F600", TextLimits.MaxPromptChars));
        var entity = OperationRowCodec.ToEntity(Record(prompt));
        var props = new Dictionary<string, object?>(entity, StringComparer.Ordinal);
        foreach (var key in props.Keys.Where(k => k.StartsWith("Text_", StringComparison.Ordinal)
            && !k.EndsWith("_Count") && !k.EndsWith("_Scalars") && !k.EndsWith("_Sha256")).ToList())
            Assert.True(System.Text.Encoding.Unicode.GetByteCount((string)props[key]!) <= 64 * 1024);
        TableStorageLimits.CheckEntityFits(props, "worst-case operation");
        Assert.True(props.Count <= 128);
        Assert.Equal(prompt, OperationRowCodec.FromEntity("u1", "op1", entity).Text);
    }

    [Fact]
    public void OperationCodec_RejectsMissingSchema()
    {
        var entity = OperationRowCodec.ToEntity(Record());
        entity.Remove("SchemaVersion");
        var ex = Assert.Throws<PayloadIntegrityException>(() =>
            OperationRowCodec.FromEntity("u1", "op1", entity));
        Assert.Equal(OccurrenceFailureCodes.PayloadCorrupt, ex.Code);
    }

    [Fact]
    public void OperationCodec_RejectsUnknownSchema()
    {
        // Pre-upgrade rows (schema 3) fail as integrity errors, never as
        // defaults or partial text.
        var entity = OperationRowCodec.ToEntity(Record());
        entity["SchemaVersion"] = 3;
        var ex = Assert.Throws<PayloadIntegrityException>(() =>
            OperationRowCodec.FromEntity("u1", "op1", entity));
        Assert.Equal(OccurrenceFailureCodes.UnsupportedPayloadVersion, ex.Code);
    }

    [Fact]
    public void OperationCodec_RejectsWrongKind()
    {
        var entity = OperationRowCodec.ToEntity(Record());
        entity["EntityKind"] = "memory";
        var ex = Assert.Throws<PayloadIntegrityException>(() =>
            OperationRowCodec.FromEntity("u1", "op1", entity));
        Assert.Equal(OccurrenceFailureCodes.PayloadCorrupt, ex.Code);
    }

    [Fact]
    public void ReceiptEntity_CarriesSchemaVersion1WithoutLegacyPayloadVersion()
    {
        var receipt = new DeliveryReceipt("u1", "op1",
            new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc),
            "generating", 0, null, UpdatedUtc: CreatedDto, ClaimId: "c1");
        var entity = TableOccurrenceRepository.ToEntity(receipt);
        Assert.Equal(1, entity["SchemaVersion"]);
        Assert.False(entity.ContainsKey("PayloadVersion"));
    }

    [Fact]
    public void PlanProps_StoreRangesOnlyNeverAnswerText()
    {
        var source = "## Status\nReady";
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var props = OccurrenceEntityCodec.PlanProps("pv1", plan);
        Assert.DoesNotContain(props.Keys, k => k.StartsWith("Answer_", StringComparison.Ordinal));
        var entity = new TableEntity("u1", TableRowKeys.Plan("op1", Created, "pv1"));
        foreach (var (key, value) in props)
            entity[key] = value;
        Assert.Equal(PlanRowCodec.Serialize(plan),
            PlanRowCodec.Serialize(OccurrenceEntityCodec.ParsePlan(entity, source)));
    }
}
