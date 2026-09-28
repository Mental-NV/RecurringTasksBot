// Functions-host verification: every generated function entry point must
// resolve unambiguously (no overloaded C# method names, no duplicate
// Function names), and a worker host built with the production entry-point
// classes must construct them all from DI.
using System.Reflection;

using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using RecurringTasksBot.Application;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.Tests;

public sealed class FunctionHostTests
{
    private static IReadOnlyList<(Type Type, MethodInfo Method, string Name)> EntryPoints()
    {
        var host = typeof(TaskLifecycleFunctions).Assembly;
        return host.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => (Type: t, Method: m,
                    Attribute: m.GetCustomAttribute<FunctionAttribute>())))
            .Where(e => e.Attribute is not null)
            .Select(e => (e.Type, e.Method, e.Attribute!.Name))
            .ToList();
    }

    [Fact]
    public void FunctionEntryPoints_ResolveUnambiguously()
    {
        var entries = EntryPoints();
        Assert.NotEmpty(entries);
        // The worker dispatches to a C# method: overloads sharing one
        // method name in a class never execute.
        foreach (var group in entries.GroupBy(e => new { e.Type, e.Method.Name }))
            Assert.True(group.Count() == 1,
                $"Overloaded function entry point: {group.Key.Type.Name}.{group.Key.Name}");
        // Each Function name must identify exactly one entry point.
        foreach (var group in entries.GroupBy(e => e.Name))
            Assert.True(group.Count() == 1,
                $"Duplicate [Function] name: {group.Key}");
    }

    [Fact]
    public async Task FunctionsHost_BuildsAndResolvesEveryEntryPoint()
    {
        using var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices(services =>
            {
                var operations = new FakeOperationStore();
                var clock = new FakeClock();
                services.AddSingleton<ITaskStore, FakeTaskStore>();
                services.AddSingleton<IOperationStore>(operations);
                services.AddSingleton<IOccurrenceRepository>(
                    new FakeOccurrenceRepository(operations, clock));
                services.AddSingleton<IUpdateReceiptStore, FakeReceiptStore>();
                services.AddSingleton<ITelegramTransport, FakeTelegramSender>();
                services.AddSingleton<ILlmExecutor, FakeLlmExecutor>();
                services.AddSingleton(TaskDefaults.Default);
                services.AddSingleton(TestLlm.Execution());
                services.AddSingleton(new TelegramOptions("fake-token", "fake-secret"));
                services.AddSingleton(Mock.Of<ITaskOccurrenceRunner>());
                services.AddSingleton(p => new ExecuteOccurrenceHandler(
                    p.GetRequiredService<IOperationStore>(),
                    p.GetRequiredService<IOccurrenceRepository>(),
                    p.GetRequiredService<ITelegramTransport>(),
                    p.GetRequiredService<ILlmExecutor>(),
                    p.GetRequiredService<ExecutionOptions>(),
                    TestLlm.ProviderName, TestLlm.ModelName));
                services.AddSingleton<Func<DurableTaskClient, ITaskOrchestrationClient>>(
                    _ => new FakeOrchestrations());
                services.AddSingleton<TaskLifecycleFunctions>();
                services.AddSingleton<RecurrenceFunctions>();
                services.AddSingleton<WebhookFunction>();
            })
            .Build();

        foreach (var type in EntryPoints().Select(e => e.Type).Distinct())
            Assert.NotNull(host.Services.GetService(type));
        await host.StopAsync();
    }
}
