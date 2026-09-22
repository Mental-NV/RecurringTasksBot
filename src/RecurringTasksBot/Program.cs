using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RecurringTasksBot;
using RecurringTasksBot.Core;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        var storage = RequiredEnv("RecurringTasksBot__AzureWebJobsStorage");
        var table = Environment.GetEnvironmentVariable("RecurringTasksBot__TableName")
            ?? "RecurringTasks";
        var botToken = RequiredEnv("RecurringTasksBot__Telegram__BotToken");

        services.AddSingleton(new BotOptions(
            storage,
            table,
            botToken,
            Environment.GetEnvironmentVariable("RecurringTasksBot__Telegram__WebhookSecret")
                ?? string.Empty));
        services.AddSingleton<TableClients>();
        services.AddSingleton<IOperationStore, TableOperationStore>();
        services.AddSingleton<IUpdateReceiptStore, TableUpdateReceiptStore>();
        services.AddSingleton<IDeliveryReceiptStore, TableDeliveryReceiptStore>();
        services.AddHttpClient<ITelegramSender, TelegramBotSender>();
    })
    .Build();

host.Run();
return;

static string RequiredEnv(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing required environment variable: {name}");
