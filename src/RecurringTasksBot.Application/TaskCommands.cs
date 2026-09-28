// Phase 5 command envelopes (docs/Spec.Phase5.md section 1): JSON-only
// /create, explicit/effective /get, partial /update, paged /list. Task ID
// references accept a full ID or a unique prefix of at least six characters.
namespace RecurringTasksBot.Application;

public enum TaskIdResolution
{
    Found,
    NotFound,
    Ambiguous,
    TooShort,
}

public sealed record ParsedGetCommand(string TaskRef, string Mode)
{
    public const string Explicit = "explicit";
    public const string Effective = "effective";
}

public sealed record ParsedUpdateCommand(string TaskRef, string JsonBody);

public sealed record ParsedListCommand(int Page, bool Compact);

public static class TaskCommandParser
{
    public const int MinIdPrefixLength = 6;
    public const int ListPageSize = 10;

    // Remainder after the verb. Empty or non-JSON remainder is legacy
    // positional input: usage help, creates nothing.
    public static bool TrySplitJsonBody(string text, out string? jsonBody)
    {
        var firstSpace = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var rest = (firstSpace < 0 ? string.Empty : text[(firstSpace + 1)..]).Trim();
        if (rest.StartsWith('{'))
        {
            jsonBody = rest;
            return true;
        }

        jsonBody = null;
        return false;
    }

    public static bool TryParseGetArgs(string text, out ParsedGetCommand? command, out string? error)
    {
        command = null;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 3)
        {
            error = "Usage: /get <task-id> [explicit|effective].";
            return false;
        }

        var mode = parts.Length == 3 ? parts[2].ToLowerInvariant() : ParsedGetCommand.Explicit;
        if (mode is not (ParsedGetCommand.Explicit or ParsedGetCommand.Effective))
        {
            error = $"Unknown get mode '{parts[2]}'. Use explicit or effective.";
            return false;
        }

        command = new ParsedGetCommand(parts[1], mode);
        error = null;
        return true;
    }

    public static bool TrySplitUpdateArgs(string text, out ParsedUpdateCommand? command, out string? error)
    {
        command = null;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            error = "Usage: /update <task-id> <JSON patch object>.";
            return false;
        }

        var afterRef = text.IndexOf(parts[1], StringComparison.Ordinal) + parts[1].Length;
        var jsonBody = text[afterRef..].Trim();
        if (!jsonBody.StartsWith('{'))
        {
            error = "Usage: /update <task-id> <JSON patch object>.";
            return false;
        }

        command = new ParsedUpdateCommand(parts[1], jsonBody);
        error = null;
        return true;
    }

    public static bool TryParseListArgs(string text, out ParsedListCommand? command, out string? error)
    {
        command = null;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 3)
        {
            error = "Usage: /list [page] [compact].";
            return false;
        }

        var page = 1;
        var compact = false;
        foreach (var arg in parts.Skip(1))
        {
            if (arg.Equals("compact", StringComparison.OrdinalIgnoreCase))
            {
                compact = true;
            }
            else if (int.TryParse(arg, out var parsed) && parsed >= 1)
            {
                page = parsed;
            }
            else
            {
                error = "Usage: /list [page] [compact]. Page must be a positive integer.";
                return false;
            }
        }

        command = new ParsedListCommand(page, compact);
        error = null;
        return true;
    }

    // Prefixes must be unique across the owner's nondeleted tasks, not just
    // the displayed page; ambiguous prefixes are rejected, never guessed.
    public static TaskIdResolution ResolveTaskRef(
        IReadOnlyList<TaskRecord> ownedTasks, string taskRef, out TaskRecord? task)
    {
        task = null;
        var candidates = ownedTasks
            .Where(t => t.Status != TaskState.Deleted)
            .ToList();
        var exact = candidates.FirstOrDefault(t =>
            t.TaskId.Equals(taskRef, StringComparison.Ordinal));
        if (exact is not null)
        {
            task = exact;
            return TaskIdResolution.Found;
        }

        if (taskRef.Length < MinIdPrefixLength)
            return TaskIdResolution.TooShort;
        var prefixed = candidates
            .Where(t => t.TaskId.StartsWith(taskRef, StringComparison.Ordinal))
            .ToList();
        if (prefixed.Count == 1)
        {
            task = prefixed[0];
            return TaskIdResolution.Found;
        }

        return prefixed.Count == 0 ? TaskIdResolution.NotFound : TaskIdResolution.Ambiguous;
    }
}
