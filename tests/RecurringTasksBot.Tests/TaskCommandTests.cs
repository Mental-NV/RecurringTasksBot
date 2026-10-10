using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

// Phase 5 command envelopes, patch semantics, get-mode rendering, and list building.
public sealed class TaskCommandTests
{
    private static TaskDefinition StoredDefinition() =>
        new("Old prompt", new TaskSchedule("0 0 9 * * *", []), "UTC", new TaskParameters(
            null, "high", null, null, null));

    [Theory]
    [InlineData("/get abc123", "abc123", "explicit")]
    [InlineData("/get abc123 effective", "abc123", "effective")]
    [InlineData("/get abc123 EXPLICIT", "abc123", "explicit")]
    public void GetArgs_Valid(string text, string expectedRef, string expectedMode)
    {
        Assert.True(TaskCommandParser.TryParseGetArgs(text, out var cmd, out _));
        Assert.NotNull(cmd);
        Assert.Equal(expectedRef, cmd.TaskRef);
        Assert.Equal(expectedMode, cmd.Mode);
    }

    [Theory]
    [InlineData("/get")]
    [InlineData("/get a b c")]
    [InlineData("/get abc123 yaml")]
    public void GetArgs_Invalid(string text)
    {
        Assert.False(TaskCommandParser.TryParseGetArgs(text, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Theory]
    [InlineData("/list", 1, false)]
    [InlineData("/list 2", 2, false)]
    [InlineData("/list compact", 1, true)]
    [InlineData("/list 3 compact", 3, true)]
    [InlineData("/list compact 3", 3, true)]
    public void ListArgs_Valid(string text, int page, bool compact)
    {
        Assert.True(TaskCommandParser.TryParseListArgs(text, out var cmd, out _));
        Assert.NotNull(cmd);
        Assert.Equal(page, cmd.Page);
        Assert.Equal(compact, cmd.Compact);
    }

    [Theory]
    [InlineData("/list 0")]
    [InlineData("/list -2")]
    [InlineData("/list soon")]
    [InlineData("/list 1 compact extra")]
    public void ListArgs_Invalid(string text)
    {
        Assert.False(TaskCommandParser.TryParseListArgs(text, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void UpdateArgs_SplitsRefAndJson()
    {
        Assert.True(TaskCommandParser.TrySplitUpdateArgs(
            """/update abc123 {"prompt": "x"}""", out var cmd, out _));
        Assert.NotNull(cmd);
        Assert.Equal("abc123", cmd.TaskRef);
        Assert.Equal("""{"prompt": "x"}""", cmd.JsonBody);
        Assert.False(TaskCommandParser.TrySplitUpdateArgs("/update abc123", out _, out _));
        Assert.False(TaskCommandParser.TrySplitUpdateArgs("/update abc123 soon", out _, out _));
    }

    [Fact]
    public void ResolveTaskRef_ExactPrefixAmbiguityRules()
    {
        var owned = new List<TaskRecord>
        {
            TestRecords.Task(id: "a31f9c00aa"),
            TestRecords.Task(id: "a31f9c11bb"),
            TestRecords.Task(id: "b82d0400cc", status: TaskState.Deleted),
        };
        Assert.Equal(TaskIdResolution.Ambiguous,
            TaskCommandParser.ResolveTaskRef(owned, "a31f9c", out _));
        Assert.Equal(TaskIdResolution.Found,
            TaskCommandParser.ResolveTaskRef(owned, "a31f9c00", out var one));
        Assert.Equal("a31f9c00aa", one!.TaskId);
        Assert.Equal(TaskIdResolution.TooShort,
            TaskCommandParser.ResolveTaskRef(owned, "a31f", out _));
        Assert.Equal(TaskIdResolution.NotFound,
            TaskCommandParser.ResolveTaskRef(owned, "b82d0400cc", out _));
        Assert.Equal(TaskIdResolution.NotFound,
            TaskCommandParser.ResolveTaskRef(owned, "zzzz9999", out _));
    }

    [Fact]
    public void ApplyUpdate_PromptOnly_PreservesInheritance()
    {
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"prompt": "New prompt"}""", out var patch, out _));
        Assert.Null(TaskUpdater.ApplyUpdate(StoredDefinition(), patch!, out var result));
        Assert.NotNull(result);
        Assert.False(result.IsUnchanged);
        Assert.True(result.PromptChanged);
        Assert.False(result.ScheduleChanged);
        Assert.False(result.LimitsChanged);
        Assert.Equal("New prompt", result.Definition.Prompt);
        Assert.Equal("high", result.Definition.Parameters.ReasoningEffort);
    }

    [Fact]
    public void ApplyUpdate_EffectiveValues_PinOverrides()
    {
        // A complete effective object is valid patch input; its concrete
        // generation values intentionally become overrides.
        var stored = StoredDefinition() with { Parameters = TaskParameters.Empty };
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"parameters": {"memoryMode": "None", "reasoningEffort": "low", "webSearch": true}}""",
            out var patch, out _));
        Assert.Null(TaskUpdater.ApplyUpdate(stored, patch!, out var result));
        Assert.NotNull(result);
        Assert.False(result.IsUnchanged);
        Assert.Equal("None", result.Definition.Parameters.MemoryMode);
        Assert.Equal("low", result.Definition.Parameters.ReasoningEffort);
        Assert.True(result.Definition.Parameters.WebSearch);
    }

    [Fact]
    public void ApplyUpdate_ReformattedSchedule_IsFieldLevelNoOp()
    {
        var stored = StoredDefinition() with
        {
            Schedule = new TaskSchedule("0 0 9 * * *", ["2026-12-08T09:00:00", "2026-12-19T15:00:00"]),
        };
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"schedule": {"cron": "0  0  9  *  *  *", "once": ["2026-12-19T15:00:00", "2026-12-08T09:00:00"]}, "parameters": {"webSearch": false}}""",
            out var patch, out _));
        Assert.Null(TaskUpdater.ApplyUpdate(stored, patch!, out var result));
        Assert.NotNull(result);
        Assert.False(result.ScheduleChanged);
        Assert.True(result.Definition.Schedule.Once[0] == "2026-12-08T09:00:00");
        Assert.False(result.Definition.Parameters.WebSearch);
    }

    [Fact]
    public void ApplyUpdate_UnchangedPatch_IsUnchanged()
    {
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"prompt": "Old prompt", "parameters": {"reasoningEffort": "high"}}""",
            out var patch, out _));
        Assert.Null(TaskUpdater.ApplyUpdate(StoredDefinition(), patch!, out var result));
        Assert.NotNull(result);
        Assert.True(result.IsUnchanged);
    }

    [Fact]
    public void ApplyUpdate_DifferentTimezone_IsRejected()
    {
        Assert.True(TaskDefinitionParser.TryParseUpdate(
            """{"timezone": "Europe/Moscow"}""", out var patch, out _));
        var error = TaskUpdater.ApplyUpdate(StoredDefinition(), patch!, out var result);
        Assert.NotNull(error);
        Assert.Equal("timezone_immutable", error.Code);
        Assert.Null(result);
    }

    [Fact]
    public void ExplicitRendering_PreservesOmissions()
    {
        Assert.True(TaskDefinitionParser.TryParseCreate(
            """{"prompt": "Hi", "schedule": {"cron": "0 0 9 * * *"}, "parameters": {"reasoningEffort": "high"}}""",
            out var def, out _));
        var json = TaskJsonRenderer.RenderExplicit(def!);
        Assert.DoesNotContain("timezone", json);
        Assert.DoesNotContain("once", json);
        Assert.DoesNotContain("memoryMode", json);
        Assert.DoesNotContain("webSearch", json);
        Assert.Contains("high", json);
    }

    [Fact]
    public void EffectiveRendering_FillsDefaultsWithoutMetadata()
    {
        Assert.True(TaskDefinitionParser.TryParseCreate(
            """{"prompt": "Hi", "schedule": {"cron": "0 0 9 * * *"}}""",
            out var def, out _));
        var json = TaskJsonRenderer.RenderEffective(def!, TaskDefaults.Default);
        Assert.Contains("\"timezone\": \"UTC\"", json);
        Assert.Contains("\"once\": []", json);
        Assert.Contains("\"memoryMode\": \"None\"", json);
        Assert.Contains("\"reasoningEffort\": \"low\"", json);
        Assert.Contains("\"webSearch\": true", json);
        Assert.DoesNotContain("revision", json);
        Assert.DoesNotContain("taskId", json);
    }

    [Fact]
    public void EffectiveOutput_IsValidUpdateInput()
    {
        Assert.True(TaskDefinitionParser.TryParseCreate(
            """{"prompt": "Hi", "schedule": {"cron": "0 0 9 * * *"}}""",
            out var def, out _));
        var json = TaskJsonRenderer.RenderEffective(def!, TaskDefaults.Default);
        Assert.True(TaskDefinitionParser.TryParseUpdate(json, out var patch, out var err));
        Assert.NotNull(patch);
    }

    [Fact]
    public void ExplicitAndEffectiveRendering_PreservesCyrillic()
    {
        Assert.True(TaskDefinitionParser.TryParseCreate(
            """{"prompt": "Ежедельно расскажи новости", "schedule": {"cron": "0 0 9 * * *"}}""",
            out var def, out _));
        var explicitJson = TaskJsonRenderer.RenderExplicit(def!);
        var effectiveJson = TaskJsonRenderer.RenderEffective(def!, TaskDefaults.Default);
        Assert.Contains("Ежедельно расскажи новости", explicitJson);
        Assert.Contains("Ежедельно расскажи новости", effectiveJson);
        Assert.DoesNotContain(@"\u", explicitJson);
        Assert.DoesNotContain(@"\u", effectiveJson);
    }

    [Fact]
    public void ListPrefixes_LengthenOnCollision()
    {
        var owned = new List<TaskRecord>
        {
            TestRecords.Task(id: "a31f9c00aa"),
            TestRecords.Task(id: "a31f9c11bb"),
        };
        var prefixes = TaskListBuilder.ComputePrefixes(owned);
        Assert.Equal("a31f9c0", prefixes["a31f9c00aa"]);
        Assert.Equal("a31f9c1", prefixes["a31f9c11bb"]);
    }

    [Fact]
    public void ListPage_SortsGroupsAndPreviewsNext()
    {
        var now = new DateTime(2026, 1, 1, 10, 20, 0, DateTimeKind.Utc);
        var owned = new List<TaskRecord>
        {
            TestRecords.Task(id: "b82d0400cc", timezone: "Europe/Moscow",
                createdAtUtc: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            TestRecords.Task(id: "a31f9c00aa", timezone: "UTC",
                createdAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        };
        var rows = TaskListBuilder.BuildPage(owned, null, now, 1);
        Assert.Equal(2, rows.Count);
        Assert.Equal("b82d04", rows[0].IdPrefix);
        Assert.Equal("active", rows[0].Status);
        // Daily 09:00 UTC task: latest due 09:00Z.
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), rows[1].NextUtc);
        // Daily 09:00 Moscow task created Jan 2: next is Jan 2 09:00 MSK.
        Assert.Equal(new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc), rows[0].NextUtc);
        var text = TaskListFormatter.FormatPage(rows, 1, now, compact: false);
        Assert.Contains("Europe/Moscow", text);
        Assert.Contains("ID | Status | Next | Expire | Prompt", text);
    }

    [Fact]
    public void ListPage_EmptyAndCompletedStates()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Contains("No recurring tasks",
            TaskListFormatter.FormatPage([], 1, now, compact: false));
        Assert.Contains("page 2",
            TaskListFormatter.FormatPage([], 2, now, compact: false));
        var owned = new List<TaskRecord> { TestRecords.Task(status: TaskState.Completed) };
        var rows = TaskListBuilder.BuildPage(owned, null, now, 1);
        Assert.Equal("completed", rows[0].Status);
        Assert.Null(rows[0].NextUtc);
    }

    [Theory]
    [InlineData("AI news today please read", "AI news today…")]
    [InlineData("Hi", "Hi")]
    [InlineData("Review the release checklist for today", "Review the release…")]
    public void PromptPreview_TruncatesToWordsAndChars(string prompt, string expectedStart)
    {
        var preview = TaskListBuilder.PromptPreview(prompt);
        Assert.StartsWith(expectedStart, preview);
    }
}
