// Reusable configuration loader: common JSON first, the selected
// environment profile over it, environment variables last. Accepts an
// explicit configuration directory and environment name so the host,
// local tools, and tests resolve the same files without depending on the
// caller's working directory. Secrets never come from JSON: credentials
// are read from the environment-provided configuration values and fail
// startup when absent. A present-but-malformed value fails instead of
// silently falling back to its default.
using Microsoft.Extensions.Configuration;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Infrastructure.Configuration;

public static class AppConfiguration
{
    public const string Section = "RecurringTasksBot";

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
        MemoryMode: Str(config, $"{Section}:Memory:Mode", ExecutionOptions.PreviousSuccessfulReply),
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
        SystemInstruction: Str(config, $"{Section}:Llm:SystemInstruction", string.Empty));

    public static OpenRouterOptions ReadOpenRouter(IConfiguration config) => new(
        Provider: Str(config, $"{Section}:Llm:Provider", "OpenRouter"),
        BaseUrl: Str(config, $"{Section}:Llm:BaseUrl", "https://openrouter.ai/api/v1"),
        Model: Str(config, $"{Section}:Llm:Model", "deepseek/deepseek-v4.1-flash"),
        ReasoningEffort: Str(config, $"{Section}:Llm:ReasoningEffort", "Maximum"),
        SearchEnabled: Bool(config, $"{Section}:Llm:SearchEnabled", true),
        SearchEngine: Str(config, $"{Section}:Llm:SearchEngine", "parallel"),
        SearchMode: Str(config, $"{Section}:Llm:SearchMode", "fast"),
        MaxSearches: Int(config, $"{Section}:Llm:MaxSearches", 8),
        MaxResultsPerSearch: Int(config, $"{Section}:Llm:MaxResultsPerSearch", 5),
        MaxTotalResults: Int(config, $"{Section}:Llm:MaxTotalResults", 40));

    public static TableStorageOptions ReadTableStorage(IConfiguration config) => new(
        StorageConnectionString: Required(config, $"{Section}:AzureWebJobsStorage"),
        TableName: Str(config, $"{Section}:TableName", "RecurringTaskData"));

    public static TelegramOptions ReadTelegram(IConfiguration config) => new(
        BotToken: Required(config, $"{Section}:Telegram:BotToken"),
        WebhookSecret: Required(config, $"{Section}:Telegram:WebhookSecret"));

    private static string Str(IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static int Int(IConfiguration config, string key, int fallback)
    {
        var raw = config[key];
        if (string.IsNullOrEmpty(raw))
            return fallback;
        if (int.TryParse(raw, out var value))
            return value;
        throw new InvalidOperationException($"Invalid integer for '{key}': '{raw}'.");
    }

    private static bool Bool(IConfiguration config, string key, bool fallback)
    {
        var raw = config[key];
        if (string.IsNullOrEmpty(raw))
            return fallback;
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
