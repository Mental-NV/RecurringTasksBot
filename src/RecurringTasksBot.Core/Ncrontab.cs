// Six-field NCRONTAB (seconds minutes hours day-of-month month day-of-week),
// interpreted in UTC. Seconds must be 0 (enforced by CreateCommand, not here).
namespace RecurringTasksBot.Core;

public sealed class NcrontabParseException(string message) : Exception(message);

public sealed class NcrontabSchedule
{
    private readonly int[] _seconds;
    private readonly int[] _minutes;
    private readonly int[] _hours;
    private readonly int[] _daysOfMonth;
    private readonly int[] _months;
    private readonly int[] _daysOfWeek;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private NcrontabSchedule(int[] seconds, int[] minutes, int[] hours, int[] daysOfMonth,
        int[] months, int[] daysOfWeek, bool domRestricted, bool dowRestricted)
    {
        _seconds = seconds;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
    }

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static NcrontabSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new NcrontabParseException("Schedule expression is empty.");

        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 6)
            throw new NcrontabParseException(
                $"Schedule must have exactly 6 fields, found {fields.Length}.");

        try
        {
            return new NcrontabSchedule(
                ParseField(fields[0], 0, 59, null, normaliseSunday: false),
                ParseField(fields[1], 0, 59, null, normaliseSunday: false),
                ParseField(fields[2], 0, 23, null, normaliseSunday: false),
                ParseField(fields[3], 1, 31, null, normaliseSunday: false),
                ParseField(fields[4], 1, 12, MonthNames, normaliseSunday: false),
                ParseField(fields[5], 0, 7, DayNames, normaliseSunday: true),
                IsRestricted(fields[3]),
                IsRestricted(fields[5]));
        }
        catch (NcrontabParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NcrontabParseException($"Invalid schedule expression: {ex.Message}");
        }
    }

    public static bool TryParse(string expression, out NcrontabSchedule? schedule)
    {
        try
        {
            schedule = Parse(expression);
            return true;
        }
        catch (NcrontabParseException)
        {
            schedule = null;
            return false;
        }
    }

    public bool RequiresZeroSecondsOnly => _seconds.Length == 1 && _seconds[0] == 0;

    private static bool IsRestricted(string field) =>
        !(field.Trim() == "*" || field.Trim().Equals("*", StringComparison.Ordinal));

    private static int[] ParseField(string field, int min, int max, string[]? names, bool normaliseSunday)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new NcrontabParseException("Schedule field is empty.");

        var values = new SortedSet<int>();
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
                throw new NcrontabParseException($"Empty list element in '{field}'.");
            ParsePart(part, min, max, names, values);
        }

        if (values.Count == 0)
            throw new NcrontabParseException($"No values in '{field}'.");

        var result = values.ToArray();
        if (normaliseSunday && result.Contains(7))
        {
            var normalised = new SortedSet<int>(result.Select(v => v == 7 ? 0 : v));
            result = normalised.ToArray();
        }

        if (result.Any(v => v < min || v > max))
            throw new NcrontabParseException($"Value out of range [{min}-{max}] in '{field}'.");
        return result;
    }

    private static void ParsePart(string part, int min, int max, string[]? names, SortedSet<int> values)
    {
        var stepSplit = part.Split('/');
        if (stepSplit.Length > 2)
            throw new NcrontabParseException($"Too many '/' in '{part}'.");
        if (stepSplit.Length == 2 && !int.TryParse(stepSplit[1], out var step))
            throw new NcrontabParseException($"Invalid step in '{part}'.");
        var stepValue = stepSplit.Length == 2 ? int.Parse(stepSplit[1]) : 1;
        if (stepValue <= 0 || stepValue > max - min + 1)
            throw new NcrontabParseException($"Step out of range in '{part}'.");

        var range = stepSplit[0];
        int from, to;
        if (range == "*" || range.Length == 0)
        {
            if (range.Length == 0)
                throw new NcrontabParseException($"Empty range in '{part}'.");
            from = min;
            to = max;
        }
        else if (range.Contains('-'))
        {
            var bounds = range.Split('-');
            if (bounds.Length != 2)
                throw new NcrontabParseException($"Invalid range in '{part}'.");
            from = ParseValue(bounds[0], names);
            to = ParseValue(bounds[1], names);
            if (from > to)
                throw new NcrontabParseException($"Reversed range in '{part}'.");
        }
        else
        {
            from = to = ParseValue(range, names);
        }

        if (from < min || to > max)
            throw new NcrontabParseException($"Value out of range [{min}-{max}] in '{part}'.");
        for (var v = from; v <= to; v += stepValue)
            values.Add(v);
    }

    private static int ParseValue(string token, string[]? names)
    {
        if (int.TryParse(token, out var n))
            return n;
        if (names != null)
        {
            var idx = Array.FindIndex(names,
                m => m.Equals(token.Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                return names == MonthNames ? idx + 1 : idx;
        }

        throw new NcrontabParseException($"Invalid value '{token}'.");
    }

    private bool MatchesDay(DateTime day)
    {
        if (!_months.Contains(day.Month))
            return false;

        var dom = _daysOfMonth.Contains(day.Day);
        var dow = _daysOfWeek.Contains((int)day.DayOfWeek);
        if (_domRestricted && _dowRestricted)
            return dom || dow;
        if (_domRestricted)
            return dom;
        if (_dowRestricted)
            return dow;
        return true;
    }

    // Strictly after `afterUtc`: never returns `afterUtc` itself.
    // Searches up to MaxSearchDays ahead; returns null when nothing occurs.
    public const int MaxSearchDays = 366 * 5 + 2;

    public DateTime? GetNextOccurrence(DateTime afterUtc)
    {
        var after = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var startDay = after.Date;

        for (var dayOffset = 0; dayOffset <= MaxSearchDays; dayOffset++)
        {
            var day = startDay.AddDays(dayOffset);
            if (!MatchesDay(day))
                continue;

            foreach (var hour in _hours)
            {
                foreach (var minute in _minutes)
                {
                    foreach (var second in _seconds)
                    {
                        DateTime candidate;
                        try
                        {
                            candidate = new DateTime(day.Year, day.Month, day.Day,
                                hour, minute, second, DateTimeKind.Utc);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            continue;
                        }

                        if (candidate > after)
                            return candidate;
                    }
                }
            }
        }

        return null;
    }
}
