// Offline LLM smoke probe: builds the exact execution messages and,
// only with --execute on a machine with network access plus an API key,
// performs one live OpenRouter call. Shares the host configuration
// loader; never references the FunctionApp assembly.
using System.Text.Json;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Configuration;
using RecurringTasksBot.Infrastructure.Llm;

return await LlmSmoke.Run(args);

static class LlmSmoke
{
    public static async Task<int> Run(string[] args)
    {
        string? prompt = null;
        string? previousReply = null;
        string? modelOverride = null;
        string? configDir = null;
        var execute = false;
        var validateConfig = false;
        int? maxSourceScalars = null;
        var maxRequests = 1;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--prompt" when i + 1 < args.Length:
                    prompt = args[++i];
                    break;
                case "--previous-reply" when i + 1 < args.Length:
                    previousReply = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    modelOverride = args[++i];
                    break;
                case "--config-dir" when i + 1 < args.Length:
                    configDir = args[++i];
                    break;
                case "--execute":
                    execute = true;
                    break;
                case "--validate-config":
                    validateConfig = true;
                    break;
                case "--max-source-scalars" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], out var parsed) && parsed > 0:
                    maxSourceScalars = parsed;
                    i++;
                    break;
                case "--max-requests" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], out var count) && count > 0:
                    maxRequests = count;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        configDir ??= Environment.GetEnvironmentVariable("FUNCTIONAPP_CONFIG_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "RecurringTasksBot.FunctionApp");
        prompt ??= Environment.GetEnvironmentVariable("SMOKE_PROMPT_FILE") is { Length: > 0 } path
            ? await File.ReadAllTextAsync(path)
            : "What were the three most important AI announcements in the past 24 hours? Include source links.";
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";
        var config = AppConfiguration.Load(Path.GetFullPath(configDir), environment);
        var execution = AppConfiguration.ReadExecution(config);
        var provider = AppConfiguration.ReadOpenRouter(config);
        if (modelOverride is not null)
            provider = provider with { Model = modelOverride };
        execution.Validate();
        provider.Validate();
        if (execution.RequestTimeout > ExecutionLimits.MaxLlmTimeout)
            throw new InvalidOperationException(
                "LLM request timeout exceeds the single-activity time budget.");
        maxSourceScalars ??= execution.MaxAnswerSourceChars;

        // Same rules as runtime: timestamps are current, and a supplied
        // previous reply applies only when memory mode keeps one.
        var now = DateTimeOffset.UtcNow;
        var previousDay = now.AddDays(-1);
        MemoryRecord? memory = null;
        if (previousReply is not null)
        {
            if (!execution.MemoryMode.Equals(ExecutionOptions.PreviousSuccessfulReply, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Ignoring --previous-reply: memory mode is '{execution.MemoryMode}'.");
            }
            else
            {
                memory = new MemoryRecord(
                    "smoke-owner", "smoke-operation",
                    previousDay.UtcDateTime,
                    previousDay.AddSeconds(20).UtcDateTime,
                    now, "v-smoke-prev", previousReply,
                    DeliveryPlan.HashSource(previousReply),
                    AnswerSourceBound.CountScalars(previousReply));
            }
        }
        var snapshot = new ExecutionContextSnapshot(
            prompt,
            "0 0 9 * * *",
            ExecutionLimits.ScheduleTimezone,
            "smoke-occurrence",
            now.ToString("o"),
            now.AddSeconds(1).ToString("o"),
            memory is not null,
            memory is null ? null : previousDay.ToString("o"),
            memory is null ? null : previousDay.AddSeconds(20).ToString("o"),
            ExecutionLimits.EnabledCapabilities,
            ExecutionLimits.InstructionVersion);
        var instruction = RecurringTaskSystemPrompt.Render(
            execution.TargetAnswerTextChars,
            TelegramLimits.RichTextChars,
            execution.SystemInstruction);
        var messages = ExecutionMessageBuilder.BuildMessages(
            instruction, snapshot, memory?.Answer);
        if (!ContextBudget.FitsBudget(messages, execution, execution.CompletionTokenBudget))
            throw new InvalidOperationException("Smoke prompt does not fit the context budget.");

        var payload = OpenRouterRequestBuilder.BuildRequestJson(provider, execution, messages);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            built = true,
            messageCount = messages.Count,
            model = provider.Model,
            requestCharacters = payload.Length,
            validateConfig,
        }));

        if (!execute)
            return 0;

        var apiKey = Environment.GetEnvironmentVariable("RecurringTasksBot__Llm__ApiKey");
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.Error.WriteLine("Missing RecurringTasksBot__Llm__ApiKey for --execute.");
            return 2;
        }

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var executor = new OpenRouterLlmExecutor(http, provider, execution, apiKey);
        var valid = true;
        for (var attempt = 0; attempt < maxRequests; attempt++)
        {
            var result = await executor.ExecuteAsync(
                new LlmRequest(messages, maxSourceScalars.Value));
            var plan = DeliveryPlan.CreateInitial("smoke", result.AnswerText);
            var ok = result.SearchUsed &&
                result.Sources.Count <= provider.MaxTotalResults &&
                plan.Leaves.Count > 0;
            valid &= ok;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                answerCharacters = result.AnswerText.Length,
                sources = result.Sources.Count,
                searchUsed = result.SearchUsed,
                provider = result.Provider,
                model = result.Model,
                leaves = plan.Leaves.Count,
            }));
        }

        Console.WriteLine(valid
            ? "PASS: complete answer, search citations, valid plan leaves"
            : "FAIL: search/citation or plan checks");
        return valid ? 0 : 1;
    }
}
