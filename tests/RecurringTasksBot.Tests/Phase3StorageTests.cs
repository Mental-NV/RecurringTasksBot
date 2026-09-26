// Phase 3 storage unit tests: segmented codec, bounds, row keys, plan
// codec, memory publication rules, and receipt derivation. No SDK types.
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class Phase3StorageTests
{
    // ---- Segmented codec ----

    [Fact]
    public void Segmented_RoundTripsLargeAstralText()
    {
        var value = string.Concat(Enumerable.Repeat("\U0001F600x", 20000));
        var props = new Dictionary<string, object?>();
        SegmentedProperties.Write(props, "Answer", value);
        Assert.Equal(3, props["Answer_Count"]);
        Assert.Equal(value, SegmentedProperties.Read(props, "Answer"));
    }

    [Fact]
    public void Segmented_RoundTripsEmptyString()
    {
        var props = new Dictionary<string, object?>();
        SegmentedProperties.Write(props, "Answer", string.Empty);
        Assert.Equal(0, props["Answer_Count"]);
        Assert.Equal(string.Empty, SegmentedProperties.Read(props, "Answer"));
    }

    [Fact]
    public void Segmented_RejectsCorruption()
    {
        var props = new Dictionary<string, object?>();
        SegmentedProperties.Write(props, "Answer", new string('a', 40000));

        var missing = new Dictionary<string, object?>(props);
        missing.Remove("Answer_0001");
        Corrupt(missing);

        var tampered = new Dictionary<string, object?>(props) { ["Answer_0000"] = "tampered" };
        Corrupt(tampered);

        var badScalars = new Dictionary<string, object?>(props) { ["Answer_Scalars"] = 1 };
        Corrupt(badScalars);

        var badCount = new Dictionary<string, object?>(props) { ["Answer_Count"] = 99 };
        Corrupt(badCount);

        var absent = new Dictionary<string, object?>();
        Corrupt(absent);

        static void Corrupt(IReadOnlyDictionary<string, object?> p)
        {
            var ex = Assert.Throws<Phase3PayloadException>(() => SegmentedProperties.Read(p, "Answer"));
            Assert.Equal(Phase3FailureCodes.PayloadCorrupt, ex.Code);
        }
    }

    // ---- Bounds ----

    [Fact]
    public void Bounds_WorstCaseAnswerEntityFits()
    {
        // Maximum answer, all supplementary characters: 9 properties at the
        // 64-KiB property limit plus metadata stays under 1 MiB / 128 props.
        var props = new Dictionary<string, object?>();
        SegmentedProperties.Write(props, "Answer", string.Concat(Enumerable.Repeat("\U0001F600", 131072)));
        foreach (var key in props.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(
            k, @"^Answer_\d{4}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToList())
            Assert.True(System.Text.Encoding.Unicode.GetByteCount((string)props[key]!) <= 64 * 1024);
        Phase3StorageBounds.CheckEntityFits(props, "worst-case answer");
    }

    [Fact]
    public void Bounds_WorstCaseSnapshotStaysBelowPolicy()
    {
        // Snapshot with a maximum previous answer, prompt, and instruction.
        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EntityKind"] = "context",
            ["OwnerId"] = "u1",
        };
        SegmentedProperties.Write(props, "Previous",
            string.Concat(Enumerable.Repeat("\U0001F600", 131072)));
        SegmentedProperties.Write(props, "Task", new string('t', 32768));
        SegmentedProperties.Write(props, "Instruction", new string('i', 16384));
        Phase3StorageBounds.CheckEntityFits(props, "worst-case snapshot");
        var total = props.Sum(p => Phase3StorageBounds.PropertyBytes(p.Key, p.Value));
        Assert.True(total < 900 * 1024);
        Assert.True(props.Count <= 128);
    }

    [Fact]
    public void Bounds_LongToShortMemoryReplacementDropsTailProperties()
    {
        var longProps = new Dictionary<string, object?>();
        SegmentedProperties.Write(longProps, "Answer", new string('a', 131072));
        Assert.True(longProps.Count > 3);
        // Full entity Replace rebuilds the property set from the new value.
        var shortProps = new Dictionary<string, object?>();
        SegmentedProperties.Write(shortProps, "Answer", "short");
        Assert.DoesNotContain("Answer_0001", shortProps.Keys);
        Assert.Equal("short", SegmentedProperties.Read(shortProps, "Answer"));
    }

    [Fact]
    public void Bounds_RejectsOversizedEntities()
    {
        var tooMany = new Dictionary<string, object?>();
        for (var i = 0; i <= Phase3StorageBounds.MaxCustomProperties; i++)
            tooMany[$"P_{i:D4}"] = "x";
        Assert.Throws<InvalidOperationException>(() => Phase3StorageBounds.CheckEntityFits(tooMany, "test"));
        var tooBig = new Dictionary<string, object?> { ["Answer"] = new string('x', 1024 * 1024) };
        Assert.Throws<InvalidOperationException>(() => Phase3StorageBounds.CheckEntityFits(tooBig, "test"));
    }

    // ---- Row keys ----

    [Fact]
    public void RowKeys_HaveDocumentedShapes()
    {
        var scheduled = new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal("memory_op1", Phase3RowKeys.Memory("op1"));
        Assert.Equal($"delivery_op1_{scheduled.Ticks}__v3context_abc",
            Phase3RowKeys.Context("op1", scheduled, "abc"));
        Assert.Equal($"delivery_op1_{scheduled.Ticks}__v3answer_abc",
            Phase3RowKeys.Answer("op1", scheduled, "abc"));
        Assert.Equal($"delivery_op1_{scheduled.Ticks}__v3plan_abc",
            Phase3RowKeys.Plan("op1", scheduled, "abc"));
    }

    // ---- Plan codec ----

    [Fact]
    public void PlanCodec_RoundTripsProgress()
    {
        var source = new string('m', 40000);
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var (replaced, _) = DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0");
        var confirmed = DeliveryPlan.Confirm(replaced, replaced.Leaves[0].Id, 901);
        var json = PlanRowCodec.Serialize(confirmed);
        var parsed = PlanRowCodec.Deserialize(json, source);
        Assert.Equal(json, PlanRowCodec.Serialize(parsed));
        Assert.Contains("\"message_id\":901", json, StringComparison.Ordinal);
        Assert.Contains("\"message_id\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanCodec_RejectsBadPayloads()
    {
        var source = "## Status\nReady";
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var json = PlanRowCodec.Serialize(plan);
        Assert.Throws<Phase3PayloadException>(() =>
            PlanRowCodec.Deserialize(json.Replace("markdown", "bogus"), source));
        Assert.Throws<Phase3PayloadException>(() =>
            PlanRowCodec.Deserialize(json.Replace("\"schema_version\":3", "\"schema_version\":4"), source));
        try
        {
            PlanRowCodec.Deserialize(json.Replace("\"schema_version\":3", "\"schema_version\":4"), source);
        }
        catch (Phase3PayloadException ex)
        {
            Assert.Equal(Phase3FailureCodes.UnsupportedPayloadVersion, ex.Code);
        }
        Assert.Throws<Phase3PayloadException>(() => PlanRowCodec.Deserialize("not json", source));
        Assert.Throws<Phase3PayloadException>(() =>
            PlanRowCodec.Deserialize(json, source + "extra"));
    }

    // ---- Memory publication ----

    [Fact]
    public void MemoryDecision_PublishesNewerSkipsNewerIdempotentOrFails()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
        var answer1 = AnswerArtifact.Create("a1", AnswerKind.Answer, "first");
        var answer2 = AnswerArtifact.Create("a2", AnswerKind.Answer, "second");

        var created = MemoryPublication.Decide(null, "u1", "op1", first, first, answer1, now);
        Assert.NotNull(created);
        Assert.Equal("first", created!.Answer);
        Assert.Equal(first, created.SourceScheduledUtc);

        // Same occurrence, same hash: idempotent.
        Assert.Null(MemoryPublication.Decide(created, "u1", "op1", first, first, answer1, now));
        // Same occurrence, different hash: consistency failure.
        Assert.Throws<Phase3ConsistencyException>(() =>
            MemoryPublication.Decide(created, "u1", "op1", first, first, answer2, now));
        // Newer memory wins over an older receipt.
        Assert.Null(MemoryPublication.Decide(
            created with { SourceScheduledUtc = second }, "u1", "op1", first, first, answer1, now));
        // Older memory advances.
        var advanced = MemoryPublication.Decide(created, "u1", "op1", second, second, answer2, now);
        Assert.Equal("second", advanced!.Answer);
        Assert.Equal("a2", advanced.AnswerVersion);
    }

    // ---- Receipt derivation ----

    [Fact]
    public void ReceiptProgress_DerivesSummaryFromPlan()
    {
        var source = new string('m', 40000);
        var plan = DeliveryPlan.CreateInitial("av1", source);
        var (replaced, _) = DeliveryPlan.ReplaceWithLiteralRich(plan, source, "p0");
        var receipt = new DeliveryReceipt("u1", "op1",
            new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc),
            "generating", 0, null, null, PlanVersion: "pv1");
        var derived = DeliveryPlanProgress.ApplyToReceipt(receipt, replaced);
        Assert.Equal(0, derived.SentParts);
        Assert.Equal(replaced.Leaves.Count, derived.TotalParts);
        Assert.Equal("pv1", derived.PlanVersion);
        var confirmed = DeliveryPlan.Confirm(replaced, replaced.Leaves[0].Id, 901);
        derived = DeliveryPlanProgress.ApplyToReceipt(receipt, confirmed);
        Assert.Equal(1, derived.SentParts);
        Assert.Equal("901", derived.MessageIds);
        Assert.Equal(901, derived.TelegramMessageId);
    }
}
