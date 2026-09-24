// Phase 2 verification with mocked providers: maximum-reasoning mapping,
// bounded search, full-length rich I/O, Unicode boundaries, formatting
// fallback, citations and splitting, retries, dedup, partial delivery,
// deletion races, failure notices, and pre-upgrade replay compatibility.
using System.Net;
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class OpenRouterMappingTests
{
    [Fact]
    public void Maximum_MapsToMaxEffort()
    {
        Assert.Equal("max", OpenRouterLlmExecutor.MapReasoningEffort("Maximum"));
    }

    [Fact]
    public void UnknownEffort_FailsClearly()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OpenRouterLlmExecutor.MapReasoningEffort("ultra"));
    }

    [Fact]
    public void Request_UsesMaxReasoning_SearchTool_Budget()
    {
        var prompt = new LlmPrompt("Summarize today's AI news.",
            new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 5, 9, 0, 4, DateTimeKind.Utc));
        var json = OpenRouterLlmExecutor.BuildRequestJson(prompt, TestLlm.Options());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("deepseek/deepseek-v4.1-flash", root.GetProperty("model").GetString());
        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("max", reasoning.GetProperty("effort").GetString());
        Assert.True(reasoning.GetProperty("exclude").GetBoolean());
        Assert.Equal(131072, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));

        var tools = root.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        var tool = tools[0];
        Assert.Equal("openrouter:web_search", tool.GetProperty("type").GetString());
        var parameters = tool.GetProperty("parameters");
        Assert.Equal("exa", parameters.GetProperty("engine").GetString());
        Assert.Equal(5, parameters.GetProperty("max_results").GetInt32());
        Assert.Equal(10, parameters.GetProperty("max_total_results").GetInt32());
        Assert.Equal(2, parameters.GetProperty("max_uses").GetInt32());
        Assert.Equal(2, root.GetProperty("max_tool_calls").GetInt32());

        Assert.DoesNotContain("plugins", json);
        Assert.DoesNotContain(":online", json);
        var system = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("2026-01-05 09:00:00Z", system);
        Assert.Contains("09:00:04", system);
    }

    [Fact]
    public void Request_NeverDisablesSearchSilently()
    {
        var opts = TestLlm.Options() with { SearchEnabled = false };
        Assert.Throws<InvalidOperationException>(() =>
            OpenRouterLlmExecutor.BuildRequestJson(
                new LlmPrompt("hi", DateTime.UtcNow, DateTime.UtcNow), opts));
    }

    [Fact]
    public void ParseResponse_ExtractsAnswer_Sources_Usage()
    {
        var body = """
            {"choices": [{"message": {
              "content": "Here is the news.",
              "reasoning_content": "secret chain of thought",
              "annotations": [
                {"type": "url_citation", "url_citation": {"url": "https://a.example/x", "title": "A"}},
                {"type": "other", "url_citation": {"url": "https://b.example/y", "title": "B"}},
                {"type": "url_citation", "url_citation": {"url": "ftp://c.example/z", "title": "C"}}
              ]}}],
             "usage": {"prompt_tokens": 12, "completion_tokens": 34}}
            """;
        var result = OpenRouterLlmExecutor.ParseResponse(body, TestLlm.Options());
        Assert.Equal("Here is the news.", result.AnswerText);
        Assert.DoesNotContain("secret chain", result.AnswerText);
        Assert.Single(result.Sources);
        Assert.Equal("https://a.example/x", result.Sources[0].Url);
        Assert.Equal(12, result.Usage.PromptTokens);
        Assert.Equal(34, result.Usage.CompletionTokens);
        Assert.True(result.SearchUsed);
    }

    [Fact]
    public void ParseResponse_CapsSources_AtMaxTotal()
    {
        var annotations = string.Join(",", Enumerable.Range(0, 15).Select(i =>
            $"{{\"type\": \"url_citation\", \"url_citation\": {{\"url\": \"https://e.example/{i}\", \"title\": \"T{i}\"}}}}"));
        var body = $"{{\"choices\": [{{\"message\": {{\"content\": \"ok\", \"annotations\": [{annotations}]}}}}]}}";
        var result = OpenRouterLlmExecutor.ParseResponse(body, TestLlm.Options());
        Assert.Equal(10, result.Sources.Count);
    }

    [Fact]
    public void ParseResponse_EmptyContent_IsExecutionFailure()
    {
        var ex = Assert.Throws<LlmExecutionException>(() =>
            OpenRouterLlmExecutor.ParseResponse(
                """{"choices": [{"message": {"content": "   "}}]}""", TestLlm.Options()));
        Assert.Equal(LlmFailureKind.EmptyResponse, ex.Kind);
    }
}

public sealed class OpenRouterHttpTests
{
    private sealed class ScriptHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices": [{"message": {"content": "ok"}}]}""",
                    Encoding.UTF8, "application/json"),
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Responder(request));
        }
    }

    private static OpenRouterLlmExecutor WithHandler(ScriptHandler h) =>
        new(new HttpClient(h), TestLlm.Options(), "test-key");

    [Fact]
    public async Task Success_PostsToChatCompletions_WithBearerAuth()
    {
        var h = new ScriptHandler();
        var ex = WithHandler(h);
        var result = await ex.ExecuteAsync(new LlmPrompt("hi", DateTime.UtcNow, DateTime.UtcNow));
        Assert.Equal("ok", result.AnswerText);
        Assert.EndsWith("/chat/completions", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", h.LastRequest.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task RateLimited_MapsTransient_WithRetryAfter()
    {
        var h = new ScriptHandler();
        h.Responder = _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "45");
            return response;
        };
        var ex = WithHandler(h);
        var thrown = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            ex.ExecuteAsync(new LlmPrompt("hi", DateTime.UtcNow, DateTime.UtcNow)));
        Assert.Equal(LlmFailureKind.Transient, thrown.Kind);
        Assert.Equal(TimeSpan.FromSeconds(45), thrown.RetryAfter);
    }

    [Fact]
    public async Task Unauthorized_IsPermanent_NoRetry()
    {
        var h = new ScriptHandler();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var ex = WithHandler(h);
        var thrown = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            ex.ExecuteAsync(new LlmPrompt("hi", DateTime.UtcNow, DateTime.UtcNow)));
        Assert.Equal(LlmFailureKind.Permanent, thrown.Kind);
        Assert.False(GenerationPolicy.ShouldRetry(thrown.Kind, 1));
    }
}

public sealed class GenerationPolicyTests
{
    [Theory]
    [InlineData(408, LlmFailureKind.Transient)]
    [InlineData(429, LlmFailureKind.Transient)]
    [InlineData(500, LlmFailureKind.Transient)]
    [InlineData(503, LlmFailureKind.Transient)]
    [InlineData(401, LlmFailureKind.Permanent)]
    [InlineData(402, LlmFailureKind.Permanent)]
    [InlineData(400, LlmFailureKind.Permanent)]
    public void HttpClassification(int status, LlmFailureKind expected)
    {
        Assert.Equal(expected, GenerationPolicy.ClassifyHttpStatus(status));
    }

    [Fact]
    public void Retries_AtMostTwice()
    {
        Assert.True(GenerationPolicy.ShouldRetry(LlmFailureKind.Transient, 1));
        Assert.True(GenerationPolicy.ShouldRetry(LlmFailureKind.Transient, 2));
        Assert.False(GenerationPolicy.ShouldRetry(LlmFailureKind.Transient, 3));
        Assert.False(GenerationPolicy.ShouldRetry(LlmFailureKind.EmptyResponse, 3));
        Assert.False(GenerationPolicy.ShouldRetry(LlmFailureKind.Permanent, 1));
    }

    [Fact]
    public void RetryDelay_Increases_HonoursRetryAfter()
    {
        var first = GenerationPolicy.RetryDelay(0);
        var second = GenerationPolicy.RetryDelay(1);
        Assert.True(first < second);
        Assert.Equal(TimeSpan.FromSeconds(120),
            GenerationPolicy.RetryDelay(0, TimeSpan.FromSeconds(120)));
        Assert.Equal(first, GenerationPolicy.RetryDelay(0, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Sanitize_RedactsKeyMaterial()
    {
        var summary = GenerationPolicy.Sanitize("failed with sk-or-v1-abcdef1234567890 tail");
        Assert.DoesNotContain("abcdef1234567890", summary);
        Assert.True(summary.Length <= 280);
    }

    [Fact]
    public void OptionsValidation_FailsClearly()
    {
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Options() with { Provider = "Other" }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Options() with { Model = "" }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Options() with { ReasoningEffort = "" }).Validate());
    }
}

public sealed class RichTextTests
{
    [Fact]
    public void PlainText_PassesThrough()
    {
        Assert.Equal("hello", RichNormalizer.NormalizePrompt("hello", null));
    }

    [Fact]
    public void RichMessage_PreservesOrder_Lists_Tables_Code_Links()
    {
        var rich = """
            {"blocks": [
              {"type": "paragraph", "text": "Morning briefing"},
              {"type": "list", "ordered": false, "items": ["alpha", "beta"]},
              {"type": "table", "rows": [[{"text": "a"}, {"text": "b"}]]},
              {"type": "code", "language": "python", "code": "print(1)"},
              {"type": "link", "text": "source", "url": "https://s.example/x"}
            ]}
            """;
        var text = RichNormalizer.NormalizePrompt(null, rich)!;
        Assert.Contains("Morning briefing", text);
        Assert.Contains("- alpha", text);
        Assert.Contains("| a | b |", text);
        Assert.Contains("```python", text);
        Assert.Contains("source (https://s.example/x)", text);
        Assert.True(text.IndexOf("Morning", StringComparison.Ordinal) <
            text.IndexOf("source", StringComparison.Ordinal));
    }

    [Fact]
    public void RichMessage_InvalidJson_ReturnsNull()
    {
        Assert.Null(RichNormalizer.NormalizePrompt(null, "{nope"));
    }

    [Fact]
    public void Split_NeverSeparatesSurrogatePairs()
    {
        var text = "a" + string.Concat(Enumerable.Repeat("🌍", 100)) + "b";
        var parts = TextLimits.SplitByChars(text, 10);
        Assert.All(parts, p => Assert.True(TextLimits.CountChars(p) <= 10));
        Assert.Equal(text, string.Concat(parts));
    }

    [Fact]
    public void Validator_StripsUnsupported_KeepsAnswer()
    {
        var safe = RichValidator.ToSafeHtml(
            "<script>alert(1)</script>Hello **world** [x](javascript:alert(1))");
        Assert.DoesNotContain("<script>", safe);
        Assert.DoesNotContain("javascript:", safe);
        Assert.Contains("Hello", safe);
        Assert.Contains("world", safe);
    }

    [Fact]
    public void Validator_BalancesSplitTags()
    {
        var safe = RichValidator.SanitizeHtml("<b>unclosed and <i>more");
        Assert.EndsWith("</i></b>", safe);
        Assert.DoesNotContain("</b></b>", safe);
    }

    [Fact]
    public void Composer_IdentifiesOperation_AndSplitsAtLimit()
    {
        var answer = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"line {i}"));
        var parts = AnswerComposer.Compose("op9",
            new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 5, 9, 0, 4, DateTimeKind.Utc),
            answer, [new LlmSource("S", "https://s.example/")]);
        Assert.All(parts, p => Assert.True(RichMessageParts.CountRenderedChars(p) <= 32768));
        Assert.Contains("op9", parts[0]);
        Assert.Contains("2026-01-05 09:00:00Z", parts[0]);
        Assert.Contains("https://s.example/", string.Concat(parts));
    }

    [Fact]
    public void Composer_PreservesFullAnswer_SplittingInsteadOfTruncating()
    {
        // A valid full-length answer keeps every character: headers and
        // markup never eat into the accepted answer, which splits instead.
        var answer = new string('z', 32768);
        var parts = AnswerComposer.Compose("op1", DateTime.UtcNow, DateTime.UtcNow,
            answer, [new LlmSource("S", "https://s.example/")]);
        Assert.All(parts, p => Assert.True(RichMessageParts.CountRenderedChars(p) <= 32768));
        var rendered = string.Concat(parts.Select(p =>
            System.Text.RegularExpressions.Regex.Replace(p, @"<[^>]+>", string.Empty)));
        var unescaped = rendered.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        Assert.Contains(answer, unescaped);
        Assert.DoesNotContain("truncated", string.Concat(parts));
    }

    [Fact]
    public void Composer_CitesAtMostTenSources()
    {
        var sources = Enumerable.Range(0, 15)
            .Select(i => new LlmSource($"T{i}", $"https://e.example/{i}")).ToList();
        var parts = AnswerComposer.Compose("op1", DateTime.UtcNow, DateTime.UtcNow, "hi", sources);
        var joined = string.Concat(parts);
        Assert.Contains("https://e.example/9", joined);
        Assert.DoesNotContain("https://e.example/10", joined);
    }

    [Fact]
    public void FailureNotice_IdentifiesOccurrence_WithoutProviderErrors()
    {
        var notice = AnswerComposer.FailureNotice("op7",
            new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc));
        Assert.Contains("op7", notice);
        Assert.Contains("future runs remain scheduled", notice);
    }
}

public sealed class ReplyCreationTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

    private static (UpdateProcessor P, FakeOperationStore Ops, FakeReceiptStore R,
        FakeOrchestrations O, FakeTelegramSender T) New()
    {
        var ops = new FakeOperationStore();
        var r = new FakeReceiptStore();
        var o = new FakeOrchestrations();
        var t = new FakeTelegramSender();
        return (new UpdateProcessor(ops, r, o, t), ops, r, o, t);
    }

    [Fact]
    public async Task ReplyWithScheduleOnly_UsesRepliedText()
    {
        var (p, ops, _, o, t) = New();
        var update = new IncomingUpdate(50, 42, 777, TelegramUpdateKind.Message,
            "/create 0 0 9 * * *", "Long prompt from the replied message.", 42);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(o.StartedInstances);
        var stored = (await ops.ListOwnedAsync("42")).Single();
        Assert.Equal("Long prompt from the replied message.", stored.Text);
        Assert.Contains("created", t.Sent[0].Text);
    }

    [Fact]
    public async Task ReplyFromAnotherUser_IsIgnored()
    {
        var (p, _, _, o, t) = New();
        var update = new IncomingUpdate(51, 42, 777, TelegramUpdateKind.Message,
            "/create 0 0 9 * * *", "Someone else's text.", 99);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(o.StartedInstances);
    }

    [Fact]
    public async Task OversizedReply_IsRejected_NotTruncated()
    {
        var (p, _, _, o, t) = New();
        var update = new IncomingUpdate(52, 42, 777, TelegramUpdateKind.Message,
            "/create 0 0 9 * * *", new string('q', 40000), 42);
        var result = await p.ProcessAsync(true, update, Now);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(o.StartedInstances);
        Assert.Contains("32768", t.Sent[0].Text);
    }

    [Fact]
    public async Task Help_MentionsExternalService()
    {
        Assert.Contains("external LLM", ListFormatter.HelpMessage);
    }
}

public sealed class RichNormalizerTypeTests
{
    [Fact]
    public void BlockType_NeverLeaksIntoPrompt_CommandDispatchIntact()
    {
        var rich = """{"type": "paragraph", "text": "/create 0 0 9 * * * Water the plants"}""";
        Assert.Equal("/create 0 0 9 * * * Water the plants",
            RichNormalizer.NormalizePrompt(null, rich));
    }

    [Fact]
    public void InlineSegments_PreserveOrderAndSpacing()
    {
        var rich = """{"type": "paragraph", "segments": ["Hello", " ", {"text": "world", "bold": true}, "!"]}""";
        Assert.Equal("Hello world!",
            RichNormalizer.NormalizePrompt(null, rich));
    }

    [Fact]
    public void UnknownType_SkipsDiscriminator_KeepsContent()
    {
        var rich = """{"type": "callout", "text": "note this"}""";
        Assert.Equal("note this", RichNormalizer.NormalizePrompt(null, rich));
    }
}

public sealed class ReceiptBoundTests
{
    [Fact]
    public void FullLengthCreate_StoresBoundedReceiptCommand()
    {
        var full = "/create 0 0 9 * * * " + new string('x', 32768);
        var bounded = UpdateReceipts.BoundCommand(full);
        Assert.StartsWith("/create", bounded);
        Assert.True(TextLimits.CountChars(bounded) <= UpdateReceipts.MaxCommandChars);
        Assert.True(Encoding.UTF8.GetByteCount(bounded) <= 65536);
    }

    [Fact]
    public void BoundCommand_PreservesShortCommands_Verbatim()
    {
        Assert.Equal("/list", UpdateReceipts.BoundCommand("/list"));
    }
}

public sealed class LlmConfigTests
{
    private static Func<string, string?> Lookup(IReadOnlyDictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var value) ? value : null;

    [Fact]
    public void JsonValues_TakeEffect()
    {
        var options = LlmConfig.Read(Lookup(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:Model"] = "deepseek/deepseek-v3.2",
            ["RecurringTasksBot:Llm:MaxResultsPerSearch"] = "3",
            ["RecurringTasksBot:Llm:SystemInstruction"] = "Custom instruction.",
        }));
        Assert.Equal("deepseek/deepseek-v3.2", options.Model);
        Assert.Equal(3, options.MaxResultsPerSearch);
        Assert.Equal("Custom instruction.", options.SystemInstruction);
        options.Validate();
    }

    [Fact]
    public void MissingValues_FallBackToDefaults()
    {
        var options = LlmConfig.Read(Lookup(new Dictionary<string, string?>()));
        Assert.Equal("deepseek/deepseek-v4.1-flash", options.Model);
        Assert.Equal(131072, options.CompletionTokenBudget);
        Assert.Equal(8, options.MaxSearches);
        Assert.Equal(5, options.MaxResultsPerSearch);
        Assert.Equal(40, options.MaxTotalResults);
    }

    [Fact]
    public void EnvironmentValues_OverrideJson()
    {
        var options = LlmConfig.Read(Lookup(new Dictionary<string, string?>
        {
            ["RecurringTasksBot:Llm:Model"] = "overridden-model",
        }));
        Assert.Equal("overridden-model", options.Model);
    }
}

public sealed class RichPayloadTests
{
    [Fact]
    public void SendRichMessage_UsesRichMessageObject()
    {
        var json = TelegramRichMessage.BuildRequestJson(777, "<b>hi</b>");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(777, root.GetProperty("chat_id").GetInt64());
        Assert.False(root.TryGetProperty("text", out _));
        var rich = root.GetProperty("rich_message");
        Assert.Equal("<b>hi</b>", rich.GetProperty("html").GetString());
        Assert.Single(rich.EnumerateObject());
    }
}

public sealed class StorageChunkingTests
{
    [Fact]
    public void LongContent_RoundTrips_ThroughBoundedChunks()
    {
        var text = string.Concat(Enumerable.Repeat("🌍ab", 10000)); // 30k chars, ~50KB UTF-8
        var chunks = TextLimits.ToStorageChunks(text);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= 64000));
        Assert.Equal(text, TextLimits.JoinStorageChunks(chunks));
    }

    [Fact]
    public void StaleGeneratingClaim_IsRecoverable()
    {
        var stale = new DeliveryReceipt("o", "op", DateTime.UtcNow, "generating",
            0, null, null, UpdatedUtc: DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        Assert.True(OccurrenceExecution.IsClaimStale(stale, DateTimeOffset.UtcNow));
        var fresh = stale with { UpdatedUtc = DateTimeOffset.UtcNow };
        Assert.False(OccurrenceExecution.IsClaimStale(fresh, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PreUpgradeState_Deserializes()
    {
        // Phase-one orchestration state and receipts carry no new fields.
        var stateJson = """{"InstanceId":"recurring-x","OperationId":"x","OwnerId":"1","CronExpression":"0 0 9 * * *","NextScheduledUtc":"2026-01-06T09:00:00Z","OccurrenceIndex":3}""";
        var state = JsonSerializer.Deserialize<RecurringTasksBot.Core.RecurrenceState>(stateJson);
        Assert.NotNull(state);
        Assert.Equal(3, state!.OccurrenceIndex);

        // Old 7-argument receipt construction still compiles and behaves.
        var receipt = new DeliveryReceipt("o", "op", DateTime.UtcNow, "sent", 1, null, 5L);
        Assert.Equal(0, receipt.GenerationAttempts);
        Assert.Equal(0, receipt.TotalParts);
    }
}
