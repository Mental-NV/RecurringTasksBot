// Validation for `/create <six-field NCRONTAB> <prompt>`:
// UTC, six fields, seconds fixed to 0, future occurrence required,
// non-empty prompts up to Telegram's 32,768-character limit with spaces and
// line breaks preserved. Character counts use Unicode scalar values, never
// UTF-8 bytes or UTF-16 code units; oversized prompts are rejected, never
// silently truncated.
namespace RecurringTasksBot.Core;

public sealed record CreateCommand(string CronExpression, string Text, NcrontabSchedule Schedule);

public static class CreateCommandParser
{
    public const int MinTextLength = 1;
    public const int MaxTextLength = TextLimits.MaxPromptChars;

    public static bool TryParse(string? commandText, DateTime nowUtc,
        out CreateCommand? command, out string? error)
    {
        command = null;
        error = null;

        if (string.IsNullOrWhiteSpace(commandText))
        {
            error = "Empty command. Send /create <six-field schedule> <text>.";
            return false;
        }

        var trimmed = commandText.Trim();
        var firstSpace = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        if (firstSpace < 0)
        {
            error = "Usage: /create <six-field schedule> <text>.";
            return false;
        }

        var verb = trimmed[..firstSpace];
        if (!verb.Equals("/create", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unknown command '{verb}'. Send /create <six-field schedule> <text>.";
            return false;
        }

        var rest = trimmed[(firstSpace + 1)..].TrimStart(' ', '\t');
        var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 7)
        {
            error = "Usage: /create <six-field schedule> <text>. Need 6 schedule fields plus message text.";
            return false;
        }

        var cron = string.Join(' ', tokens.Take(6));

        // Preserve spaces and line breaks after the schedule fields.
        var textStart = rest.IndexOf(tokens[6], StringComparison.Ordinal);
        var text = rest[textStart..].TrimEnd();
        if (TextLimits.CountChars(text) < MinTextLength)
        {
            error = "Prompt text must not be empty.";
            return false;
        }

        if (TextLimits.CountChars(text) > MaxTextLength)
        {
            error = $"Prompt text must be at most {MaxTextLength} characters.";
            return false;
        }

        if (!NcrontabSchedule.TryParse(cron, out var schedule) || schedule is null)
        {
            error = $"Invalid schedule '{cron}'. Use six NCRONTAB fields (seconds minutes hours day month weekday), UTC.";
            return false;
        }

        if (!schedule.RequiresZeroSecondsOnly)
        {
            error = "Schedule seconds must be 0.";
            return false;
        }

        if (schedule.GetNextOccurrence(nowUtc) is null)
        {
            error = "Schedule has no future occurrence.";
            return false;
        }

        command = new CreateCommand(cron, text, schedule);
        return true;
    }
}
