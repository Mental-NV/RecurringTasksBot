// Configuration loading: the shared loader resolves common JSON, the
// environment profile, then environment variables; typed readers apply
// code defaults only for absent keys and fail on malformed or explicit
// blank values. Task defaults derive from the shared Memory, reasoning,
// and search settings; provider connections live in named profiles
// selected by Llm:ActiveProfile. Only the selected profile's credential
// reference is ever resolved.
using System.Text.Json;
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

    private static Dictionary<string, string?> Merge(
        params Dictionary<string, string?>[] parts)
    {
        var merged = new Dictionary<string, string?>();
        foreach (var part in parts)
            foreach (var (key, value) in part)
                merged[key] = value;
        return merged;
    }

    private static Dictionary<string, string?> OpenRouterProfile() => new()
    {
        ["RecurringTasksBot:Llm:ActiveProfile"] = "OpenRouter",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:Provider"] = "OpenRouter",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:Model"] = "deepseek/deepseek-v4.1-flash",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:ApiKey"] = "%TEST_LLM_KEY%",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:SearchEngine"] = "parallel",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:SearchMode"] = "fast",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:MaxSearches"] = "8",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:MaxResultsPerSearch"] = "5",
        ["RecurringTasksBot:Llm:Profiles:OpenRouter:MaxTotalResults"] = "40",
    };

    private static Dictionary<string, string?> DeepSeekProfile() => new()
    {
        ["RecurringTasksBot:Llm:ActiveProfile"] = "DeepSeek",
        ["RecurringTasksBot:Llm:Profiles:DeepSeek:Provider"] = "DeepSeek",
        ["RecurringTasksBot:Llm:Profiles:DeepSeek:BaseUrl"] = "https://api.deepseek.com/anthropic",
        ["RecurringTasksBot:Llm:Profiles:DeepSeek:Model"] = "deepseek-flash",
        ["RecurringTasksBot:Llm:Profiles:DeepSeek:ApiKey"] = "%TEST_DEEPSEEK_KEY%",
        ["RecurringTasksBot:Llm:Profiles:DeepSeek:MaxSearches"] = "8",
        ["RecurringTasksBot:Llm:Profiles:DeepSeek:MaxTotalResults"] = "40",
    };

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
        Assert.True(execution.SearchEnabled);
        execution.Validate();

        // Shared defaults derive without any profile; selecting one
        // requires an explicit selector and a named profile section.
        var defaults = AppConfiguration.DeriveTaskDefaults(
            Config(new Dictionary<string, string?>()));
        Assert.Equal(TaskMemoryModes.IncludePreviousMessage, defaults.MemoryMode);
        Assert.Equal(TaskReasoningEfforts.Max, defaults.ReasoningEffort);
        Assert.True(defaults.WebSearch);
        Assert.StartsWith("defaults-v2:", defaults.Revision);
        Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(new Dictionary<string, string?>())));
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
        var provider = AppConfiguration.ReadOpenRouter(Config(Merge(OpenRouterProfile(),
            new Dictionary<string, string?>
            {
                ["RecurringTasksBot:Llm:Profiles:OpenRouter:SearchEngine"] = "exa",
                ["RecurringTasksBot:Llm:Profiles:OpenRouter:SearchMode"] = "turbo",
            })));
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

    [Fact]
    public void SearchDisabled_AcceptsValidation_OmitsTools()
    {
        var provider = TestLlm.Provider() with { SearchEnabled = false };
        provider.Validate();
        var json = OpenRouterRequestBuilder.BuildRequestJson(
            provider, TestLlm.Execution(), [new ChatMessage("user", "hi")]);
        Assert.DoesNotContain("tools", json);
        Assert.DoesNotContain("max_tool_calls", json);
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
        var config = Config(Merge(OpenRouterProfile(),
            new Dictionary<string, string?>
            {
                ["RecurringTasksBot:Llm:Profiles:OpenRouter:MaxSearches"] = "many",
            }));
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadOpenRouter(config));
    }

    [Fact]
    public void MalformedSearchFlag_FailsInsteadOfDefaulting()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:SearchEnabled"] = "yes",
        });
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadExecution(config));
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.DeriveTaskDefaults(config));
    }

    [Theory]
    [InlineData("RecurringTasksBot:Memory:Mode", "")]
    [InlineData("RecurringTasksBot:Llm:ReasoningEffort", "")]
    [InlineData("RecurringTasksBot:Llm:SearchEnabled", "")]
    [InlineData("RecurringTasksBot:Llm:ActiveProfile", "")]
    public void ExplicitBlankValues_FailInsteadOfDefaulting(string key, string value)
    {
        var config = Config(Merge(OpenRouterProfile(),
            new Dictionary<string, string?> { [key] = value }));
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadSelectedLlm(config));
    }

    [Fact]
    public void ExplicitBlankTimeout_FailsInsteadOfDefaulting()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:RequestTimeoutSeconds"] = "",
        });
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.ReadExecution(config));
    }

    [Theory]
    [InlineData("Sometimes")]
    [InlineData("previoussuccessfulreply")]
    public void UnknownMemoryMode_Fails(string mode)
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Memory:Mode"] = mode,
        });
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.DeriveTaskDefaults(config));
    }

    [Theory]
    [InlineData("ultra")]
    [InlineData("medium")]
    public void UnknownReasoningEffort_Fails(string effort)
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:ReasoningEffort"] = effort,
        });
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.DeriveTaskDefaults(config));
    }

    [Theory]
    [InlineData("Maximum", "max")]
    [InlineData("MAXIMUM", "max")]
    [InlineData("low", "low")]
    [InlineData("med", "med")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "max")]
    public void ReasoningEffort_NormalizesToTaskToken(string configured, string expected)
    {
        var defaults = AppConfiguration.DeriveTaskDefaults(Config(
            new Dictionary<string, string?>
            {
                ["RecurringTasksBot:Llm:ReasoningEffort"] = configured,
            }));
        Assert.Equal(expected, defaults.ReasoningEffort);
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
        Assert.Equal("RecurringTaskDataV5", storage.TableName);
        var telegram = AppConfiguration.ReadTelegram(config);
        Assert.Equal("token", telegram.BotToken);
        Assert.Equal("secret", telegram.WebhookSecret);
    }

    [Theory]
    [InlineData("RecurringTasksBot:TaskDefaults:MemoryMode", "None")]
    [InlineData("RecurringTasksBot:TaskDefaults:Revision", "1")]
    [InlineData("RecurringTasksBot:Llm:Provider", "OpenRouter")]
    [InlineData("RecurringTasksBot:Llm:BaseUrl", "https://openrouter.ai/api/v1")]
    [InlineData("RecurringTasksBot:Llm:Model", "deepseek/deepseek-v4.1-flash")]
    [InlineData("RecurringTasksBot:Llm:SearchEngine", "parallel")]
    [InlineData("RecurringTasksBot:Llm:SearchMode", "fast")]
    [InlineData("RecurringTasksBot:Llm:MaxSearches", "8")]
    [InlineData("RecurringTasksBot:Llm:MaxResultsPerSearch", "5")]
    [InlineData("RecurringTasksBot:Llm:MaxTotalResults", "40")]
    public void ObsoletePaths_FailWithMigrationError(string key, string value)
    {
        var config = Config(Merge(OpenRouterProfile(),
            new Dictionary<string, string?> { [key] = value }));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(config));
        // Path-only migration error: names the obsolete section or key.
        var section = key.StartsWith("RecurringTasksBot:TaskDefaults", StringComparison.Ordinal)
            ? "RecurringTasksBot:TaskDefaults" : key;
        Assert.Contains(section, ex.Message);
        Assert.Throws<InvalidOperationException>(() => AppConfiguration.DeriveTaskDefaults(config));
    }

    [Theory]
    [InlineData("RecurringTasksBot:Llm:OpenRouter:ApiKey", "opaque")]
    [InlineData("RecurringTasksBot:Llm:DeepSeek:ApiKey", "opaque")]
    public void CredentialSourcePaths_AreNotRejectedAsObsolete(string key, string value)
    {
        var selected = AppConfiguration.ReadSelectedLlm(
            Config(Merge(OpenRouterProfile(),
                new Dictionary<string, string?> { [key] = value })));
        Assert.Equal("OpenRouter", selected.ActiveProfile);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownBlankOrMissingSelector_FailsWithoutFallback(string? selector)
    {
        var profile = OpenRouterProfile();
        if (selector is null)
            profile.Remove("RecurringTasksBot:Llm:ActiveProfile");
        else
            profile["RecurringTasksBot:Llm:ActiveProfile"] = selector;
        Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(profile)));
    }

    [Fact]
    public void SelectorCase_MatchesProfileCaseInsensitively()
    {
        var profile = OpenRouterProfile();
        profile["RecurringTasksBot:Llm:ActiveProfile"] = "openrouter";
        var selected = AppConfiguration.ReadSelectedLlm(Config(profile));
        Assert.NotNull(selected.OpenRouter);
    }

    [Fact]
    public void BothProfiles_SwitchTogetherOnSelectorOnly()
    {
        var open = Merge(OpenRouterProfile(), DeepSeekProfile());
        open["RecurringTasksBot:Llm:ActiveProfile"] = "OpenRouter";
        var openRouter = AppConfiguration.ReadSelectedLlm(Config(open));
        Assert.Equal("OpenRouter", openRouter.Provider);
        Assert.Equal("https://openrouter.ai/api/v1", openRouter.OpenRouter!.BaseUrl);
        Assert.Equal("deepseek/deepseek-v4.1-flash", openRouter.OpenRouter.Model);
        Assert.Equal("deepseek/deepseek-v4.1-flash", openRouter.Model);

        var deep = Merge(OpenRouterProfile(), DeepSeekProfile());
        deep["RecurringTasksBot:Llm:ActiveProfile"] = "DeepSeek";
        var deepSeek = AppConfiguration.ReadSelectedLlm(Config(deep));
        Assert.Equal("DeepSeek", deepSeek.Provider);
        Assert.Equal("https://api.deepseek.com/anthropic", deepSeek.DeepSeek!.BaseUrl);
        Assert.Equal("deepseek-flash", deepSeek.DeepSeek.Model);
        Assert.Equal("deepseek-flash", deepSeek.Model);

        var adapter = LlmAdapterFactory.CreateSelected(
            new HttpClient(), TestLlm.Execution(), deepSeek, "deepseek-key");
        Assert.IsType<DeepSeekLlmExecutor>(adapter.Executor);
        Assert.Equal("DeepSeek", adapter.ProviderName);
        Assert.Equal("deepseek-flash", adapter.ModelName);
    }

    [Fact]
    public void InactiveProfile_NeedsNoCredential()
    {
        // The DeepSeek profile lacks its ApiKey entirely; selecting
        // OpenRouter still starts.
        var profile = OpenRouterProfile();
        profile["RecurringTasksBot:Llm:Profiles:DeepSeek:Provider"] = "DeepSeek";
        var selected = AppConfiguration.ReadSelectedLlm(Config(profile));
        Assert.Equal("OpenRouter", selected.ActiveProfile);
        var key = AppConfiguration.ResolveSelectedApiKey(
            Config(profile), selected, _ => "openrouter-key");
        Assert.Equal("openrouter-key", key);
    }

    [Fact]
    public void SelectedProfileWithoutApiKeyReference_FailsStructurally()
    {
        var profile = OpenRouterProfile();
        profile.Remove("RecurringTasksBot:Llm:Profiles:OpenRouter:ApiKey");
        Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(profile)));
    }

    [Fact]
    public void EnvironmentProfile_OverlaysCommon()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                """{"RecurringTasksBot":{"Llm":{"ActiveProfile":"OpenRouter","ReasoningEffort":"Maximum","Profiles":{"OpenRouter":{"Provider":"OpenRouter","BaseUrl":"https://openrouter.ai/api/v1","Model":"common-model","ApiKey":"%TEST_LLM_KEY%","MaxSearches":3},"DeepSeek":{"Provider":"DeepSeek","BaseUrl":"https://api.deepseek.com/anthropic","Model":"deepseek-flash","ApiKey":"%TEST_DEEPSEEK_KEY%"}}}}}""");
            File.WriteAllText(Path.Combine(dir, "appsettings.Staging.json"),
                """{"RecurringTasksBot":{"Llm":{"ReasoningEffort":"low","Profiles":{"OpenRouter":{"MaxSearches":9}}}}}""");
            var config = AppConfiguration.Load(dir, "Staging");
            var selected = AppConfiguration.ReadSelectedLlm(config);
            Assert.Equal("common-model", selected.OpenRouter!.Model);
            Assert.Equal(9, selected.OpenRouter.MaxSearches);
            Assert.Equal("low", selected.Defaults.ReasoningEffort);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnvironmentVariable_OverridesSelectorAndProfileFields()
    {
        const string selectorVar = "RecurringTasksBot__Llm__ActiveProfile";
        const string searchesVar = "RecurringTasksBot__Llm__Profiles__DeepSeek__MaxSearches";
        var previousSelector = Environment.GetEnvironmentVariable(selectorVar);
        var previousSearches = Environment.GetEnvironmentVariable(searchesVar);
        Environment.SetEnvironmentVariable(selectorVar, "DeepSeek");
        Environment.SetEnvironmentVariable(searchesVar, "5");
        try
        {
            // Environment variables are the last source, like the host loader.
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(Merge(OpenRouterProfile(), DeepSeekProfile())!)
                .AddEnvironmentVariables()
                .Build();
            var selected = AppConfiguration.ReadSelectedLlm(config);
            Assert.Equal("DeepSeek", selected.ActiveProfile);
            Assert.Equal(5, selected.DeepSeek!.MaxSearches);
        }
        finally
        {
            Environment.SetEnvironmentVariable(selectorVar, previousSelector);
            Environment.SetEnvironmentVariable(searchesVar, previousSearches);
        }
    }

    [Fact]
    public void CheckedInDefaults_SelectValidProfileWithDerivedDefaults()
    {
        // Selector-agnostic: the checked-in selector may name either
        // shipped profile. Both named sections are validated; only the
        // shared derived defaults are pinned.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RecurringTasksBot.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.True(dir is not null, "Repository root with RecurringTasksBot.sln was not found.");
        var config = AppConfiguration.Load(
            Path.Combine(dir!, "src", "RecurringTasksBot.FunctionApp"), "Production");
        var selected = AppConfiguration.ReadSelectedLlm(config);
        Assert.Contains(selected.ActiveProfile, new[] { "OpenRouter", "DeepSeek" });
        var openRouter = AppConfiguration.ReadOpenRouter(config);
        Assert.Equal("parallel", openRouter.SearchEngine);
        Assert.Equal("fast", openRouter.SearchMode);
        Assert.Equal(8, openRouter.MaxSearches);
        Assert.Equal(5, openRouter.MaxResultsPerSearch);
        Assert.Equal(40, openRouter.MaxTotalResults);
        openRouter.Validate();
        var deepSeek = AppConfiguration.ReadDeepSeek(config);
        deepSeek.Validate();
        Assert.Equal(TaskMemoryModes.IncludePreviousMessage, selected.Defaults.MemoryMode);
        Assert.Equal(TaskReasoningEfforts.Max, selected.Defaults.ReasoningEffort);
        Assert.True(selected.Defaults.WebSearch);
        selected.Defaults.Validate();
        // Exactly one authoritative path per shared default: no
        // TaskDefaults section and no root provider connection fields.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!,
            "src", "RecurringTasksBot.FunctionApp", "appsettings.json")));
        var bot = doc.RootElement.GetProperty("RecurringTasksBot");
        Assert.False(bot.TryGetProperty("TaskDefaults", out _));
        var llm = bot.GetProperty("Llm");
        foreach (var obsolete in new[] { "Provider", "BaseUrl", "Model",
            "SearchEngine", "SearchMode", "MaxSearches",
            "MaxResultsPerSearch", "MaxTotalResults" })
        {
            Assert.False(llm.TryGetProperty(obsolete, out _));
        }
        Assert.True(llm.TryGetProperty("Profiles", out _));
    }

    [Fact]
    public void ApiKeyReference_ResolvesExactVariableNameOnce()
    {
        var merged = Merge(OpenRouterProfile(), DeepSeekProfile());
        merged["RecurringTasksBot:Llm:Profiles:DeepSeek:ApiKey"] =
            "%RecurringTasksBot__Llm__DeepSeek__ApiKey%";
        var config = Config(merged);
        var selected = AppConfiguration.ReadSelectedLlm(config);
        Assert.Equal("DeepSeek", selected.ActiveProfile);
        var lookups = new List<string>();
        var key = AppConfiguration.ResolveSelectedApiKey(config, selected, name =>
        {
            lookups.Add(name);
            return "resolved-deepseek-key";
        });
        Assert.Equal("resolved-deepseek-key", key);
        // Exactly the requested spelling: double underscores throughout.
        Assert.Single(lookups);
        Assert.Equal("RecurringTasksBot__Llm__DeepSeek__ApiKey", lookups[0]);
    }

    [Fact]
    public void ApiKeyReference_PreservesUnderscoresVerbatim()
    {
        var profile = DeepSeekProfile();
        profile["RecurringTasksBot:Llm:Profiles:DeepSeek:ApiKey"] = "%CUSTOM__DOUBLE__KEY%";
        var config = Config(profile);
        var selected = AppConfiguration.ReadSelectedLlm(config);
        string? seen = null;
        AppConfiguration.ResolveSelectedApiKey(config, selected, name =>
        {
            seen = name;
            return "k";
        });
        Assert.Equal("CUSTOM__DOUBLE__KEY", seen);
    }

    [Theory]
    [InlineData("sk-live-literal-key")]
    [InlineData("")]
    [InlineData("prefix-%TEST_LLM_KEY%")]
    [InlineData("%TEST_LLM_KEY%-suffix")]
    [InlineData("%TEST_LLM_KEY")]
    [InlineData("TEST_LLM_KEY%")]
    [InlineData("%9BAD%")]
    [InlineData("%%")]
    public void MalformedApiKeyReference_FailsStructurally(string apiKey)
    {
        var profile = OpenRouterProfile();
        profile["RecurringTasksBot:Llm:Profiles:OpenRouter:ApiKey"] = apiKey;
        Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(profile)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankCredential_FailsWithoutLeaking(string? value)
    {
        var config = Config(OpenRouterProfile());
        var selected = AppConfiguration.ReadSelectedLlm(config);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ResolveSelectedApiKey(config, selected, _ => value));
        Assert.Contains("TEST_LLM_KEY", ex.Message);
        Assert.DoesNotContain("resolved", ex.Message);
    }

    [Fact]
    public void ResolvedCredential_PassesThroughUnchanged()
    {
        var config = Config(OpenRouterProfile());
        var selected = AppConfiguration.ReadSelectedLlm(config);
        var calls = 0;
        var key = AppConfiguration.ResolveSelectedApiKey(config, selected, _ =>
        {
            calls++;
            return "100%discount%";
        });
        Assert.Equal("100%discount%", key);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void OldOpenRouterKey_CannotSatisfyDeepSeek()
    {
        var config = Config(Merge(OpenRouterProfile(), DeepSeekProfile()));
        var selected = AppConfiguration.ReadSelectedLlm(config);
        Assert.Equal("DeepSeek", selected.ActiveProfile);
        var seen = new List<string>();
        AppConfiguration.ResolveSelectedApiKey(config, selected, name =>
        {
            seen.Add(name);
            return "openrouter-key-value";
        });
        Assert.DoesNotContain("RecurringTasksBot__Llm__OpenRouter__ApiKey", seen);
    }

    [Fact]
    public void CheckedInProfiles_ReferenceRenamedCredentialVariables()
    {
        // The shipped JSON must reference the exact renamed variables:
        // resolving them looks up these names verbatim.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RecurringTasksBot.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.True(dir is not null, "Repository root with RecurringTasksBot.sln was not found.");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!,
            "src", "RecurringTasksBot.FunctionApp", "appsettings.json")));
        var profiles = doc.RootElement.GetProperty("RecurringTasksBot").GetProperty("Llm").GetProperty("Profiles");
        Assert.Equal("%RecurringTasksBot__Llm__OpenRouter__ApiKey%",
            profiles.GetProperty("OpenRouter").GetProperty("ApiKey").GetString());
        Assert.Equal("%RecurringTasksBot__Llm__DeepSeek__ApiKey%",
            profiles.GetProperty("DeepSeek").GetProperty("ApiKey").GetString());
    }

    [Fact]
    public void DefaultsRevision_IsDeterministicAndSensitive()
    {
        var first = AppConfiguration.DeriveTaskDefaults(Config(new Dictionary<string, string?>()));
        var second = AppConfiguration.DeriveTaskDefaults(Config(new Dictionary<string, string?>()));
        Assert.Equal(first.Revision, second.Revision);
        Assert.StartsWith("defaults-v2:", first.Revision);
        var hex = first.Revision["defaults-v2:".Length..];
        Assert.Equal(64, hex.Length);
        Assert.Matches("^[0-9a-f]{64}$", hex);

        foreach (var change in new Dictionary<string, string?>[]
        {
            new() { ["RecurringTasksBot:Memory:Mode"] = "None" },
            new() { ["RecurringTasksBot:Llm:ReasoningEffort"] = "low" },
            new() { ["RecurringTasksBot:Llm:SearchEnabled"] = "false" },
        })
        {
            var altered = AppConfiguration.DeriveTaskDefaults(Config(change));
            Assert.NotEqual(first.Revision, altered.Revision);
        }
    }

    [Fact]
    public void DefaultsRevision_ExcludesProfileAndSecrets()
    {
        var open = AppConfiguration.DeriveTaskDefaults(Config(OpenRouterProfile()));
        var deep = AppConfiguration.DeriveTaskDefaults(Config(Merge(OpenRouterProfile(), DeepSeekProfile())));
        Assert.Equal(open.Revision, deep.Revision);
    }

    [Theory]
    [InlineData("ReasoningEffort", "Maximum")]
    [InlineData("SearchEnabled", "true")]
    public void ProfileSharedCopies_AreRejected(string key, string value)
    {
        var profile = OpenRouterProfile();
        profile[$"RecurringTasksBot:Llm:Profiles:OpenRouter:{key}"] = value;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(profile)));
        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData("SearchEngine", "parallel")]
    [InlineData("SearchMode", "fast")]
    [InlineData("MaxResultsPerSearch", "5")]
    public void DeepSeek_OpenRouterOnlyOptions_AreRejected(string key, string value)
    {
        var profile = DeepSeekProfile();
        profile[$"RecurringTasksBot:Llm:Profiles:DeepSeek:{key}"] = value;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(profile)));
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void DeepSeek_RequiresHttpsBaseAndModel()
    {
        var http = DeepSeekProfile();
        http["RecurringTasksBot:Llm:Profiles:DeepSeek:BaseUrl"] = "http://api.deepseek.com/anthropic";
        Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(http)));

        var noModel = DeepSeekProfile();
        noModel.Remove("RecurringTasksBot:Llm:Profiles:DeepSeek:Model");
        Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(noModel)));

        var badProvider = DeepSeekProfile();
        badProvider["RecurringTasksBot:Llm:Profiles:DeepSeek:Provider"] = "Anthropic";
        Assert.Throws<InvalidOperationException>(() =>
            AppConfiguration.ReadSelectedLlm(Config(badProvider)));
    }

    [Fact]
    public void SearchLimits_ValidatedEvenWhenSearchDisabled()
    {
        var bad = Merge(OpenRouterProfile(), new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:SearchEnabled"] = "false",
            ["RecurringTasksBot:Llm:Profiles:OpenRouter:MaxSearches"] = "0",
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            var options = AppConfiguration.ReadOpenRouter(Config(bad));
            options.Validate();
        });

        var ok = Config(Merge(OpenRouterProfile(), new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:SearchEnabled"] = "false",
        }));
        var selected = AppConfiguration.ReadSelectedLlm(ok);
        Assert.False(selected.OpenRouter!.SearchEnabled);
        Assert.False(selected.Defaults.WebSearch);
        selected.OpenRouter.Validate();
    }

    [Fact]
    public void ExplicitTaskOverride_WinsOverDisabledSharedSearch()
    {
        var config = Config(Merge(OpenRouterProfile(), new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:SearchEnabled"] = "false",
        }));
        var provider = AppConfiguration.ReadOpenRouter(config);
        var json = OpenRouterRequestBuilder.BuildRequestJson(
            provider, TestLlm.Execution(),
            [new ChatMessage("user", "hi")],
            new LlmRequest([new ChatMessage("user", "hi")], 131072, null, true));
        Assert.Contains("openrouter:web_search", json);
    }
}
