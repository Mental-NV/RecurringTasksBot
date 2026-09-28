// Phase 5 occurrence resolver (docs/Spec.Phase5.md section 2): one
// authoritative local calendar rule, one resolver, UTC execution records.
// Conversion back to local time is display formatting, never scheduling input.
using NodaTime;
using NodaTime.TimeZones;

namespace RecurringTasksBot.Application;

public static class TaskScheduleResolver
{
    // Local calendar days searched ahead for a future cron occurrence.
    public const int MaxSearchDays = 1832;

    // Next cron instant strictly after afterUtc, or null past the horizon.
    // DST gap: skip the occurrence. DST fold: run once at the earlier UTC
    // instant. Absent month days (e.g. Feb 30) are skipped, never clamped.
    public static DateTime? GetNextCronUtc(
        NcrontabSchedule cron, DateTimeZone zone, DateTime afterUtc)
    {
        var boundary = EnsureUtc(afterUtc);
        var startDay = TaskTimezones.ToLocal(boundary, zone).Date;
        for (var dayOffset = 0; dayOffset <= MaxSearchDays; dayOffset++)
        {
            var day = startDay.AddDays(dayOffset);
            if (!cron.MatchesLocalDay(day.Month, day.Day, day.DayOfWeek))
                continue;
            foreach (var candidate in DayCandidatesUtc(cron, zone, day))
            {
                if (candidate > boundary)
                    return candidate;
            }
        }

        return null;
    }

    // Greatest cron instant in (waterlineUtc, nowUtc], or null when none is due.
    public static DateTime? GetLatestDueCronUtc(
        NcrontabSchedule cron, DateTimeZone zone, DateTime nowUtc, DateTime waterlineUtc)
    {
        var now = EnsureUtc(nowUtc);
        var waterline = EnsureUtc(waterlineUtc);
        if (now <= waterline)
            return null;
        var startDay = TaskTimezones.ToLocal(waterline, zone).Date;
        var endDay = TaskTimezones.ToLocal(now, zone).Date;
        DateTime? latest = null;
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            if (!cron.MatchesLocalDay(day.Month, day.Day, day.DayOfWeek))
                continue;
            foreach (var candidate in DayCandidatesUtc(cron, zone, day))
            {
                if (candidate > waterline && candidate <= now &&
                    (latest is null || candidate > latest.Value))
                    latest = candidate;
            }
        }

        return latest;
    }

    // Resolved explicit UTC dates, ordered and unique. Inputs were validated
    // at command time (unambiguous, existing local times); anything else is
    // a programming error, not user input.
    public static IReadOnlyList<DateTime> ResolveExplicitUtc(
        IReadOnlyList<string> onceLocal, DateTimeZone zone)
    {
        var resolved = new SortedSet<DateTime>();
        foreach (var text in onceLocal ?? [])
        {
            if (!TaskTimezones.TryParseLocalInput(text, zone, out var local, out _))
                throw new InvalidOperationException($"Stored explicit date '{text}' is not usable.");
            resolved.Add(TaskTimezones.ConvertToUtc(local, zone).UtcDateTime);
        }

        return resolved.ToArray();
    }

    // Union of cron occurrences and explicit UTC dates; coincident instants
    // produce one occurrence. Next merged instant strictly after afterUtc.
    public static DateTime? GetNextMergedUtc(
        string? cronExpression, IReadOnlyList<string> onceLocal, DateTimeZone zone, DateTime afterUtc)
    {
        var boundary = EnsureUtc(afterUtc);
        DateTime? next = null;
        if (cronExpression is not null)
            next = GetNextCronUtc(NcrontabSchedule.Parse(cronExpression), zone, boundary);
        foreach (var explicitUtc in ResolveExplicitUtc(onceLocal, zone))
        {
            if (explicitUtc > boundary && (next is null || explicitUtc < next.Value))
                next = explicitUtc;
        }

        return next;
    }

    // Latest merged instant in (waterlineUtc, nowUtc].
    public static DateTime? GetLatestDueMergedUtc(
        string? cronExpression, IReadOnlyList<string> onceLocal, DateTimeZone zone,
        DateTime nowUtc, DateTime waterlineUtc)
    {
        var now = EnsureUtc(nowUtc);
        var waterline = EnsureUtc(waterlineUtc);
        DateTime? latest = cronExpression is not null
            ? GetLatestDueCronUtc(NcrontabSchedule.Parse(cronExpression), zone, now, waterline)
            : null;
        foreach (var explicitUtc in ResolveExplicitUtc(onceLocal, zone))
        {
            if (explicitUtc > waterline && explicitUtc <= now &&
                (latest is null || explicitUtc > latest.Value))
                latest = explicitUtc;
        }

        return latest;
    }

    // Creation/replacement/reactivation gate: a cron rule must have a future
    // occurrence within the search horizon even when explicit dates exist.
    public static bool HasFutureCronOccurrence(
        string cronExpression, DateTimeZone zone, DateTime nowUtc)
    {
        if (!NcrontabSchedule.TryParse(cronExpression, out var cron) || cron is null ||
            !cron.RequiresZeroSecondsOnly)
            return false;
        return GetNextCronUtc(cron, zone, nowUtc) is not null;
    }

    private static IEnumerable<DateTime> DayCandidatesUtc(
        NcrontabSchedule cron, DateTimeZone zone, DateTime localDay)
    {
        foreach (var hour in cron.HourValues)
        {
            foreach (var minute in cron.MinuteValues)
            {
                foreach (var second in cron.SecondValues)
                {
                    LocalDateTime local;
                    try
                    {
                        local = new LocalDateTime(localDay.Year, localDay.Month, localDay.Day,
                            hour, minute, second);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        continue;
                    }

                    DateTime candidate;
                    try
                    {
                        candidate = local.InZoneStrictly(zone).ToDateTimeUtc();
                    }
                    catch (SkippedTimeException)
                    {
                        // DST gap: skip the occurrence.
                        continue;
                    }
                    catch (AmbiguousTimeException)
                    {
                        // DST fold: run once at the earlier UTC instant.
                        candidate = zone.ResolveLocal(local, Resolvers.CreateMappingResolver(
                                Resolvers.ReturnEarlier, Resolvers.ThrowWhenSkipped))
                            .ToDateTimeUtc();
                    }

                    yield return candidate;
                }
            }
        }
    }

    private static DateTime EnsureUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
