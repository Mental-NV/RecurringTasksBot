// Configuration loading: the shared loader resolves common JSON, the
// environment profile, then environment variables; typed readers apply
// code defaults only for absent keys and fail on malformed values.
using Microsoft.Extensions.Configuration;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Configuration;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Tests;

public sealed class ConfigurationTests
{
    private static IConfiguration Config(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values!)
            .Build();

    [Fact]
    public void EmptyConfiguration_YieldsDocumentedDefaults()
    {
        var execution = AppConfiguration.ReadExecution(
            Config(new Dictionary<string, string?>()));
        Assert.Equal(ExecutionOptions.PreviousSuccessfulReply, execution.MemoryMode);
        Assert.Equal(24000, execution.TargetAnswerTextChars);
        Assert.Equal(ExecutionLimits.AnswerSourceMaxScalars, execution.MaxAnswerSourceChars);
        Assert.Equal(1048576, execution.DeclaredContextTokens);
        Assert.Equal(65536, execution.SearchContextReserveTokens);
        Assert.Equal(8192, execution.ContextEnvelopeReserveTokens);
        Assert.Equal(TimeSpan.FromSeconds(480), execution.RequestTimeout);
        Assert.Equal(131072, execution.CompletionTokenBudget);
        Assert.Equal(2, execution.GenerationRetries);
        Assert.Equal(string.Empty, execution.SystemInstruction);
        execution.Validate();

        var provider = AppConfiguration.ReadOpenRouter(
            Config(new Dictionary<string, string?>()));
        Assert.Equal("OpenRouter", provider.Provider);
        Assert.Equal("https://openrouter.ai/api/v1", provider.BaseUrl);
        Assert.Equal("deepseek/deepseek-v4.1-flash", provider.Model);
        Assert.Equal("Maximum", provider.ReasoningEffort);
        Assert.True(provider.SearchEnabled);
        Assert.Equal("parallel", provider.SearchEngine);
        Assert.Equal("fast", provider.SearchMode);
        Assert.Equal(8, provider.MaxSearches);
        Assert.Equal(5, provider.MaxResultsPerSearch);
        Assert.Equal(40, provider.MaxTotalResults);
        provider.Validate();
    }

    [Fact]
    public void ExplicitValues_OverrideDefaults()
    {
        var execution = AppConfiguration.ReadExecution(Config(
            new Dictionary<string, string?>
            {
                ["RecurringTasksBot:Memory:Mode"] = ExecutionOptions.None,
                ["RecurringTasksBot:Llm:DeclaredContextTokens"] = "100",
            }));
        Assert.Equal(ExecutionOptions.None, execution.MemoryMode);
        Assert.Equal(100, execution.DeclaredContextTokens);
        execution.Validate();
    }

    [Fact]
    public void ExplicitSearchEngineAndMode_OverrideDefaults()
    {
        var provider = AppConfiguration.ReadOpenRouter(Config(
            new Dictionary<string, string?>
            {
                ["RecurringTasksBot:Llm:SearchEngine"] = "exa",
                ["RecurringTasksBot:Llm:SearchMode"] = "turbo",
            }));
        Assert.Equal("exa", provider.SearchEngine);
        Assert.Equal("turbo", provider.SearchMode);
        provider.Validate();
    }

    [Fact]
    public void WhitespaceSearchMode_FailsValidation()
    {
        var provider = TestLlm.Provider() with { SearchMode = "  " };
        Assert.Throws<InvalidOperationException>(() => provider.Validate());
    }

    [Theory]
    [InlineData("RecurringTasksBot:Llm:TargetAnswerTextChars", "abc")]
    [InlineData("RecurringTasksBot:Llm:RequestTimeoutSeconds", "forever")]
    public void MalformedExecutionValues_FailInsteadOfDefaulting(string key, string value)
    {
        var config = Config(new Dictionary<string, string?> { [key] = value });
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadExecution(config));
    }

    [Fact]
    public void MalformedProviderValues_FailInsteadOfDefaulting()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:MaxSearches"] = "many",
        });
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadOpenRouter(config));
    }

    [Fact]
    public void MalformedSearchFlag_FailsInsteadOfDefaulting()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:SearchEnabled"] = "yes",
        });
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadOpenRouter(config));
    }

    [Fact]
    public void MissingSecrets_FailStartup()
    {
        var empty = Config(new Dictionary<string, string?>());
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadTableStorage(empty));
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadTelegram(empty));
    }

    [Fact]
    public void PresentSecrets_LoadWithoutDefaults()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:AzureWebJobsStorage"] = "UseDevelopmentStorage=true",
            ["RecurringTasksBot:Telegram:BotToken"] = "token",
            ["RecurringTasksBot:Telegram:WebhookSecret"] = "secret",
        });
        var storage = AppConfiguration.ReadTableStorage(config);
        Assert.Equal("UseDevelopmentStorage=true", storage.StorageConnectionString);
        Assert.Equal("RecurringTaskData", storage.TableName);
        var telegram = AppConfiguration.ReadTelegram(config);
        Assert.Equal("token", telegram.BotToken);
        Assert.Equal("secret", telegram.WebhookSecret);
    }

    [Fact]
    public void EnvironmentProfile_OverlaysCommon()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                """{"RecurringTasksBot":{"Llm":{"Model":"common-model","MaxSearches":3}}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.Staging.json"),
                """{"RecurringTasksBot":{"Llm":{"MaxSearches":9}}}""");
            var config = AppConfiguration.Load(dir, "Staging");
            var provider = AppConfiguration.ReadOpenRouter(config);
            Assert.Equal("common-model", provider.Model);
            Assert.Equal(9, provider.MaxSearches);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CheckedInDefaults_SelectParallelFastSearch()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RecurringTasksBot.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.True(dir is not null, "Repository root with RecurringTasksBot.sln was not found.");
        var config = AppConfiguration.Load(
            Path.Combine(dir!, "src", "RecurringTasksBot.FunctionApp"), "Production");
        var provider = AppConfiguration.ReadOpenRouter(config);
        Assert.Equal("parallel", provider.SearchEngine);
        Assert.Equal("fast", provider.SearchMode);
        Assert.Equal(8, provider.MaxSearches);
        Assert.Equal(5, provider.MaxResultsPerSearch);
        Assert.Equal(40, provider.MaxTotalResults);
        provider.Validate();
    }

    [Fact]
    public void ExecutionValidation_RejectsUnknownModeAndBadBudgets()
    {
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Execution() with { MemoryMode = "Sometimes" }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Execution() with { DeclaredContextTokens = 0 }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Execution() with { CompletionTokenBudget = 0 }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Execution() with { GenerationRetries = -1 }).Validate());
    }
}
