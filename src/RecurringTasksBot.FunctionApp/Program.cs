using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Infrastructure.Configuration;

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
        services.AddFunctionAppServices(context.Configuration))
    .Build();

host.Run();
