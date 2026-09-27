// Storage codecs and bounds: segmented properties and entity fit accounting. Row keys live with transactions.
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Persistence;

namespace RecurringTasksBot.Tests;

public sealed class StorageCodecTests
{

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
            var ex = Assert.Throws<PayloadIntegrityException>(() => SegmentedProperties.Read(p, "Answer"));
            Assert.Equal(OccurrenceFailureCodes.PayloadCorrupt, ex.Code);
        }
    }



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
        TableStorageLimits.CheckEntityFits(props, "worst-case answer");
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
        TableStorageLimits.CheckEntityFits(props, "worst-case snapshot");
        var total = props.Sum(p => TableStorageLimits.PropertyBytes(p.Key, p.Value));
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
        for (var i = 0; i <= TableStorageLimits.MaxCustomProperties; i++)
            tooMany[$"P_{i:D4}"] = "x";
        Assert.Throws<InvalidOperationException>(() => TableStorageLimits.CheckEntityFits(tooMany, "test"));
        var tooBig = new Dictionary<string, object?> { ["Answer"] = new string('x', 1024 * 1024) };
        Assert.Throws<InvalidOperationException>(() => TableStorageLimits.CheckEntityFits(tooBig, "test"));
    }
}
