using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

// Phase 5 task contract: JSON-only create, explicit/effective get shapes,
// partial update, inheritance, and no-op schedule comparison.
public sealed class TaskDefinitionTests
{
    private const string CombinedCreate = """
        {
          "prompt": "Review the release checklist",
          "schedule": {
            "cron": "0 0 9 5 * *",
            "once": ["2026-12-08T09:00:00", "2026-12-19T15:00:00"]
          },
          "timezone": "Europe/Moscow",
          "parameters": {
            "reasoningEffort": "high",
            "expiresAt": "2027-01-01T00:00:00",
            "maxOccurrences": 5
          }
        }
        """;

    [Fact]
    public void SpecCombinedExample_ParsesWithExplicitOverridesOnly()
    {
        Assert.True(TaskDefinitionParser.TryParseCreate(CombinedCreate, out var def, out var err));
        Assert.NotNull(def);
        Assert.Equal("Review the release checklist", def.Prompt);
        Assert.Equal("0 0 9 5 * *", def.Schedule.Cron);
        Assert.Equal(["2026-12-08T09:00:00", "2026-12-19T15:00:00"], def.Schedule.Once);
        Assert.Equal("Europe/Moscow", def.Timezone);
        Assert.Null(def.Parameters.MemoryMode);
        Assert.Equal("high", def.Parameters.ReasoningEffort);
        Assert.Null(def.Parameters.WebSearch);
        Assert.Equal("2027-01-01T00:00:00", def.Parameters.ExpiresAt);
        Assert.Equal(5, def.Parameters.MaxOccurrences);
    }

    [Fact]
    public void SpecMoscowExample_ResolvesToDocumentedUtcInstants()
    {
        Assert.True(TaskTimezones.TryResolve("Europe/Moscow", out var zone));
        Assert.NotNull(zone);
        Assert.True(TaskTimezones.TryParseLocalInput("2026-12-08T09:00:00", zone, out var first, out _));
        Assert.True(TaskTimezones.TryParseLocalInput("2026-12-19T15:00:00", zone, out var second, out _));
        Assert.Equal(new DateTimeOffset(2026, 12, 8, 6, 0, 0, TimeSpan.Zero),
            TaskTimezones.ConvertToUtc(first, zone));
        Assert.Equal(new DateTimeOffset(2026, 12, 19, 12, 0, 0, TimeSpan.Zero),
            TaskTimezones.ConvertToUtc(second, zone));
    }

    [Fact]
    public void Effective_FillsDefaultsWithoutMetadata()
    {
        Assert.True(TaskDefinitionParser.TryParseCreate(CombinedCreate, out var def, out _));
        var effective = TaskDefinitionParser.ResolveEffective(def!, TaskDefaults.Default);
        Assert.Equal("None", effective.MemoryMode);
        Assert.Equal("high", effective.ReasoningEffort);
        Assert.True(effective.WebSearch);
        Assert.Equal("2027-01-01T00:00:00", effective.ExpiresAt);
        Assert.Equal(5, effective.MaxOccurrences);
        Assert.Equal("Europe/Moscow", effective.Timezone);
    }

    [Fact]
    public void Update_NullResetsOverrideToInheritance()
    {
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"parameters": {"reasoningEffort": null}}""", out var update, out var err));
        Assert.NotNull(update);
        Assert.True(update.ParametersSupplied);
        Assert.Null(update.Parameters!.ReasoningEffort);
        var stored = new TaskDefinition("P", new TaskSchedule("0 0 9 * * *", []),
            "UTC", new TaskParameters(null, "high", null, null, null));
        var merged = stored with
        {
            Parameters = stored.Parameters with { ReasoningEffort = update.Parameters.ReasoningEffort },
        };
        var effective = TaskDefinitionParser.ResolveEffective(merged, TaskDefaults.Default);
        Assert.Equal("low", effective.ReasoningEffort);
    }

    [Theory]
    [InlineData("""{"prompt": "x", "ownerId": "1"}""", "field_read_only")]
    [InlineData("""{"prompt": "x", "status": "active"}""", "field_read_only")]
    [InlineData("""{"prompt": "x", "waterlineUtc": "2026-01-01T00:00:00Z"}""", "field_read_only")]
    [InlineData("""{"prompt": "x", "bogus": 1}""", "unknown_field")]
    [InlineData("""{"prompt": "x", "prompt": "y"}""", "duplicate_field")]
    [InlineData("""{"prompt": ""}""", "prompt_invalid")]
    [InlineData("""{"schedule": {"cron": "0 0 9 * * *"}}""", "prompt_invalid")]
    [InlineData("""{"prompt": "x", "schedule": null}""", "schedule_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": null}}""", "cron_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "nope"}}""", "cron_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"once": ["2026-12-08T09:00:00"]}}""", null)] // one-time-only, valid
    [InlineData("""{"prompt": "x", "schedule": {"once": []}}""", "schedule_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {}}""", "schedule_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "0 0 9 * * *"}, "timezone": "MSK"}""",
        "timezone_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "0 0 9 * * *"}, "parameters": null}""",
        "parameters_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "0 0 9 * * *"}, "parameters": {"webSearch": "yes"}}""",
        "parameters_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "0 0 9 * * *"}, "parameters": {"maxOccurrences": 0}}""",
        "occurrence_limit_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "0 0 9 * * *"}, "parameters": {"maxOccurrences": 1.5}}""",
        "occurrence_limit_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "0 0 9 * * *"}, "parameters": {"maxOccurrences": "5"}}""",
        "occurrence_limit_invalid")]
    [InlineData("""{"prompt": "x", "schedule": {"cron": "0 0 9 * * *"}, "parameters": {"expiresAt": "2027-01-01T00:00:00Z"}}""",
        "expiration_invalid")]
    public void CreateShape_ValidationCodes(string json, string? expectedCode)
    {
        var ok = TaskDefinitionParser.TryParseCreate(json, out var def, out var err);
        if (expectedCode is null)
        {
            Assert.True(ok, err?.Message);
            Assert.NotNull(def);
        }
        else
        {
            Assert.False(ok);
            Assert.NotNull(err);
            Assert.Equal(expectedCode, err.Code);
        }
    }

    [Fact]
    public void BerlinDstGap_IsRejected()
    {
        var ok = TaskDefinitionParser.TryParseCreate(
            """{"prompt": "x", "timezone": "Europe/Berlin", "schedule": {"once": ["2026-03-29T02:30:00"]}}""",
            out _, out var err);
        Assert.False(ok);
        Assert.Equal("local_time_nonexistent", err!.Code);
    }

    [Fact]
    public void BerlinDstFold_IsRejected()
    {
        var ok = TaskDefinitionParser.TryParseCreate(
            """{"prompt": "x", "timezone": "Europe/Berlin", "schedule": {"once": ["2026-10-25T02:30:00"]}}""",
            out _, out var err);
        Assert.False(ok);
        Assert.Equal("local_time_ambiguous", err!.Code);
    }

    [Fact]
    public void Update_EmptyPatch_IsRejected()
    {
        Assert.False(TaskDefinitionParser.TryParseUpdate("{}", out _, out var err));
        Assert.Equal("patch_empty", err!.Code);
        Assert.False(TaskDefinitionParser.TryParseUpdate(
            """{"parameters": {}}""", out _, out err));
        Assert.Equal("patch_empty", err!.Code);
    }

    [Fact]
    public void Update_RepeatedTimezone_IsNoOp_DifferentIsImmutable()
    {
        Assert.Null(TaskDefinitionParser.CheckTimezoneNoChange("Europe/Moscow", "Europe/Moscow"));
        Assert.Null(TaskDefinitionParser.CheckTimezoneNoChange(null, "Europe/Moscow"));
        var err = TaskDefinitionParser.CheckTimezoneNoChange("UTC", "Europe/Moscow");
        Assert.NotNull(err);
        Assert.Equal("timezone_immutable", err.Code);
    }

    [Fact]
    public void TimezoneDataVersion_IsPinned()
    {
        // Bundled TZDB release: upgrading the NodaTime package is the
        // deliberate data upgrade; this pin fails loudly when it changes.
        Assert.Equal("TZDB: 2026d (mapping: 48.2)", TaskTimezones.PinnedDataVersion);
    }

    [Fact]
    public void TimezoneResolution_AdmitsIanaAndUtcOnly()
    {
        Assert.True(TaskTimezones.TryResolve("UTC", out _));
        Assert.True(TaskTimezones.TryResolve("Europe/Berlin", out _));
        Assert.False(TaskTimezones.TryResolve("EST", out _));
        Assert.False(TaskTimezones.TryResolve("Eastern Standard Time", out _));
        Assert.False(TaskTimezones.TryResolve("PST8PDT", out _));
        Assert.False(TaskTimezones.TryResolve(null, out _));
        Assert.False(TaskTimezones.TryResolve("Mars/Olympus", out _));
    }

    [Fact]
    public void Update_ExpirationUsesStoredTimezone()
    {
        // 2027-03-28T02:30 does not exist in Europe/Berlin (DST gap) but is
        // a valid UTC time: without the stored zone the patch corrupts.
        Assert.False(TaskDefinitionParser.TryParseUpdate(
            """{"parameters": {"expiresAt": "2027-03-28T02:30:00"}}""",
            "Europe/Berlin", out _, out var gapError));
        Assert.Equal("local_time_nonexistent", gapError!.Code);
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"parameters": {"expiresAt": "2027-03-28T03:30:00"}}""",
            "Europe/Berlin", out var patch, out _));
        Assert.Equal("2027-03-28T03:30:00", patch!.Parameters!.ExpiresAt);
    }

    [Fact]
    public void Update_ExplicitDatesUseStoredTimezone()
    {
        Assert.False(TaskDefinitionParser.TryParseUpdate(
            """{"schedule": {"once": ["2027-03-28T02:30:00"]}}""",
            "Europe/Berlin", out _, out var gapError));
        Assert.Equal("local_time_nonexistent", gapError!.Code);
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"schedule": {"once": ["2027-03-28T03:30:00"]}}""",
            "Europe/Berlin", out var patch, out _));
        Assert.Equal(["2027-03-28T03:30:00"], patch!.Schedule!.Once);
    }

    [Fact]
    public void ScheduleEquality_IgnoresFormattingButDetectsReplacement()
    {
        var a = new TaskSchedule("0 0 9 5 * *", ["2026-12-08T09:00:00", "2026-12-19T15:00:00"]);
        var reformatted = new TaskSchedule("0  0  9  5  *  *",
            ["2026-12-19T15:00:00", "2026-12-08T09:00:00"]);
        var cronOnlyNoOnce = new TaskSchedule("0 0 9 5 * *", []);
        var cronOnlyAbsentOnce = new TaskSchedule("0 0 9 5 * *", null!);
        Assert.True(TaskDefinitionParser.SchedulesEqual(a, reformatted));
        Assert.True(TaskDefinitionParser.SchedulesEqual(cronOnlyNoOnce, cronOnlyAbsentOnce));
        Assert.False(TaskDefinitionParser.SchedulesEqual(a, cronOnlyNoOnce));
        Assert.False(TaskDefinitionParser.SchedulesEqual(
            a, new TaskSchedule("0 0 10 5 * *", ["2026-12-08T09:00:00", "2026-12-19T15:00:00"])));
    }
}
