using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RecurringTasksBot;
using RecurringTasksBot.Core;

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, config) =>
    {
        // Common profile first, environment profile over it, environment
        // variables last: editing appsettings.json (or the Development /
        // Production profile) changes non-secret settings without code or
        // redeploy-time edits. Secrets come only from the environment.
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        config.AddJsonFile(
            $"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
            optional: true, reloadOnChange: false);
        config.AddEnvironmentVariables();
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;
        var storage = RequiredEnv("RecurringTasksBot__AzureWebJobsStorage");
        var table = config["RecurringTasksBot:TableName"] ?? "RecurringTasks";
        var botToken = RequiredEnv("RecurringTasksBot__Telegram__BotToken");

        // The LLM credential is validated by presence only: values are never
        // printed, and no paid API calls happen at startup or in ordinary CI.
        var llmApiKey = RequiredEnv("RecurringTasksBot__Llm__ApiKey");
        var llmOptions = LlmConfig.Read(key => config[key]);
        llmOptions.Validate();

        services.AddSingleton(new BotOptions(
            storage,
            table,
            botToken,
            Environment.GetEnvironmentVariable("RecurringTasksBot__Telegram__WebhookSecret")
                ?? string.Empty));
        services.AddSingleton(llmOptions);
        services.AddSingleton<TableClients>();
        services.AddSingleton<IOperationStore, TableOperationStore>();
        services.AddSingleton<IUpdateReceiptStore, TableUpdateReceiptStore>();
        services.AddSingleton<IDeliveryReceiptStore, TableDeliveryReceiptStore>();
        services.AddSingleton<IOccurrencePayloadStore, TableOccurrencePayloadStore>();
        services.AddHttpClient<ITelegramSender, TelegramBotSender>();
        services.AddHttpClient(nameof(ILlmPromptExecutor), client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // per-request timeout applies
        });
        services.AddSingleton<ILlmPromptExecutor>(provider =>
            new OpenRouterLlmExecutor(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(
                    nameof(ILlmPromptExecutor)),
                llmOptions,
                llmApiKey));
    })
    .Build();

host.Run();
return;

static string RequiredEnv(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing required environment variable: {name}");
