// Functions-host verification: every generated function entry point must
// resolve unambiguously (no overloaded C# method names, no duplicate
// Function names), and a worker host built through the production service
// registration must construct them all from DI.
using System.Reflection;
using System.Text.Json;

using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RecurringTasksBot.Application;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Infrastructure.Configuration;
using RecurringTasksBot.Infrastructure.Llm;
using RecurringTasksBot.Infrastructure.Persistence;

namespace RecurringTasksBot.Tests;

public sealed class FunctionHostTests
{
    private static IReadOnlyList<(Type Type, MethodInfo Method, string Name)> EntryPoints()
    {
        var host = typeof(TaskLifecycleFunctions).Assembly;
        return host.GetTypes()
            .SelectMany(t => t.GetMethods(DeclaredOnly)
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
        Assert.Empty(FindAmbiguousEntryPoints(
            entries.Select(e => e.Type).Distinct()));
        // Each Function name must identify exactly one entry point.
        foreach (var group in entries.GroupBy(e => e.Name))
            Assert.True(group.Count() == 1,
                $"Duplicate [Function] name: {group.Key}");
    }

    [Fact]
    public void AmbiguityCheck_CatchesUnannotatedOverload()
    {
        // The previous overloaded-Run defect paired an attributed method
        // with a same-named sibling: the check must catch overloads even
        // when only one side carries [Function].
        Assert.Equal(["DeliberateOverload.Run"],
            FindAmbiguousEntryPoints([typeof(DeliberateOverload)]));
        Assert.Empty(FindAmbiguousEntryPoints([typeof(CleanEntryPoints)]));
    }

    [Fact]
    public void GeneratedMetadata_AgreesWithDeclaredFunctions()
    {
        // Reads the worker SDK output copied alongside the test binaries:
        // no build is invoked from this test.
        var path = Path.Combine(AppContext.BaseDirectory, "functions.metadata");
        Assert.True(File.Exists(path),
            $"Missing generated metadata at {path}; build the solution first.");
        var metadata = JsonSerializer.Deserialize<List<FunctionMetadata>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Unparsable {path}.");
        var assembly = typeof(TaskLifecycleFunctions).Assembly;
        foreach (var entry in metadata)
        {
            // Worker-equivalent lookup: entryPoint "Full.Type.Name.Method"
            // must resolve to exactly one method carrying the same name.
            var method = ResolveEntryPoint(assembly, entry.EntryPoint);
            Assert.True(method is not null,
                $"Metadata entry '{entry.Name}' does not resolve: {entry.EntryPoint}.");
            Assert.Equal(entry.Name,
                method!.GetCustomAttribute<FunctionAttribute>()?.Name);
        }
        // Exact agreement in both directions: no missing or stray entries.
        Assert.Equal(
            EntryPoints().Select(e => $"{e.Type.FullName}.{e.Method.Name}").ToHashSet(),
            metadata.Select(e => e.EntryPoint).ToHashSet());
    }

    [Fact]
    public void FunctionCatalog_MatchesRecordedBaseline()
    {
        // Renaming, adding, or removing a function changes the deployed
        // contract: such edits must update this baseline deliberately.
        var expected = new Dictionary<string, string>
        {
            ["RecurrenceLifecycle"] = "RecurringTasksBot.FunctionApp.RecurrenceFunctions.Run",
            ["LoadOperation"] = "RecurringTasksBot.FunctionApp.RecurrenceFunctions.Load",
            ["DeliverOccurrence"] = "RecurringTasksBot.FunctionApp.RecurrenceFunctions.Deliver",
            ["TaskLifecycle"] = "RecurringTasksBot.FunctionApp.TaskLifecycleFunctions.RunLifecycleAsync",
            ["PlanTask"] = "RecurringTasksBot.FunctionApp.TaskLifecycleFunctions.Plan",
            ["ClaimTask"] = "RecurringTasksBot.FunctionApp.TaskLifecycleFunctions.Claim",
            ["RunTask"] = "RecurringTasksBot.FunctionApp.TaskLifecycleFunctions.RunOccurrenceAsync",
            ["CompleteTask"] = "RecurringTasksBot.FunctionApp.TaskLifecycleFunctions.Complete",
            ["FinishTask"] = "RecurringTasksBot.FunctionApp.TaskLifecycleFunctions.Finish",
            ["Webhook"] = "RecurringTasksBot.FunctionApp.WebhookFunction.Run",
        };
        var actual = EntryPoints().ToDictionary(
            e => e.Name, e => $"{e.Type.FullName}.{e.Method.Name}");
        Assert.Equal(expected, actual);
    }

    // A [Function] method must be the only method with its name on the
    // declaring type — attributed or not — or the worker cannot
    // dispatch the entry point.
    private static IReadOnlyList<string> FindAmbiguousEntryPoints(IEnumerable<Type> types) =>
        types.SelectMany(t => t.GetMethods(DeclaredOnly)
                .Where(m => m.GetCustomAttribute<FunctionAttribute>() is not null)
                .Where(m => t.GetMethods(DeclaredOnly).Count(o => o.Name == m.Name) != 1)
                .Select(m => $"{t.Name}.{m.Name}"))
            .Distinct()
            .ToList();

    private const BindingFlags DeclaredOnly = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static MethodInfo? ResolveEntryPoint(Assembly assembly, string entryPoint)
    {
        var dot = entryPoint.LastIndexOf('.');
        if (dot <= 0)
            return null;
        var candidates = assembly.GetType(entryPoint[..dot])
            ?.GetMethods(DeclaredOnly)
            .Where(m => m.Name == entryPoint[(dot + 1)..])
            .ToList();
        return candidates?.Count == 1 ? candidates[0] : null;
    }

    private sealed record FunctionMetadata(string Name, string EntryPoint);

    private sealed class DeliberateOverload
    {
        [Function("one")]
        public void Run()
        {
        }

        public void Run(int attempt)
        {
        }
    }

    private sealed class CleanEntryPoints
    {
        [Function("one")]
        public void First()
        {
        }

        [Function("two")]
        public void Second()
        {
        }

        public void Helper(int attempt)
        {
        }
    }

    [Fact]
    public async Task FunctionsHost_BuildsAndResolvesEveryEntryPoint()
    {
        // Valid test configuration through the production readers: fake
        // secrets by presence only, never used for network calls.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RecurringTasksBot:AzureWebJobsStorage"] = "UseDevelopmentStorage=true",
                ["RecurringTasksBot:Telegram:BotToken"] = "test-bot-token",
                ["RecurringTasksBot:Telegram:WebhookSecret"] = "test-webhook-secret",
                ["RecurringTasksBot:Llm:ApiKey"] = "test-api-key",
            })
            .Build();

        using var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices(services =>
            {
                services.AddFunctionAppServices(config);
                // External I/O and network stay faked: table clients and
                // stores, the Telegram/LLM transports. Everything else —
                // options, defaults, runners, handlers, the Durable client
                // factory — resolves through the production registrations.
                Remove<TableClients>(services);
                Replace<ITaskStore>(services, new FakeTaskStore());
                var operations = new FakeOperationStore();
                Replace<IOperationStore>(services, operations);
                Replace<IOccurrenceRepository>(services,
                    new FakeOccurrenceRepository(operations, new FakeClock()));
                Replace<IUpdateReceiptStore>(services, new FakeReceiptStore());
                Replace<ITelegramTransport>(services, new FakeTelegramSender());
                Remove<OpenRouterLlmExecutor>(services);
                Replace<ILlmExecutor>(services, new FakeLlmExecutor());
            })
            .Build();

        // Production configuration flows into the container untouched.
        Assert.Equal(AppConfiguration.ReadTaskDefaults(config),
            host.Services.GetRequiredService<TaskDefaults>());

        // The worker activates function classes from DI without
        // requiring their registration: every declared entry point's
        // class must construct through the same mechanism.
        foreach (var type in EntryPoints().Select(e => e.Type).Distinct())
            Assert.IsType(type, ActivatorUtilities.CreateInstance(host.Services, type));
        await host.StopAsync();
    }

    private static void Remove<TService>(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
            if (services[i].ServiceType == typeof(TService))
                services.RemoveAt(i);
    }

    private static void Replace<TService>(IServiceCollection services, TService implementation)
        where TService : class
    {
        Remove<TService>(services);
        services.AddSingleton(implementation);
    }
}
