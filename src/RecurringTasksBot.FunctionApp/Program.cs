using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecurringTasksBot.Application;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Infrastructure.Bot;
using RecurringTasksBot.Infrastructure.Configuration;
using RecurringTasksBot.Infrastructure.Llm;
using RecurringTasksBot.Infrastructure.Persistence;

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, config) =>
    {
        // The shared loader owns precedence: common profile first, the
        // selected environment profile over it, environment variables
        // last. Secrets come only from the environment, never JSON.
        var loaded = AppConfiguration.Load(
            AppContext.BaseDirectory, context.HostingEnvironment.EnvironmentName);
        config.AddConfiguration(loaded);
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;
        var execution = AppConfiguration.ReadExecution(config);
        execution.Validate();
        var provider = AppConfiguration.ReadOpenRouter(config);
        provider.Validate();
        if (execution.RequestTimeout > ExecutionLimits.MaxLlmTimeout)
            throw new InvalidOperationException(
                "LLM request timeout exceeds the single-activity time budget.");
        var storage = AppConfiguration.ReadTableStorage(config);
        var telegram = AppConfiguration.ReadTelegram(config);
        var taskDefaults = AppConfiguration.ReadTaskDefaults(config);

        // The LLM credential is validated by presence only: values are never
        // printed, and no paid API calls happen at startup or in ordinary CI.
        var llmApiKey = config["RecurringTasksBot:Llm:ApiKey"];
        if (string.IsNullOrEmpty(llmApiKey))
            throw new InvalidOperationException("Missing required configuration value: 'RecurringTasksBot:Llm:ApiKey'.");

        services.AddSingleton(execution);
        services.AddSingleton(provider);
        services.AddSingleton(storage);
        services.AddSingleton(telegram);
        services.AddSingleton(taskDefaults);
        services.AddSingleton<TableClients>();
        services.AddSingleton<IOccurrenceRepository, TableOccurrenceRepository>();
        services.AddSingleton<IOperationStore, TableOperationStore>();
        services.AddSingleton<ITaskStore, TableTaskStore>();
        services.AddSingleton<ITaskOccurrenceRunner>(p =>
            new TaskOccurrenceRunner(
                p.GetRequiredService<ITaskStore>(),
                p.GetRequiredService<IOccurrenceRepository>(),
                p.GetRequiredService<ITelegramTransport>(),
                p.GetRequiredService<ILlmExecutor>(),
                execution,
                provider.Provider,
                provider.Model,
                TimeProvider.System,
                logger: p.GetRequiredService<ILogger<TaskOccurrenceRunner>>()));
        services.AddSingleton<IUpdateReceiptStore, TableUpdateReceiptStore>();
        services.AddHttpClient<ITelegramTransport, TelegramBotSender>();
        services.AddHttpClient(nameof(ILlmExecutor), client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // per-request timeout applies
        });
        services.AddSingleton<OpenRouterLlmExecutor>(p =>
            new OpenRouterLlmExecutor(
                p.GetRequiredService<IHttpClientFactory>().CreateClient(
                    nameof(ILlmExecutor)),
                provider,
                execution,
                llmApiKey));
        services.AddSingleton<ILlmExecutor>(p =>
            p.GetRequiredService<OpenRouterLlmExecutor>());
        services.AddSingleton<ExecuteOccurrenceHandler>(p =>
            new ExecuteOccurrenceHandler(
                p.GetRequiredService<IOperationStore>(),
                p.GetRequiredService<IOccurrenceRepository>(),
                p.GetRequiredService<ITelegramTransport>(),
                p.GetRequiredService<ILlmExecutor>(),
                execution,
                provider.Provider,
                provider.Model,
                TimeProvider.System,
                logger: p.GetRequiredService<ILogger<ExecuteOccurrenceHandler>>()));
        // The Durable client exists only per invocation, so the
        // recurrence adapter is created through this small explicit
        // factory instead of constructor injection.
        services.AddSingleton<Func<DurableTaskClient, ITaskOrchestrationClient>>(
            client => new DurableOrchestrationClient(client));
    })
    .Build();

host.Run();
