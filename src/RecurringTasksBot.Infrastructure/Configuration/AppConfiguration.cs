// Reusable configuration loader: common JSON first, the selected
// environment profile over it, environment variables last. Accepts an
// explicit configuration directory and environment name so the host,
// local tools, and tests resolve the same files without depending on the
// caller's working directory. Secrets never come from JSON: profile
// credentials are whole-value "%ENV_VAR%" references resolved from the
// process environment, and only for the selected profile. A
// present-but-malformed value fails instead of silently falling back to
// its default, and removed settings fail with a path-only migration error.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Infrastructure.Configuration;

public sealed record SelectedLlmConfiguration(
    string ActiveProfile,
    string Provider,
    OpenRouterOptions? OpenRouter,
    DeepSeekOptions? DeepSeek,
    TaskDefaults Defaults)
{
    public string Model => OpenRouter?.Model ?? DeepSeek?.Model ??
        throw new InvalidOperationException(
            $"LLM profile '{ActiveProfile}' has no configured model.");
}

public static class AppConfiguration
{
    public const string Section = "RecurringTasksBot";

    public const string OpenRouterProfileName = "OpenRouter";
    public const string DeepSeekProfileName = "DeepSeek";

    public static IConfigurationRoot Load(string configDirectory, string environmentName)
    {
        return new ConfigurationBuilder()
            .SetBasePath(configDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    public static ExecutionOptions ReadExecution(IConfiguration config) => new(
        MemoryMode: StrictStr(config, $"{Section}:Memory:Mode", ExecutionOptions.PreviousSuccessfulReply),
        TargetAnswerTextChars: Int(config, $"{Section}:Llm:TargetAnswerTextChars", 24000),
        MaxAnswerSourceChars: Int(config, $"{Section}:Llm:MaxAnswerSourceChars",
            ExecutionLimits.AnswerSourceMaxScalars),
        DeclaredContextTokens: Int(config, $"{Section}:Llm:DeclaredContextTokens", 1048576),
        SearchContextReserveTokens: Int(config, $"{Section}:Llm:SearchContextReserveTokens", 65536),
        ContextEnvelopeReserveTokens: Int(config, $"{Section}:Llm:ContextEnvelopeReserveTokens", 8192),
        RequestTimeout: TimeSpan.FromSeconds(
            Int(config, $"{Section}:Llm:RequestTimeoutSeconds", 480)),
        CompletionTokenBudget: Int(config, $"{Section}:Llm:CompletionTokenBudget", 131072),
        GenerationRetries: Int(config, $"{Section}:Llm:GenerationRetries", 2),
        SystemInstruction: Str(config, $"{Section}:Llm:SystemInstruction", string.Empty),
        SearchEnabled: Bool(config, $"{Section}:Llm:SearchEnabled", true));

    // Selected-provider connection: profile transport/search options plus
    // the shared reasoning/search defaults. The wire protocol and readers
    // are unchanged; search-disabled execution is allowed.
    public static OpenRouterOptions ReadOpenRouter(IConfiguration config) =>
        ReadOpenRouterProfile(config, OpenRouterProfileName);

    public static DeepSeekOptions ReadDeepSeek(IConfiguration config) =>
        ReadDeepSeekProfile(config, DeepSeekProfileName);

    // Task defaults are derived, not configured: one authoritative path
    // per setting (Memory:Mode, Llm:ReasoningEffort, Llm:SearchEnabled).
    // Missing shared values use the code defaults; explicit blank or
    // malformed values fail instead of silently becoming defaults.
    public static TaskDefaults DeriveTaskDefaults(IConfiguration config)
    {
        RejectObsoleteConfiguration(config);
        var memory = NormalizeMemoryMode(StrictStr(
            config, $"{Section}:Memory:Mode", ExecutionOptions.PreviousSuccessfulReply));
        var effort = NormalizeReasoningEffort(StrictStr(
            config, $"{Section}:Llm:ReasoningEffort", "Maximum"));
        var webSearch = Bool(config, $"{Section}:Llm:SearchEnabled", true);
        var defaults = new TaskDefaults(memory, effort, webSearch, Revision: Fingerprint(memory, effort, webSearch));
        defaults.Validate();
        return defaults;
    }

    // Structural selection without secrets: validates shared values and
    // the selected profile, derives defaults, and requires only the
    // reference form of the selected credential. Offline tools and
    // --validate-config use this without reading any secret.
    public static SelectedLlmConfiguration ReadSelectedLlm(IConfiguration config)
    {
        RejectObsoleteConfiguration(config);
        var active = config[$"{Section}:Llm:ActiveProfile"];
        if (string.IsNullOrWhiteSpace(active))
            throw new InvalidOperationException(
                $"Missing required configuration value: '{Section}:Llm:ActiveProfile'. " +
                "Select an LLM profile explicitly; there is no implicit provider fallback.");
        var profile = config.GetSection($"{Section}:Llm:Profiles:{active}");
        if (!profile.Exists())
            throw new InvalidOperationException(
                $"Unknown LLM profile '{active}': no '{Section}:Llm:Profiles:{active}' section is configured.");
        var provider = NormalizeProvider(StrictStr(
            config, $"{Section}:Llm:Profiles:{active}:Provider", string.Empty));
        var defaults = DeriveTaskDefaults(config);
        if (provider.Equals(OpenRouterProfileName, StringComparison.Ordinal))
            return new SelectedLlmConfiguration(active, provider,
                ReadOpenRouterProfile(config, active), null, defaults);
        if (provider.Equals(DeepSeekProfileName, StringComparison.Ordinal))
            return new SelectedLlmConfiguration(active, provider,
                null, ReadDeepSeekProfile(config, active), defaults);
        throw new InvalidOperationException(
            $"Unsupported LLM provider '{provider}' in profile '{active}'. " +
                $"Use '{OpenRouterProfileName}' or '{DeepSeekProfileName}'.");
    }

    // Credential resolution for the selected profile only. Inactive
    // profiles never require their secrets: an OpenRouter-only deployment
    // starts without a DeepSeek key, and vice versa.
    public static string ResolveSelectedApiKey(
        IConfiguration config, SelectedLlmConfiguration selected,
        Func<string, string?>? lookup = null) =>
        EnvironmentReferenceResolver.ResolveApiKey(
            config[$"{Section}:Llm:Profiles:{selected.ActiveProfile}:ApiKey"],
            selected.ActiveProfile,
            $"{Section}:Llm:Profiles:{selected.ActiveProfile}:ApiKey",
            lookup);

    public static TableStorageOptions ReadTableStorage(IConfiguration config) => new(
        StorageConnectionString: Required(config, $"{Section}:AzureWebJobsStorage"),
        TableName: Str(config, $"{Section}:TableName", "RecurringTaskDataV5"));

    public static TelegramOptions ReadTelegram(IConfiguration config) => new(
        BotToken: Required(config, $"{Section}:Telegram:BotToken"),
        WebhookSecret: Required(config, $"{Section}:Telegram:WebhookSecret"));

    private static OpenRouterOptions ReadOpenRouterProfile(IConfiguration config, string profile)
    {
        RequireProfile(config, profile, OpenRouterProfileName);
        RejectSharedCopies(config, profile);
        return new OpenRouterOptions(
            Provider: OpenRouterProfileName,
            BaseUrl: ProfileUrl(config, profile),
            Model: ProfileModel(config, profile),
            ReasoningEffort: StrictStr(config, $"{Section}:Llm:ReasoningEffort", "Maximum"),
            SearchEnabled: Bool(config, $"{Section}:Llm:SearchEnabled", true),
            SearchEngine: Str(config, ProfileKey(profile, "SearchEngine"), "parallel"),
            SearchMode: Str(config, ProfileKey(profile, "SearchMode"), "fast"),
            MaxSearches: Int(config, ProfileKey(profile, "MaxSearches"), 8),
            MaxResultsPerSearch: Int(config, ProfileKey(profile, "MaxResultsPerSearch"), 5),
            MaxTotalResults: Int(config, ProfileKey(profile, "MaxTotalResults"), 40));
    }

    private static DeepSeekOptions ReadDeepSeekProfile(IConfiguration config, string profile)
    {
        RequireProfile(config, profile, DeepSeekProfileName);
        RejectSharedCopies(config, profile);
        foreach (var key in new[] { "SearchEngine", "SearchMode", "MaxResultsPerSearch" })
        {
            if (config[ProfileKey(profile, key)] is not null)
                throw new InvalidOperationException(
                    $"Unsupported '{ProfileKey(profile, key)}' in selected DeepSeek profile '{profile}': " +
                    "DeepSeek has no per-search or engine/mode controls. Remove the setting.");
        }
        return new DeepSeekOptions(
            Provider: DeepSeekProfileName,
            BaseUrl: ProfileUrl(config, profile),
            Model: ProfileModel(config, profile),
            ReasoningEffort: StrictStr(config, $"{Section}:Llm:ReasoningEffort", "Maximum"),
            SearchEnabled: Bool(config, $"{Section}:Llm:SearchEnabled", true),
            MaxSearches: Int(config, ProfileKey(profile, "MaxSearches"), 8),
            MaxTotalResults: Int(config, ProfileKey(profile, "MaxTotalResults"), 40));
    }

    private static void RequireProfile(IConfiguration config, string profile, string expectedProvider)
    {
        if (!config.GetSection($"{Section}:Llm:Profiles:{profile}").Exists())
            throw new InvalidOperationException(
                $"Missing LLM profile '{profile}': no '{Section}:Llm:Profiles:{profile}' section is configured.");
        var provider = NormalizeProvider(StrictStr(
            config, ProfileKey(profile, "Provider"), string.Empty));
        if (!provider.Equals(expectedProvider, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"LLM profile '{profile}' declares provider '{provider}'; " +
                $"expected '{expectedProvider}'. Select by '{Section}:Llm:ActiveProfile'.");
        var apiKey = config[ProfileKey(profile, "ApiKey")];
        if (!EnvironmentReferenceResolver.TryParseReference(apiKey, out _))
            throw new InvalidOperationException(
                $"Invalid {ProfileKey(profile, "ApiKey")} for LLM profile '{profile}': " +
                "the API key must be a whole-value environment reference " +
                "like '%RecurringTasksBot__Llm__OpenRouter__ApiKey%'. Literal keys in JSON are invalid.");
    }

    private static void RejectSharedCopies(IConfiguration config, string profile)
    {
        foreach (var key in new[] { "ReasoningEffort", "SearchEnabled" })
        {
            if (config[ProfileKey(profile, key)] is not null)
                throw new InvalidOperationException(
                    $"Unsupported '{ProfileKey(profile, key)}' in LLM profile '{profile}': " +
                    $"shared task defaults stay at '{Section}:Llm:{key}' and apply to every profile.");
        }
    }

    private static string ProfileKey(string profile, string key) =>
        $"{Section}:Llm:Profiles:{profile}:{key}";

    private static string ProfileUrl(IConfiguration config, string profile)
    {
        var baseUrl = StrictStr(config, ProfileKey(profile, "BaseUrl"), string.Empty);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Invalid {ProfileKey(profile, "BaseUrl")} in LLM profile '{profile}': " +
                "the base URL must be an absolute HTTPS URL.");
        return baseUrl;
    }

    private static string ProfileModel(IConfiguration config, string profile)
    {
        var model = StrictStr(config, ProfileKey(profile, "Model"), string.Empty);
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                $"Missing required configuration value: '{ProfileKey(profile, "Model")}'.");
        return model;
    }

    private static string NormalizeProvider(string provider)
    {
        if (provider.Equals(OpenRouterProfileName, StringComparison.OrdinalIgnoreCase))
            return OpenRouterProfileName;
        if (provider.Equals(DeepSeekProfileName, StringComparison.OrdinalIgnoreCase))
            return DeepSeekProfileName;
        throw new InvalidOperationException(
            $"Unsupported LLM provider '{provider}'. Use '{OpenRouterProfileName}' or '{DeepSeekProfileName}'.");
    }

    internal static string NormalizeMemoryMode(string mode) => mode switch
    {
        ExecutionOptions.PreviousSuccessfulReply => TaskMemoryModes.IncludePreviousMessage,
        ExecutionOptions.None => TaskMemoryModes.None,
        _ => throw new InvalidOperationException(
            $"Unknown {Section}:Memory:Mode '{mode}'. " +
            $"Use '{ExecutionOptions.PreviousSuccessfulReply}' or '{ExecutionOptions.None}'."),
    };

    internal static string NormalizeReasoningEffort(string effort)
    {
        if (effort.Equals("Maximum", StringComparison.OrdinalIgnoreCase))
            return TaskReasoningEfforts.Max;
        var token = effort.ToLowerInvariant();
        if (TaskReasoningEfforts.All.Contains(token))
            return token;
        throw new InvalidOperationException(
            $"Unknown {Section}:Llm:ReasoningEffort '{effort}'. " +
            "Use 'Maximum' or a task effort token (low, med, high, xhigh, max).");
    }

    // Defaults fingerprint: identical effective defaults yield the same
    // revision; changing any of them changes it. Secrets, profile
    // identity, and transport settings are excluded.
    internal static string Fingerprint(string memoryMode, string reasoningEffort, bool webSearch)
    {
        var json = "{\"memoryMode\":\"" + memoryMode +
            "\",\"reasoningEffort\":\"" + reasoningEffort +
            "\",\"webSearch\":" + (webSearch ? "true" : "false") + "}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "defaults-v2:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void RejectObsoleteConfiguration(IConfiguration config)
    {
        if (config.GetSection($"{Section}:TaskDefaults").Exists())
            throw new InvalidOperationException(
                $"Obsolete configuration '{Section}:TaskDefaults' was removed: task defaults now derive " +
                $"from '{Section}:Memory:Mode', '{Section}:Llm:ReasoningEffort', and '{Section}:Llm:SearchEnabled'. " +
                "Delete the section and any matching environment overrides.");
        foreach (var key in new[] { "Provider", "BaseUrl", "Model", "SearchEngine", "SearchMode",
            "MaxSearches", "MaxResultsPerSearch", "MaxTotalResults" })
        {
            if (config[$"{Section}:Llm:{key}"] is not null)
                throw new InvalidOperationException(
                    $"Obsolete configuration '{Section}:Llm:{key}' was removed: provider connections now live " +
                    $"under '{Section}:Llm:Profiles:<name>'. Move the setting into its named profile.");
        }
    }

    private static string Str(IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        return value is null ? fallback : value;
    }

    private static string StrictStr(IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        if (value is null)
            return fallback;
        if (value.Length == 0)
            throw new InvalidOperationException($"Invalid empty value for '{key}': the setting requires explicit text.");
        return value;
    }

    private static int Int(IConfiguration config, string key, int fallback)
    {
        var raw = config[key];
        if (raw is null)
            return fallback;
        if (raw.Length == 0)
            throw new InvalidOperationException($"Invalid empty value for '{key}': the setting requires an integer.");
        if (int.TryParse(raw, out var value))
            return value;
        throw new InvalidOperationException($"Invalid integer for '{key}': '{raw}'.");
    }

    private static bool Bool(IConfiguration config, string key, bool fallback)
    {
        var raw = config[key];
        if (raw is null)
            return fallback;
        if (raw.Length == 0)
            throw new InvalidOperationException($"Invalid empty value for '{key}': the setting requires true or false.");
        if (bool.TryParse(raw, out var value))
            return value;
        throw new InvalidOperationException($"Invalid boolean for '{key}': '{raw}'.");
    }

    private static string Required(IConfiguration config, string key)
    {
        var value = config[key];
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"Missing required configuration value: '{key}'.");
        return value;
    }
}
