// Production service registration, shared by the Functions host and the
// host tests. Configuration loading and precedence stay in Program.cs;
// everything validated and registered from the loaded configuration lives
// here exactly once, so tests exercise the production path with test
// configuration instead of a parallel hand-built container.
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Bot;
using RecurringTasksBot.Infrastructure.Configuration;
using RecurringTasksBot.Infrastructure.Llm;
using RecurringTasksBot.Infrastructure.Persistence;

namespace RecurringTasksBot.FunctionApp;

public static class FunctionAppServices
{
    public static IServiceCollection AddFunctionAppServices(
        this IServiceCollection services, IConfiguration config)
    {
        var execution = AppConfiguration.ReadExecution(config);
        execution.Validate();
        var selected = AppConfiguration.ReadSelectedLlm(config);
        selected.Defaults.Validate();
        // The selected provider connection is validated here at startup,
        // not on first adapter resolution: invalid limits fail the host
        // before any registration completes.
        selected.OpenRouter?.Validate();
        selected.DeepSeek?.Validate();
        if (execution.RequestTimeout > ExecutionLimits.MaxLlmTimeout)
            throw new InvalidOperationException(
                "LLM request timeout exceeds the single-activity time budget.");
        var storage = AppConfiguration.ReadTableStorage(config);
        var telegram = AppConfiguration.ReadTelegram(config);

        // The selected profile credential is validated by presence only:
        // values are never printed, and no paid API calls happen at
        // startup or in ordinary CI. Only the selected profile's
        // reference is resolved; inactive profiles need no secret.
        var llmApiKey = AppConfiguration.ResolveSelectedApiKey(config, selected);

        services.AddSingleton(execution);
        services.AddSingleton(selected);
        services.AddSingleton(selected.Defaults);
        services.AddSingleton(storage);
        services.AddSingleton(telegram);
        services.AddSingleton<TableClients>();
        services.AddSingleton<IOccurrenceRepository, TableOccurrenceRepository>();
        services.AddSingleton<ITaskStore, TableTaskStore>();
        services.AddSingleton<ITaskOccurrenceRunner>(p =>
            new TaskOccurrenceRunner(
                p.GetRequiredService<ITaskStore>(),
                p.GetRequiredService<IOccurrenceRepository>(),
                p.GetRequiredService<ITelegramTransport>(),
                p.GetRequiredService<ILlmExecutor>(),
                execution,
                selected.Provider,
                selected.Model,
                TimeProvider.System,
                logger: p.GetRequiredService<ILogger<TaskOccurrenceRunner>>()));
        services.AddSingleton<IUpdateReceiptStore, TableUpdateReceiptStore>();
        services.AddHttpClient<ITelegramTransport, TelegramBotSender>();
        services.AddHttpClient(nameof(ILlmExecutor), client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // per-request timeout applies
        });
        services.AddSingleton<ILlmExecutor>(p =>
            LlmAdapterFactory.CreateSelected(
                p.GetRequiredService<IHttpClientFactory>().CreateClient(
                    nameof(ILlmExecutor)),
                execution,
                selected,
                llmApiKey).Executor);
        // The Durable client exists only per invocation, so the
        // recurrence adapter is created through this small explicit
        // factory instead of constructor injection.
        services.AddSingleton<Func<DurableTaskClient, ITaskOrchestrationClient>>(
            client => new DurableOrchestrationClient(client));
        return services;
    }
}
