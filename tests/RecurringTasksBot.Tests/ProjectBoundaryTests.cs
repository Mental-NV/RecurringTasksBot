// Project boundary check: Application must not reference Infrastructure or
// the FunctionApp host, and Infrastructure must not reference the host.
// Uses only reflection over the referenced production assemblies; no
// architecture-test package.
using System.Reflection;
using RecurringTasksBot.Application;
using RecurringTasksBot.FunctionApp;
using RecurringTasksBot.Infrastructure.Bot;

namespace RecurringTasksBot.Tests;

public sealed class ProjectBoundaryTests
{
    private static readonly Assembly Application = typeof(OperationRecord).Assembly;
    private static readonly Assembly Infrastructure = typeof(TelegramBotSender).Assembly;
    private static readonly Assembly Host = typeof(RecurrenceFunctions).Assembly;

    [Fact]
    public void Application_ReferencesNeitherInfrastructureNorHost()
    {
        var references = Application.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();
        Assert.DoesNotContain(Infrastructure.GetName().Name, references);
        Assert.DoesNotContain(Host.GetName().Name, references);
    }

    [Fact]
    public void Infrastructure_DoesNotReferenceHost()
    {
        var references = Infrastructure.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();
        Assert.DoesNotContain(Host.GetName().Name, references);
        Assert.Contains(Application.GetName().Name, references);
    }

    [Fact]
    public void Host_ReferencesBothLibraries()
    {
        var references = Host.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();
        Assert.Contains(Application.GetName().Name, references);
        Assert.Contains(Infrastructure.GetName().Name, references);
    }
}
