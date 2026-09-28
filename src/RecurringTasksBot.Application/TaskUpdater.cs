// Phase 5 update semantics (docs/Spec.Phase5.md sections 1, 3, 4): patches
// apply against the stored explicit definition, never the effective view.
// Omission preserves; concrete values pin overrides; null restores
// inheritance. Unchanged resubmissions are field-level no-ops.
using NodaTime;

namespace RecurringTasksBot.Application;

public sealed record TaskUpdateResult(
    TaskDefinition Definition,
    IReadOnlyList<string> Changed,
    bool PromptChanged,
    bool ScheduleChanged,
    bool LimitsChanged)
{
    public bool IsUnchanged => !PromptChanged && !ScheduleChanged && Changed.Count == 0;
}

public static class TaskUpdater
{
    public static TaskDefinitionError? ApplyUpdate(
        TaskDefinition stored, TaskUpdate patch, out TaskUpdateResult? result)
    {
        result = null;
        if (patch.TimezoneSupplied)
        {
            var timezoneError = TaskDefinitionParser.CheckTimezoneNoChange(patch.Timezone, stored.Timezone);
            if (timezoneError is not null)
                return timezoneError;
        }

        var changed = new List<string>();
        var prompt = stored.Prompt;
        var promptChanged = false;
        if (patch.PromptSupplied && !Equals(patch.Prompt, stored.Prompt))
        {
            prompt = patch.Prompt;
            promptChanged = true;
            changed.Add("prompt updated");
        }

        // Unchanged schedules keep the saved representation (cron text and
        // date order); only a real replacement swaps it in.
        var schedule = stored.Schedule;
        var scheduleChanged = false;
        if (patch.ScheduleSupplied && patch.Schedule is not null &&
            !TaskDefinitionParser.SchedulesEqual(stored.Schedule, patch.Schedule))
        {
            schedule = patch.Schedule;
            scheduleChanged = true;
            changed.Add(DescribeScheduleChange(stored.Schedule, patch.Schedule));
        }

        var parameters = stored.Parameters;
        if (patch.ParametersSupplied && patch.Parameters is not null)
        {
            parameters = MergeParameters(stored.Parameters, patch.Parameters,
                patch.SuppliedParameters, changed);
        }

        var limitsChanged =
            !Equals(parameters.ExpiresAt, stored.Parameters.ExpiresAt) ||
            !Equals(parameters.MaxOccurrences, stored.Parameters.MaxOccurrences);

        result = new TaskUpdateResult(
            stored with { Prompt = prompt, Schedule = schedule, Parameters = parameters },
            changed, promptChanged, scheduleChanged, limitsChanged);
        return null;
    }

    private static TaskParameters MergeParameters(
        TaskParameters stored, TaskParameters patch, IReadOnlySet<string> supplied,
        List<string> changed)
    {
        var memoryMode = MergeOverride(stored.MemoryMode, patch.MemoryMode,
            supplied.Contains("memoryMode"), "memoryMode", changed);
        var reasoningEffort = MergeOverride(stored.ReasoningEffort, patch.ReasoningEffort,
            supplied.Contains("reasoningEffort"), "reasoningEffort", changed);
        var webSearch = MergeOverride(stored.WebSearch, patch.WebSearch,
            supplied.Contains("webSearch"), "webSearch", changed);
        var expiresAt = MergeOverride(stored.ExpiresAt, patch.ExpiresAt,
            supplied.Contains("expiresAt"), "expiresAt", changed);
        var maxOccurrences = MergeOverride(stored.MaxOccurrences, patch.MaxOccurrences,
            supplied.Contains("maxOccurrences"), "maxOccurrences", changed);
        return new TaskParameters(memoryMode, reasoningEffort, webSearch, expiresAt, maxOccurrences);
    }

    private static T MergeOverride<T>(T stored, T patch, bool supplied, string name, List<string> changed)
    {
        if (!supplied)
            return stored;
        if (!Equals(patch, stored))
            changed.Add(patch is null ? $"{name} reset to inherited default" : $"{name} set to {patch}");
        return patch;
    }

    private static string DescribeScheduleChange(TaskSchedule oldSchedule, TaskSchedule next)
    {
        if (oldSchedule.Cron != next.Cron)
            return next.Cron is null ? "recurrence removed" : $"recurrence set to {next.Cron}";
        return "explicit dates replaced";
    }
}

// Commit-time validation: future-date checks apply to creation, changed
// schedule/deadline fields, and reactivation. Retained past dates or an
// expired unchanged deadline never block an unrelated edit.
public static class TaskCommitValidator
{
    public static TaskDefinitionError? ValidateForCreate(
        TaskDefinition definition, DateTimeZone zone, DateTime nowUtc)
    {
        var scheduleError = ValidateScheduleSources(definition, zone, nowUtc,
            validateCronHorizon: true, validateExplicitFuture: true);
        if (scheduleError is not null)
            return scheduleError;
        var expiresAtUtc = ResolveExpiresAtUtc(definition.Parameters.ExpiresAt, zone);
        if (expiresAtUtc is not null)
        {
            if (expiresAtUtc.Value <= nowUtc)
                return new TaskDefinitionError(TaskDefinitionErrorCodes.ExpirationInvalid,
                    "parameters.expiresAt", "Expiration must be strictly future at commit.");
            foreach (var explicitUtc in TaskScheduleResolver.ResolveExplicitUtc(definition.Schedule.Once, zone))
            {
                if (explicitUtc >= expiresAtUtc.Value)
                    return new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid,
                        "schedule.once", "Explicit dates must precede the expiration deadline.");
            }
        }

        return RequireEligibleFutureWork(definition, zone, nowUtc, expiresAtUtc, maxStarted: null, started: 0);
    }

    public static TaskDefinitionError? ValidateForUpdate(
        TaskDefinition merged, DateTimeZone zone, DateTime nowUtc,
        bool scheduleChanged, bool expirationChanged, DateTime? storedExpiresAtUtc)
    {
        if (scheduleChanged)
        {
            var scheduleError = ValidateScheduleSources(merged, zone, nowUtc,
                validateCronHorizon: true, validateExplicitFuture: true);
            if (scheduleError is not null)
                return scheduleError;
        }

        var expiresAtUtc = storedExpiresAtUtc;
        if (expirationChanged)
        {
            expiresAtUtc = ResolveExpiresAtUtc(merged.Parameters.ExpiresAt, zone);
            if (expiresAtUtc is not null && expiresAtUtc.Value <= nowUtc)
                return new TaskDefinitionError(TaskDefinitionErrorCodes.ExpirationInvalid,
                    "parameters.expiresAt", "Expiration must be strictly future at commit.");
        }

        if (scheduleChanged && expiresAtUtc is not null)
        {
            foreach (var explicitUtc in TaskScheduleResolver.ResolveExplicitUtc(merged.Schedule.Once, zone))
            {
                if (explicitUtc >= expiresAtUtc.Value)
                    return new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid,
                        "schedule.once", "Explicit dates must precede the expiration deadline.");
            }
        }

        return null;
    }

    // Reactivation gate: a changed schedule or relaxed limits on a completed
    // task must yield eligible future work, otherwise reject the command.
    public static TaskDefinitionError? RequireReactivationWork(
        TaskDefinition merged, DateTimeZone zone, DateTime commitUtc,
        DateTime? expiresAtUtc, int? maxOccurrences, int startedOccurrences)
    {
        if (maxOccurrences is not null && startedOccurrences >= maxOccurrences.Value)
            return new TaskDefinitionError(TaskDefinitionErrorCodes.OccurrenceLimitInvalid,
                "parameters.maxOccurrences", "Occurrence limit already reached; raise it to resume.");
        return RequireEligibleFutureWork(merged, zone, commitUtc, expiresAtUtc, maxOccurrences, startedOccurrences);
    }

    public static DateTime? ResolveExpiresAtUtc(string? expiresAtLocal, DateTimeZone zone)
    {
        if (expiresAtLocal is null)
            return null;
        if (!TaskTimezones.TryParseLocalInput(expiresAtLocal, zone, out var local, out _))
            return null;
        return TaskTimezones.ConvertToUtc(local, zone).UtcDateTime;
    }

    private static TaskDefinitionError? ValidateScheduleSources(
        TaskDefinition definition, DateTimeZone zone, DateTime nowUtc,
        bool validateCronHorizon, bool validateExplicitFuture)
    {
        if (validateCronHorizon && definition.Schedule.Cron is not null &&
            !TaskScheduleResolver.HasFutureCronOccurrence(definition.Schedule.Cron, zone, nowUtc))
            return new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleNoFutureOccurrence,
                "schedule.cron", "Recurrence has no future occurrence within the search horizon.");
        if (validateExplicitFuture)
        {
            foreach (var explicitUtc in TaskScheduleResolver.ResolveExplicitUtc(definition.Schedule.Once, zone))
            {
                if (explicitUtc <= nowUtc)
                    return new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleDateInvalid,
                        "schedule.once", "New explicit dates must be strictly future at commit.");
            }
        }

        return null;
    }

    private static TaskDefinitionError? RequireEligibleFutureWork(
        TaskDefinition definition, DateTimeZone zone, DateTime nowUtc,
        DateTime? expiresAtUtc, int? maxStarted, int started)
    {
        if (maxStarted is not null && started >= maxStarted.Value)
            return new TaskDefinitionError(TaskDefinitionErrorCodes.OccurrenceLimitInvalid,
                "parameters.maxOccurrences", "Occurrence limit already reached.");
        var next = TaskScheduleResolver.GetNextMergedUtc(
            definition.Schedule.Cron, definition.Schedule.Once, zone, nowUtc);
        if (next is null || (expiresAtUtc is not null && next.Value >= expiresAtUtc.Value))
            return new TaskDefinitionError(TaskDefinitionErrorCodes.ScheduleNoFutureOccurrence,
                "schedule", "No eligible future occurrence remains.");
        return null;
    }
}
