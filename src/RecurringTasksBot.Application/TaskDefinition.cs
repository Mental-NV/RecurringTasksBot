// Phase 5 task contract (docs/Spec.Phase5.md): JSON-only /create, explicit/
// effective /get, partial /update. Create/update share field definitions;
// generation parameters inherit live global defaults unless overridden.
using System.Text.Json;
using System.Text.RegularExpressions;
using NodaTime;

namespace RecurringTasksBot.Application;

public static class TaskDefinitionErrorCodes
{
    public const string UnknownField = "unknown_field";
    public const string DuplicateField = "duplicate_field";
    public const string FieldReadOnly = "field_read_only";
    public const string PromptInvalid = "prompt_invalid";
    public const string ScheduleInvalid = "schedule_invalid";
    public const string CronInvalid = "cron_invalid";
    public const string TimezoneInvalid = "timezone_invalid";
    public const string TimezoneImmutable = "timezone_immutable";
    public const string LocalTimeNonexistent = "local_time_nonexistent";
    public const string LocalTimeAmbiguous = "local_time_ambiguous";
    public const string ScheduleNoFutureOccurrence = "schedule_no_future_occurrence";
    public const string ScheduleDateInvalid = "schedule_date_invalid";
    public const string ExpirationInvalid = "expiration_invalid";
    public const string OccurrenceLimitInvalid = "occurrence_limit_invalid";
    public const string ParametersInvalid = "parameters_invalid";
    public const string PatchEmpty = "patch_empty";
}

public sealed record TaskDefinitionError(string Code, string FieldPath, string Message);

public static class TaskMemoryModes
{
    public const string IncludePreviousMessage = "IncludePreviousMessage";
    public const string None = "None";
}

// Effective task defaults, derived once at startup from the shared
// Memory:Mode, Llm:ReasoningEffort, and Llm:SearchEnabled settings (not
// from a TaskDefaults configuration section, which was removed).
// Explicit task parameters inherit from this record; already persisted
// occurrence claims retain their frozen values. Revision is a fingerprint
// of the effective defaults ("defaults-v2:<sha256>"); legacy revision
// strings remain readable and are never recomputed for retained claims.
public sealed record TaskDefaults(string MemoryMode, string ReasoningEffort, bool WebSearch,
    string Revision = "1")
{
    public static readonly TaskDefaults Default = new(
        TaskMemoryModes.None, TaskReasoningEfforts.Low, WebSearch: true);

    public void Validate()
    {
        if (!MemoryMode.Equals(TaskMemoryModes.IncludePreviousMessage, StringComparison.Ordinal) &&
            !MemoryMode.Equals(TaskMemoryModes.None, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Unknown TaskDefaults memoryMode '{MemoryMode}'.");
        if (!TaskReasoningEfforts.All.Contains(ReasoningEffort))
            throw new InvalidOperationException(
                $"Unknown TaskDefaults reasoningEffort '{ReasoningEffort}'.");
    }
}

public static class TaskReasoningEfforts
{
    public const string Low = "low";
    public const string Med = "med";
    public const string High = "high";
    public const string Xhigh = "xhigh";
    public const string Max = "max";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Low, Med, High, Xhigh, Max,
    };
}

public sealed record TaskSchedule(string? Cron, IReadOnlyList<string> Once, bool OnceExplicit = false)
{
    public static readonly TaskSchedule Empty = new(null, []);
}

// Explicit overrides only. Null (or omission) means inherit the global
// default for generation parameters, or no limit for stop limits; an
// inheritance reset is stored as an omitted field.
public sealed record TaskParameters(
    string? MemoryMode,
    string? ReasoningEffort,
    bool? WebSearch,
    string? ExpiresAt,
    int? MaxOccurrences)
{
    public static readonly TaskParameters Empty = new(null, null, null, null, null);
}

public sealed record TaskDefinition(
    string? Prompt,
    TaskSchedule Schedule,
    string Timezone,
    TaskParameters Parameters,
    bool TimezoneExplicit = false);

// Effective settings for a new occurrence: same shape, missing values
// filled from current global defaults and fixed task defaults.
public sealed record EffectiveTaskSettings(
    string Prompt,
    string? Cron,
    IReadOnlyList<string> Once,
    string Timezone,
    string MemoryMode,
    string ReasoningEffort,
    bool WebSearch,
    string? ExpiresAt,
    int? MaxOccurrences);

// Partial-update patch against the stored explicit definition. Schedule
// follows whole-schedule replacement semantics when supplied.
public sealed record TaskUpdate(
    bool PromptSupplied,
    string? Prompt,
    bool ScheduleSupplied,
    TaskSchedule? Schedule,
    bool TimezoneSupplied,
    string? Timezone,
    bool ParametersSupplied,
    TaskParameters? Parameters,
    IReadOnlySet<string> SuppliedParameters,
    bool HasLeafFields);

// Timezone + local date-time handling. User-entered expiresAt and explicit
// dates use the task timezone; internal UTC names stay outside this contract.
//
// Pinned timezone data: zones resolve from NodaTime's embedded TZDB, one
// release per pinned NodaTime package version, independent of host tzdata.
// Upgrading that package is the deliberate data upgrade (see PinnedDataVersion).
public static class TaskTimezones
{
    public const string UtcId = "UTC";
    public const int MaxExplicitDates = 10;
    public const int MaxOccurrencesLimit = int.MaxValue;

    public static string PinnedDataVersion => DateTimeZoneProviders.Tzdb.VersionId;

    private static readonly Regex LocalDateTimePattern =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00$", RegexOptions.CultureInvariant);

    public static bool TryResolve(string? timezoneId, out DateTimeZone? zone)
    {
        zone = null;
        if (string.IsNullOrEmpty(timezoneId))
            return false;
        if (timezoneId.Equals(UtcId, StringComparison.Ordinal))
        {
            zone = DateTimeZone.Utc;
            return true;
        }
        // IANA IDs only: fixed abbreviations (EST) and Windows IDs resolve
        // per host and must never silently change scheduling.
        if (!timezoneId.Contains('/'))
            return false;
        zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timezoneId);
        return zone is not null;
    }

    // Exact input format YYYY-MM-DDTHH:mm:00: no offset, Z, or fractions.
    public static bool TryParseLocalInput(string? value, DateTimeZone zone,
        out DateTime local, out string? dstCode)
    {
        local = default;
        dstCode = null;
        if (value is null || !LocalDateTimePattern.IsMatch(value))
            return false;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            return false;
        local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (IsGap(local, zone))
        {
            dstCode = TaskDefinitionErrorCodes.LocalTimeNonexistent;
            return false;
        }
        if (IsFold(local, zone))
        {
            dstCode = TaskDefinitionErrorCodes.LocalTimeAmbiguous;
            return false;
        }
        return true;
    }

    public static DateTimeOffset ConvertToUtc(DateTime localUnspecified, DateTimeZone zone) =>
        new(ToUtcStrict(localUnspecified, zone), TimeSpan.Zero);

    // Strict mapping for validated inputs; gaps/folds are programming errors.
    public static DateTime ToUtcStrict(DateTime localUnspecified, DateTimeZone zone) =>
        LocalDateTime.FromDateTime(localUnspecified).InZoneStrictly(zone).ToDateTimeUtc();

    public static DateTime ToLocal(DateTime utc, DateTimeZone zone) =>
        Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            .InZone(zone).LocalDateTime.ToDateTimeUnspecified();

    public static TimeSpan OffsetAt(DateTime utc, DateTimeZone zone) =>
        zone.GetUtcOffset(Instant.FromDateTimeUtc(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc))).ToTimeSpan();

    public static bool IsGap(DateTime localUnspecified, DateTimeZone zone)
    {
        try
        {
            LocalDateTime.FromDateTime(localUnspecified).InZoneStrictly(zone);
            return false;
        }
        catch (SkippedTimeException)
        {
            return true;
        }
        catch (AmbiguousTimeException)
        {
            return false;
        }
    }

    public static bool IsFold(DateTime localUnspecified, DateTimeZone zone)
    {
        try
        {
            LocalDateTime.FromDateTime(localUnspecified).InZoneStrictly(zone);
            return false;
        }
        catch (AmbiguousTimeException)
        {
            return true;
        }
        catch (SkippedTimeException)
        {
            return false;
        }
    }
}

// JSON parsing + structural validation for /create and /update. Field names
// and enum values are case-sensitive; unknown/duplicate keys, system-managed
// fields, wrong types, and unsupported nulls reject the whole command.
public static class TaskDefinitionParser
{
    private static readonly IReadOnlySet<string> TopLevelFields =
        new HashSet<string>(StringComparer.Ordinal) { "prompt", "schedule", "timezone", "parameters" };

    private static readonly IReadOnlySet<string> ScheduleFields =
        new HashSet<string>(StringComparer.Ordinal) { "cron", "once" };

    private static readonly IReadOnlySet<string> ParameterFields =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "memoryMode", "reasoningEffort", "webSearch", "expiresAt", "maxOccurrences",
        };

    // Identity/system-managed state: never accepted in create/update
    // settings, even when the submitted value matches storage.
    private static readonly IReadOnlySet<string> ReadOnlyFields =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "taskId", "operationId", "id", "ownerId", "owner", "chatId", "chat",
            "createdAtUtc", "createdAt", "updatedAtUtc", "updatedAt", "revision",
            "status", "waterlineUtc", "waterline", "startedOccurrences", "count",
            "scheduledUtc", "expiresAtUtc", "instanceId", "failureSummary",
            "stopReason", "stopReasonAtUtc",
        };

    public static bool TryParseCreate(string json,
        out TaskDefinition? definition, out TaskDefinitionError? error)
        => TryParseCreate(json, null, out definition, out error);

    // JSON /create may use a replied-to message from the same user as the
    // prompt when the JSON omits it. An explicit prompt field always wins.
    public static bool TryParseCreate(string json, string? promptFallback,
        out TaskDefinition? definition, out TaskDefinitionError? error)
    {
        definition = null;
        if (!TryGetRoot(json, out var root, out error))
            return false;
        using var doc = root!;
        if (!CollectFields(doc.RootElement, "", TopLevelFields, out var fields, out error))
            return false;

        if (!TryReadPrompt(fields, required: true, out var prompt, out error, promptFallback))
            return false;
        if (!TryReadSchedule(fields, required: true, out var schedule, out var zone, out error))
            return false;
        if (!TryReadParameters(fields, out var parameters, out error))
            return false;
        var timezoneExplicit = fields.ContainsKey("timezone");
        var timezone = timezoneExplicit && fields.TryGetValue("timezone", out var tzProp)
            ? tzProp.GetString() ?? string.Empty
            : TaskTimezones.UtcId;

        definition = new TaskDefinition(prompt, schedule!, timezone, parameters, timezoneExplicit);
        return true;
    }

    public static bool TryParseUpdate(string json,
        out TaskUpdate? update, out TaskDefinitionError? error)
        => TryParseUpdate(json, null, out update, out error);

    // Updates validate local dates in the stored task timezone: the zone is
    // immutable, so a patch must never resolve dates in UTC (or any other
    // zone) just because it omits "timezone".
    public static bool TryParseUpdate(string json, string? storedTimezone,
        out TaskUpdate? update, out TaskDefinitionError? error)
    {
        update = null;
        if (!TryGetRoot(json, out var root, out error))
            return false;
        using var doc = root!;
        if (!CollectFields(doc.RootElement, "", TopLevelFields, out var fields, out error))
            return false;

        var promptSupplied = fields.ContainsKey("prompt");
        string? prompt = null;
        if (promptSupplied && !TryReadPrompt(fields, required: true, out prompt, out error))
            return false;

        var scheduleSupplied = fields.ContainsKey("schedule");
        TaskSchedule? schedule = null;
        DateTimeZone? unused = null;
        if (scheduleSupplied && !TryReadSchedule(fields, required: true, storedTimezone,
            out schedule, out unused, out error))
            return false;

        var timezoneSupplied = fields.ContainsKey("timezone");
        string? timezone = null;
        if (timezoneSupplied)
        {
            var prop = fields["timezone"];
            if (prop.ValueKind != JsonValueKind.String)
                return Fail(out error, TaskDefinitionErrorCodes.TimezoneInvalid, "timezone",
                    "Timezone must be an IANA zone ID string.");
            timezone = prop.GetString() ?? string.Empty;
            if (!TaskTimezones.TryResolve(timezone, out _))
                return Fail(out error, TaskDefinitionErrorCodes.TimezoneInvalid, "timezone",
                    $"Unknown timezone '{timezone}'.");
        }

        var parametersSupplied = fields.ContainsKey("parameters");
        TaskParameters? parameters = null;
        if (parametersSupplied && !TryReadParameters(fields, storedTimezone, out parameters, out error))
            return false;

        var suppliedParameters = new HashSet<string>(StringComparer.Ordinal);
        if (parametersSupplied)
        {
            foreach (var leaf in fields["parameters"].EnumerateObject())
                suppliedParameters.Add(leaf.Name);
        }

        var leafFields = (promptSupplied ? 1 : 0) + (scheduleSupplied ? 1 : 0) +
            suppliedParameters.Count + (timezoneSupplied ? 1 : 0);
        if (leafFields == 0)
            return Fail(out error, TaskDefinitionErrorCodes.PatchEmpty, "",
                "Update patch has no leaf fields.");

        update = new TaskUpdate(promptSupplied, prompt, scheduleSupplied, schedule,
            timezoneSupplied, timezone, parametersSupplied, parameters,
            suppliedParameters, HasLeafFields: true);
        return true;
    }

    // A repeated timezone ID is a no-op assertion; a different ID is rejected.
    public static TaskDefinitionError? CheckTimezoneNoChange(string? supplied, string stored) =>
        supplied is null || supplied.Equals(stored, StringComparison.Ordinal)
            ? null
            : new TaskDefinitionError(TaskDefinitionErrorCodes.TimezoneImmutable, "timezone",
                "Timezone is immutable. Create a new task to use a different zone.");

    // No-op comparison: ignore JSON key order, cron whitespace,
    // explicit-date array order, and absent versus empty once.
    public static bool SchedulesEqual(TaskSchedule a, TaskSchedule b) =>
        NormalizeCron(a.Cron) == NormalizeCron(b.Cron) &&
        new HashSet<string>(a.Once ?? [], StringComparer.Ordinal).SetEquals(b.Once ?? []);

    public static string? NormalizeCron(string? cron)
    {
        if (cron is null)
            return null;
        return string.Join(' ', cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // effective value = explicit task override, otherwise current global default.
    public static EffectiveTaskSettings ResolveEffective(TaskDefinition definition, TaskDefaults globals) =>
        new(
            definition.Prompt ?? string.Empty,
            definition.Schedule.Cron,
            definition.Schedule.Once ?? [],
            definition.Timezone,
            definition.Parameters.MemoryMode ?? globals.MemoryMode,
            definition.Parameters.ReasoningEffort ?? globals.ReasoningEffort,
            definition.Parameters.WebSearch ?? globals.WebSearch,
            definition.Parameters.ExpiresAt,
            definition.Parameters.MaxOccurrences);

    private static bool TryGetRoot(string json, out JsonDocument? doc, out TaskDefinitionError? error)
    {
        doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleInvalid, "",
                $"Invalid JSON object: {ex.Message}");
            return false;
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            doc = null;
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleInvalid, "",
                "Command body must be a JSON object.");
            return false;
        }
        error = null;
        return true;
    }

    private static bool CollectFields(JsonElement obj, string path,
        IReadOnlySet<string> allowed, out Dictionary<string, JsonElement> fields,
        out TaskDefinitionError? error)
    {
        fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            if (ReadOnlyFields.Contains(prop.Name))
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.FieldReadOnly,
                    JoinPath(path, prop.Name), $"Field '{prop.Name}' is system-managed and read-only.");
                return false;
            }
            if (!allowed.Contains(prop.Name))
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.UnknownField,
                    JoinPath(path, prop.Name), $"Unknown field '{prop.Name}'.");
                return false;
            }
            if (!fields.TryAdd(prop.Name, prop.Value))
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.DuplicateField,
                    JoinPath(path, prop.Name), $"Duplicate field '{prop.Name}'.");
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool TryReadPrompt(Dictionary<string, JsonElement> fields, bool required,
        out string? prompt, out TaskDefinitionError? error, string? promptFallback = null)
    {
        prompt = null;
        if (!fields.TryGetValue("prompt", out var prop))
        {
            if (promptFallback is not null)
            {
                if (TextLimits.CountChars(promptFallback) < 1 ||
                    TextLimits.CountChars(promptFallback) > TextLimits.MaxPromptChars)
                {
                    error = new TaskDefinitionError(TaskDefinitionErrorCodes.PromptInvalid, "prompt",
                        $"Prompt must be 1-{TextLimits.MaxPromptChars} characters.");
                    return false;
                }
                prompt = promptFallback;
                error = null;
                return true;
            }
            if (required)
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.PromptInvalid, "prompt",
                    "Prompt is required.");
                return false;
            }
            error = null;
            return true;
        }
        if (prop.ValueKind != JsonValueKind.String)
        {
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.PromptInvalid, "prompt",
                "Prompt must be nonempty text.");
            return false;
        }
        var text = prop.GetString() ?? string.Empty;
        if (TextLimits.CountChars(text) < 1 || TextLimits.CountChars(text) > TextLimits.MaxPromptChars)
        {
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.PromptInvalid, "prompt",
                $"Prompt must be 1-{TextLimits.MaxPromptChars} characters.");
            return false;
        }
        prompt = text;
        error = null;
        return true;
    }

    private static bool TryReadSchedule(Dictionary<string, JsonElement> fields, bool required,
        out TaskSchedule? schedule, out DateTimeZone? zone, out TaskDefinitionError? error)
        => TryReadSchedule(fields, required, null, out schedule, out zone, out error);

    private static bool TryReadSchedule(Dictionary<string, JsonElement> fields, bool required,
        string? storedTimezone,
        out TaskSchedule? schedule, out DateTimeZone? zone, out TaskDefinitionError? error)
    {
        schedule = null;
        zone = null;
        if (!fields.TryGetValue("schedule", out var prop))
        {
            if (required)
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleInvalid, "schedule",
                    "At least one schedule source (cron or once) is required.");
                return false;
            }
            error = null;
            return true;
        }
        if (prop.ValueKind != JsonValueKind.Object)
        {
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleInvalid, "schedule",
                "Schedule must be an object with cron and/or once.");
            return false;
        }
        if (!CollectFields(prop, "schedule", ScheduleFields, out var sub, out error))
            return false;

        string? cron = null;
        if (sub.TryGetValue("cron", out var cronProp))
        {
            if (cronProp.ValueKind != JsonValueKind.String)
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.CronInvalid, "schedule.cron",
                    "Cron must be a six-field expression string.");
                return false;
            }
            cron = cronProp.GetString() ?? string.Empty;
            if (!NcrontabSchedule.TryParse(cron, out var parsed) || parsed is null ||
                !parsed.RequiresZeroSecondsOnly)
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.CronInvalid, "schedule.cron",
                    $"Invalid schedule '{cron}'. Use six NCRONTAB fields with seconds 0.");
                return false;
            }
        }

        // Resolve the task timezone now: explicit-date DST policy needs it.
        // Updates always use the stored zone; the patch zone (when present)
        // is only a no-op assertion checked later.
        var tzId = storedTimezone ?? (fields.TryGetValue("timezone", out var tzProp)
            ? tzProp.ValueKind == JsonValueKind.String ? tzProp.GetString() ?? string.Empty : null
            : TaskTimezones.UtcId);
        if (tzId is null || !TaskTimezones.TryResolve(tzId, out zone) || zone is null)
        {
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.TimezoneInvalid, "timezone",
                $"Unknown timezone '{tzId}'. Use an IANA zone ID or UTC.");
            return false;
        }

        var once = new List<string>();
        if (sub.TryGetValue("once", out var onceProp))
        {
            if (onceProp.ValueKind != JsonValueKind.Array)
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid, "schedule.once",
                    "Explicit dates must be an array of local date-times.");
                return false;
            }
            var items = onceProp.EnumerateArray().ToArray();
            if (items.Length > TaskTimezones.MaxExplicitDates)
            {
                error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid, "schedule.once",
                    $"At most {TaskTimezones.MaxExplicitDates} explicit dates.");
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid, "schedule.once",
                        "Explicit dates must use YYYY-MM-DDTHH:mm:00 without offset.");
                    return false;
                }
                var text = item.GetString() ?? string.Empty;
                if (!TaskTimezones.TryParseLocalInput(text, zone, out _, out var dstCode))
                {
                    error = dstCode == TaskDefinitionErrorCodes.LocalTimeNonexistent
                        ? new TaskDefinitionError(dstCode, "schedule.once",
                            $"Local time '{text}' does not exist in '{tzId}'. Choose another time.")
                        : dstCode == TaskDefinitionErrorCodes.LocalTimeAmbiguous
                            ? new TaskDefinitionError(dstCode, "schedule.once",
                                $"Local time '{text}' is ambiguous in '{tzId}'. Choose an unambiguous time.")
                            : new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid, "schedule.once",
                                $"Invalid date '{text}'. Use YYYY-MM-DDTHH:mm:00 without offset.");
                    return false;
                }
                if (!seen.Add(text))
                {
                    error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid, "schedule.once",
                        $"Duplicate explicit date '{text}'.");
                    return false;
                }
                once.Add(text);
            }
        }

        if (cron is null && once.Count == 0)
        {
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleInvalid, "schedule",
                "At least one schedule source (cron or once) is required.");
            return false;
        }

        schedule = new TaskSchedule(cron, once, sub.ContainsKey("once"));
        error = null;
        return true;
    }

    private static bool TryReadParameters(Dictionary<string, JsonElement> fields,
        out TaskParameters parameters, out TaskDefinitionError? error)
        => TryReadParameters(fields, null, out parameters, out error);

    private static bool TryReadParameters(Dictionary<string, JsonElement> fields,
        string? storedTimezone,
        out TaskParameters parameters, out TaskDefinitionError? error)
    {
        parameters = TaskParameters.Empty;
        if (!fields.TryGetValue("parameters", out var prop))
        {
            error = null;
            return true;
        }
        if (prop.ValueKind != JsonValueKind.Object)
        {
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.ParametersInvalid, "parameters",
                "Parameters must be an object.");
            return false;
        }
        if (!CollectFields(prop, "parameters", ParameterFields, out var sub, out error))
            return false;

        string? memoryMode = null;
        string? reasoningEffort = null;
        bool? webSearch = null;
        string? expiresAt = null;
        int? maxOccurrences = null;

        if (sub.TryGetValue("memoryMode", out var mm) && !mm.IsNull(out memoryMode, out error, "parameters.memoryMode"))
            return false;
        if (memoryMode is not null &&
            !memoryMode.Equals(TaskMemoryModes.IncludePreviousMessage, StringComparison.Ordinal) &&
            !memoryMode.Equals(TaskMemoryModes.None, StringComparison.Ordinal))
            return Fail(out error, TaskDefinitionErrorCodes.ParametersInvalid, "parameters.memoryMode",
                "memoryMode must be IncludePreviousMessage or None.");

        if (sub.TryGetValue("reasoningEffort", out var re) && !re.IsNull(out reasoningEffort, out error, "parameters.reasoningEffort"))
            return false;
        if (reasoningEffort is not null && !TaskReasoningEfforts.All.Contains(reasoningEffort))
            return Fail(out error, TaskDefinitionErrorCodes.ParametersInvalid, "parameters.reasoningEffort",
                "reasoningEffort must be low, med, high, xhigh, or max.");

        if (sub.TryGetValue("webSearch", out var ws))
        {
            if (ws.ValueKind == JsonValueKind.Null)
                webSearch = null;
            else if (ws.ValueKind is JsonValueKind.True or JsonValueKind.False)
                webSearch = ws.GetBoolean();
            else
                return Fail(out error, TaskDefinitionErrorCodes.ParametersInvalid, "parameters.webSearch",
                    "webSearch must be a boolean.");
        }

        if (sub.TryGetValue("expiresAt", out var ea))
        {
            if (ea.ValueKind == JsonValueKind.Null)
                expiresAt = null;
            else if (ea.ValueKind != JsonValueKind.String)
                return Fail(out error, TaskDefinitionErrorCodes.ExpirationInvalid, "parameters.expiresAt",
                    "Expiration must use YYYY-MM-DDTHH:mm:00 in the task timezone.");
            else
            {
                // Updates always use the stored zone; creates resolve from
                // the top-level field for parameter-only bodies.
                var tzId = storedTimezone ?? (fields.TryGetValue("timezone", out var tzProp) &&
                    tzProp.ValueKind == JsonValueKind.String
                    ? tzProp.GetString() ?? TaskTimezones.UtcId
                    : TaskTimezones.UtcId);
                if (!TaskTimezones.TryResolve(tzId, out var zone) || zone is null)
                    return Fail(out error, TaskDefinitionErrorCodes.TimezoneInvalid, "timezone",
                        $"Unknown timezone '{tzId}'.");
                var text = ea.GetString() ?? string.Empty;
                if (!TaskTimezones.TryParseLocalInput(text, zone, out _, out var dstCode))
                    return Fail(out error,
                        dstCode ?? TaskDefinitionErrorCodes.ExpirationInvalid,
                        "parameters.expiresAt",
                        $"Invalid expiration '{text}'. Use YYYY-MM-DDTHH:mm:00 in the task timezone.");
                expiresAt = text;
            }
        }

        if (sub.TryGetValue("maxOccurrences", out var mo))
        {
            if (mo.ValueKind == JsonValueKind.Null)
                maxOccurrences = null;
            else if (mo.ValueKind != JsonValueKind.Number ||
                !mo.TryGetInt32(out var count) || count < 1)
                return Fail(out error, TaskDefinitionErrorCodes.OccurrenceLimitInvalid,
                    "parameters.maxOccurrences",
                    "maxOccurrences must be an integer from 1 through 2147483647.");
            else
                maxOccurrences = count;
        }

        parameters = new TaskParameters(memoryMode, reasoningEffort, webSearch, expiresAt, maxOccurrences);
        error = null;
        return true;
    }

    private static bool Fail(out TaskDefinitionError? error, string code, string path, string message)
    {
        error = new TaskDefinitionError(code, path, message);
        return false;
    }

    private static string JoinPath(string prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;

    private static bool IsNull(this JsonElement element, out string? value,
        out TaskDefinitionError? error, string path)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            error = null;
            return true;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            value = null;
            error = new TaskDefinitionError(TaskDefinitionErrorCodes.ParametersInvalid, path,
                "Value must be a string or null.");
            return false;
        }
        value = element.GetString();
        error = null;
        return true;
    }
}
