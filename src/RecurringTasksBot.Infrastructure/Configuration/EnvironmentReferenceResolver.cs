// Whole-value environment references for profile credentials: a profile
// ApiKey must be exactly "%ENVIRONMENT_VARIABLE_NAME%", resolved against
// the process environment at startup. No embedded substitution, escaping,
// defaults, configuration-key lookups, shell evaluation, or recursion.
using System.Text.RegularExpressions;

namespace RecurringTasksBot.Infrastructure.Configuration;

public static partial class EnvironmentReferenceResolver
{
    [GeneratedRegex(@"\A%([A-Za-z_][A-Za-z0-9_]*)%\z")]
    private static partial Regex WholeReference();

    public static bool TryParseReference(string? configured, out string variableName)
    {
        variableName = string.Empty;
        if (configured is null)
            return false;
        var match = WholeReference().Match(configured);
        if (!match.Success)
            return false;
        variableName = match.Groups[1].Value;
        return true;
    }

    // Resolves one profile ApiKey reference. The lookup is injectable for
    // tests; production passes Environment.GetEnvironmentVariable. Errors
    // name the profile, path, and variable but never the resolved value.
    public static string ResolveApiKey(
        string? configured,
        string profileName,
        string configPath,
        Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        if (!TryParseReference(configured, out var variableName))
            throw new InvalidOperationException(
                $"Invalid {configPath} for LLM profile '{profileName}': " +
                "the API key must be a whole-value environment reference " +
                "like '%RecurringTasksBot__Llm__OpenRouter__ApiKey%'. Literal keys in JSON are invalid.");
        var value = lookup(variableName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Missing required credential for LLM profile '{profileName}': " +
                $"environment variable '{variableName}' ({configPath}) is not set.");
        return value;
    }
}
