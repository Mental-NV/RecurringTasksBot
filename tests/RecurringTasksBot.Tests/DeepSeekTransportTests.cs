// Direct DeepSeek transport tests: request/response mapping against the
// embedded Anthropic-compatible fixtures, indexed SSE assembly,
// final-segment selection, citation metadata, and the adapter failure
// policy. Fake transports only: no live requests.
using System.Net;
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Llm;

namespace RecurringTasksBot.Tests;

public sealed class DeepSeekTransportTests
{
    private static readonly IReadOnlyList<ChatMessage> Prompt = new[]
    {
        new ChatMessage("system", "instruction"),
        new ChatMessage("user", "Summarize today's AI news."),
    };

    private static readonly IReadOnlyList<ChatMessage> MemoryPrompt = new[]
    {
        new ChatMessage("system", "instruction"),
        new ChatMessage("user", "archived metadata"),
        new ChatMessage("assistant", "Previous result"),
        new ChatMessage("user", "current envelope"),
    };

    [Theory]
    [InlineData("low", "low")]
    [InlineData("med", "high")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "high")]
    [InlineData("max", "max")]
    [InlineData("Maximum", "max")]
    public void Effort_MapsToDocumentedScale(string configured, string expected)
    {
        Assert.Equal(expected, DeepSeekLlmExecutor.MapReasoningEffort(configured));
    }

    [Fact]
    public void UnknownEffort_FailsClearly()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DeepSeekLlmExecutor.MapReasoningEffort("ultra"));
    }

    [Fact]
    public void Request_MovesSystemMovesRoles_MapsBudgets()
    {
        var json = DeepSeekRequestBuilder.BuildRequestJson(
            TestLlm.DeepSeekProvider(), TestLlm.Execution(), MemoryPrompt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("deepseek-flash", root.GetProperty("model").GetString());
        Assert.Equal(131072, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("instruction", root.GetProperty("system").GetString());
        Assert.Equal("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("max", root.GetProperty("output_config").GetProperty("effort").GetString());
        var wire = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"],
            wire.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Previous result", wire[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("text", wire[1].GetProperty("content")[0].GetProperty("type").GetString());
        var tools = root.GetProperty("tools").EnumerateArray().ToList();
        var tool = Assert.Single(tools);
        Assert.Equal("web_search_20250305", tool.GetProperty("type").GetString());
        Assert.Equal("web_search", tool.GetProperty("name").GetString());
        Assert.Equal(2, tool.GetProperty("max_uses").GetInt32());
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(root.TryGetProperty("max_tool_calls", out _));
        Assert.DoesNotContain("engine", json);
        Assert.DoesNotContain("Bearer", json);
    }

    [Fact]
    public void Request_SearchDisabled_OmitsTools()
    {
        var provider = TestLlm.DeepSeekProvider() with { SearchEnabled = false };
        provider.Validate();
        var json = DeepSeekRequestBuilder.BuildRequestJson(provider, TestLlm.Execution(), Prompt);
        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public void Request_TaskOverride_WinsOverDisabledDefault()
    {
        var provider = TestLlm.DeepSeekProvider() with { SearchEnabled = false };
        var request = new LlmRequest(Prompt, 131072, null, true);
        var json = DeepSeekRequestBuilder.BuildRequestJson(provider, TestLlm.Execution(), Prompt, request);
        Assert.True(JsonDocument.Parse(json).RootElement.TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Request_UnsupportedRoleShapes_Rejected(int count)
    {
        var messages = count == 1
            ? new[] { new ChatMessage("system", "instruction") }
            : count == 3
                ? new[] { new ChatMessage("system", "i"), new ChatMessage("user", "a"), new ChatMessage("user", "b") }
                : new[] { new ChatMessage("user", "a"), new ChatMessage("assistant", "b"),
                    new ChatMessage("user", "c"), new ChatMessage("user", "d"), new ChatMessage("system", "i") };
        Assert.Throws<InvalidOperationException>(() =>
            DeepSeekRequestBuilder.BuildRequestJson(TestLlm.DeepSeekProvider(), TestLlm.Execution(), messages));
    }

    [Fact]
    public void Endpoint_JoinsMessagesPath_ToleratingTrailingSlash()
    {
        Assert.Equal("https://api.deepseek.com/anthropic/v1/messages",
            DeepSeekRequestBuilder.MessagesEndpoint(TestLlm.DeepSeekProvider()));
        var slashed = TestLlm.DeepSeekProvider() with { BaseUrl = "https://api.deepseek.com/anthropic/" };
        Assert.Equal("https://api.deepseek.com/anthropic/v1/messages",
            DeepSeekRequestBuilder.MessagesEndpoint(slashed));
    }

    [Fact]
    public async Task Success_PostsToMessages_WithApiKeyAuth()
    {
        var handler = new ScriptHandler();
        var executor = Executor(handler);
        var result = await executor.ExecuteAsync(new LlmRequest(Prompt, 131072));
        Assert.Equal("ok", result.AnswerText);
        Assert.EndsWith("/v1/messages", handler.LastRequest!.RequestUri!.ToString());
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.Equal("deepseek-key",
            string.Join(",", handler.LastRequest.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01",
            string.Join(",", handler.LastRequest.Headers.GetValues("anthropic-version")));
        Assert.False(handler.LastRequest.Headers.Contains("X-Title"));
        Assert.Equal("DeepSeek", result.Provider);
        Assert.Equal("deepseek-flash", result.Model);
    }

    [Fact]
    public void JsonSearchResponse_MapsFinalTextSourcesUsage()
    {
        const string body = """
            {"id":"msg_fixture","type":"message","role":"assistant","model":"deepseek-flash",
             "content":[
               {"type":"text","text":"Searching."},
               {"type":"server_tool_use","id":"search_1","name":"web_search","input":{"query":"example"}},
               {"type":"web_search_tool_result","tool_use_id":"search_1","content":[
                 {"type":"web_search_result","url":"https://example.com/","title":"Example","encrypted_content":"opaque"}]},
               {"type":"text","text":"See [Example](https://example.com/).","citations":[
                 {"type":"web_search_result_location","url":"https://example.com/","title":"Example","encrypted_index":"opaque","cited_text":"Example"}]}
             ],
             "stop_reason":"end_turn","usage":{"input_tokens":120,"output_tokens":35}}
            """;
        var result = DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072);
        Assert.Equal("See [Example](https://example.com/).", result.AnswerText);
        var source = Assert.Single(result.Sources);
        Assert.Equal("https://example.com/", source.Url);
        Assert.Equal("Example", source.Title);
        Assert.Equal(120, result.Usage.PromptTokens);
        Assert.Equal(35, result.Usage.CompletionTokens);
        Assert.Equal(1, result.Usage.SearchResults);
        Assert.True(result.Usage.SearchUsed);
        Assert.True(result.SearchUsed);
    }

    [Fact]
    public void JsonWithoutCitations_ReturnsValidAnswerWithoutSources()
    {
        const string body = """
            {"type":"message","content":[{"type":"text","text":"Direct answer."}],
             "stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":5}}
            """;
        var result = DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072);
        Assert.Equal("Direct answer.", result.AnswerText);
        Assert.Empty(result.Sources);
        Assert.Equal(0, result.Usage.SearchResults);
        Assert.False(result.Usage.SearchUsed);
        Assert.False(result.SearchUsed);
    }

    [Fact]
    public void JsonMissingUsage_DefaultsCountersToZero()
    {
        const string body = """
            {"type":"message","content":[{"type":"text","text":"Hi."}],"stop_reason":"end_turn"}
            """;
        var result = DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072);
        Assert.Equal(0, result.Usage.PromptTokens);
        Assert.Equal(0, result.Usage.CompletionTokens);
    }

    [Fact]
    public void JsonFiltersNonHttpSources_AndCapsAtMaxTotal()
    {
        var citations = string.Join(",", Enumerable.Range(0, 15).Select(i =>
            $"{{\"type\": \"web_search_result_location\", \"url\": \"https://e.example/{i}\", \"title\": \"T{i}\"}}")) +
            """,{"type": "web_search_result_location", "url": "ftp://e.example/x", "title": "F"}""";
        var body = $"{{\"type\": \"message\", \"content\": [{{\"type\": \"text\", \"text\": \"ok\", \"citations\": [{citations}]}}], \"stop_reason\": \"end_turn\"}}";
        var result = DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072);
        Assert.Equal(10, result.Sources.Count);
        Assert.All(result.Sources, s => Assert.StartsWith("https://", s.Url));
    }

    [Fact]
    public void JsonRetainsDuplicateSources_WhileStreamDeduplicates()
    {
        const string body = """
            {"type":"message","content":[{"type":"text","text":"ok","citations":[
              {"type":"web_search_result_location","url":"https://e.example/1","title":"A"},
              {"type":"web_search_result_location","url":"https://e.example/1","title":"A"}]}],
             "stop_reason":"end_turn"}
            """;
        var json = DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072);
        Assert.Equal(2, json.Sources.Count);
    }

    [Fact]
    public async Task Stream_Fixture_YieldsAnswerUsageWithoutSources()
    {
        const string events = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_fixture","type":"message","role":"assistant","model":"deepseek-flash","content":[],"stop_reason":null,"usage":{"input_tokens":120,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello."}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":35}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var result = await ExecuteStream(events);
        Assert.Equal("Hello.", result.AnswerText);
        Assert.Equal(120, result.Usage.PromptTokens);
        Assert.Equal(35, result.Usage.CompletionTokens);
        Assert.Empty(result.Sources);
        Assert.False(result.SearchUsed);
    }

    [Fact]
    public async Task Stream_IgnoresPingAndProviderUsageExtensions()
    {
        // Observed live shape: keepalive pings plus usage fields beyond
        // input/output tokens (cache counters, tier, server tool counts).
        // Counters map from input/output tokens only; the rest is ignored.
        var events = string.Join("\n",
            "event: ping",
            "data: {\"type\": \"ping\"}",
            "",
            Event("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":69071,"output_tokens":2}}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi."}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":69071,"cache_creation_input_tokens":0,"cache_read_input_tokens":119808,"output_tokens":10,"service_tier":"standard","server_tool_use":{"web_search_requests":6}}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        var result = await ExecuteStream(events);
        Assert.Equal("Hi.", result.AnswerText);
        Assert.Equal(69071, result.Usage.PromptTokens);
        Assert.Equal(10, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Stream_SearchFlow_KeepsFinalSegmentOnly()
    {
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{"usage":{"input_tokens":50,"output_tokens":1}}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Narration."}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://narr.example/","title":"N"}}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"server_tool_use","id":"s1","name":"web_search"}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json": "{\"query\":"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            Event("content_block_start", """{"type":"content_block_start","index":2,"content_block":{"type":"web_search_tool_result","tool_use_id":"s1","content":[]}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":2}"""),
            Event("content_block_start", """{"type":"content_block_start","index":3,"content_block":{"type":"text","text":"First "}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":3,"delta":{"type":"text_delta","text":"half"}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":3,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://example.com/","title":"E"}}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":3,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://example.com/","title":"E"}}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":3}"""),
            Event("content_block_start", """{"type":"content_block_start","index":4,"content_block":{"type":"thinking","thinking":"private"}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":4,"delta":{"type":"thinking_delta","thinking":" more"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":4}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":40}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        var result = await ExecuteStream(events);
        Assert.Equal("First half", result.AnswerText);
        var source = Assert.Single(result.Sources);
        Assert.Equal("https://example.com/", source.Url);
        Assert.True(result.SearchUsed);
    }

    private static string Event(string name, string data) => $"event: {name}\ndata: {data}\n";

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("pause_turn")]
    [InlineData("bogus_stop")]
    public async Task Stream_UnsupportedCompletion_IsAnswerIncomplete(string stop)
    {
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Partial"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + stop + "\"}}"),
            Event("message_stop", """{"type":"message_stop"}"""));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
        Assert.Equal(LlmFailureKind.AnswerIncomplete, ex.Kind);
        Assert.Equal("answer_incomplete", ex.Summary);
    }

    [Fact]
    public async Task Stream_ClientToolUse_IsAnswerIncomplete()
    {
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t1","name":"client"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
        Assert.Equal(LlmFailureKind.AnswerIncomplete, ex.Kind);
    }

    [Theory]
    [InlineData("data: {\"type\":\"message_stop\"}\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("")]
    public async Task Stream_WithoutValidatedStop_NeverReturnsPartialAnswer(string events)
    {
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
    }

    [Fact]
    public async Task Stream_ConfirmedEndTurnWithEmptyAnswer_IsEmptyResponse()
    {
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{}}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
        Assert.Equal(LlmFailureKind.EmptyResponse, ex.Kind);
    }

    [Fact]
    public async Task Stream_OversizedAnswerThenEof_IsSourceLimitNotTransient()
    {
        // The bound trips during accumulation: an oversized segment
        // followed by EOF is SourceLimit, not a retryable stream failure.
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"way too long"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""));
        var handler = new Handler(_ => Reply(events, "text/event-stream"));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            Executor(handler).ExecuteAsync(new LlmRequest(Prompt, 4)));
        Assert.Equal(LlmFailureKind.SourceLimit, ex.Kind);
    }

    [Fact]
    public async Task Stream_WithoutMessageStart_NeverSucceeds()
    {
        var events = string.Join("\n",
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello."}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("{\"type\":\"something_else\"}")]
    public async Task ResultContent_UnsupportedShapes_FailTerminally(string? contentJson)
    {
        var body = "{\"type\":\"message\",\"content\":[" +
            "{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"search_1\"" +
            (contentJson is null ? "" : ",\"content\":" + contentJson) + "}," +
            "{\"type\":\"text\",\"text\":\"Unverified draft.\"}]," +
            "\"stop_reason\":\"end_turn\"}";
        var ex = Assert.Throws<LlmExecutionException>(() =>
            DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072));
        Assert.Equal(LlmFailureKind.AnswerIncomplete, ex.Kind);
        Assert.Equal("answer_incomplete", ex.Summary);

        var start = "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":" +
            "{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"search_1\"" +
            (contentJson is null ? "" : ",\"content\":" + contentJson) + "}}";
        var streamEx = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(
            Event("message_start", """{"type":"message_start","message":{}}""") + "\n" + Event("content_block_start", start)));
        Assert.Equal(LlmFailureKind.AnswerIncomplete, streamEx.Kind);
    }

    [Fact]
    public void InBodySearchRateLimit_PreservesRetryAfter()
    {
        var body = "{\"type\":\"message\",\"content\":[" +
            "{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"search_1\",\"content\":" +
            "{\"type\":\"web_search_tool_result_error\",\"error_code\":\"too_many_requests\"}}," +
            "{\"type\":\"text\",\"text\":\"Unverified draft.\"}]," +
            "\"stop_reason\":\"end_turn\"}";
        var ex = Assert.Throws<LlmExecutionException>(() =>
            DeepSeekResponseReader.ReadResponse(
                body, TestLlm.DeepSeekProvider(), 131072, TimeSpan.FromSeconds(120)));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
    }

    [Fact]
    public async Task Stream_AnswerBeyondBound_IsSourceLimit()
    {
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"way too long"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        var handler = new Handler(_ => Reply(events, "text/event-stream"));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            Executor(handler).ExecuteAsync(new LlmRequest(Prompt, 4)));
        Assert.Equal(LlmFailureKind.SourceLimit, ex.Kind);
    }

    [Theory]
    [InlineData("rate_limit_error", LlmFailureKind.Transient)]
    [InlineData("overloaded_error", LlmFailureKind.Transient)]
    [InlineData("api_error", LlmFailureKind.Transient)]
    [InlineData("authentication_error", LlmFailureKind.Permanent)]
    [InlineData("permission_error", LlmFailureKind.Permanent)]
    [InlineData("invalid_request_error", LlmFailureKind.Permanent)]
    [InlineData("not_found_error", LlmFailureKind.Permanent)]
    [InlineData("request_too_large", LlmFailureKind.Permanent)]
    [InlineData("future_error", LlmFailureKind.Transient)]
    public async Task TopLevelError_ClassifiedWithoutLeakingMessage(string errorType, LlmFailureKind expected)
    {
        var body = JsonSerializer.Serialize(new
        {
            type = "error",
            error = new { type = errorType, message = "secret provider detail" },
        });
        foreach (var streaming in new[] { true, false })
        {
            var payload = streaming
                ? "event: error\n" + "data: " + body + "\n\n"
                : body;
            var handler = new Handler(_ => Reply(payload,
                streaming ? "text/event-stream" : "application/json"));
            var ex = await Assert.ThrowsAsync<LlmExecutionException>(() =>
                Executor(handler).ExecuteAsync(new LlmRequest(Prompt, 131072)));
            Assert.Equal(expected, ex.Kind);
            Assert.DoesNotContain("secret", ex.Summary);
        }
    }

    [Fact]
    public async Task ErrorEvent_NeverDeliversAccumulatedText()
    {
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Partial"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("error", """{"type":"error","error":{"type":"overloaded_error","message":"busy"}}"""));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
    }

    [Theory]
    [InlineData("too_many_requests", LlmFailureKind.Transient)]
    [InlineData("unavailable", LlmFailureKind.Transient)]
    [InlineData("invalid_tool_input", LlmFailureKind.Permanent)]
    [InlineData("query_too_long", LlmFailureKind.Permanent)]
    [InlineData("request_too_large", LlmFailureKind.Permanent)]
    public void InBodySearchError_MapsToFailurePolicy(string errorCode, LlmFailureKind expected)
    {
        var body = "{\"type\":\"message\",\"content\":[" +
            "{\"type\":\"text\",\"text\":\"Searching.\"}," +
            "{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"search_1\",\"content\":" +
            "{\"type\":\"web_search_tool_result_error\",\"error_code\":\"" + errorCode + "\"}}," +
            "{\"type\":\"text\",\"text\":\"Unverified draft.\"}]," +
            "\"stop_reason\":\"end_turn\"}";
        var ex = Assert.Throws<LlmExecutionException>(() =>
            DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public void SearchBudgetExhausted_IsTerminalWithDedicatedCode()
    {
        var body = "{\"type\":\"message\",\"content\":[" +
            "{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"search_1\",\"content\":" +
            "{\"type\":\"web_search_tool_result_error\",\"error_code\":\"max_uses_exceeded\"}}," +
            "{\"type\":\"text\",\"text\":\"Unverified draft.\"}]," +
            "\"stop_reason\":\"end_turn\"}";
        var ex = Assert.Throws<LlmExecutionException>(() =>
            DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072));
        Assert.Equal(LlmFailureKind.Permanent, ex.Kind);
        Assert.Equal("search_budget_exhausted", ex.Summary);
    }

    [Fact]
    public void UnknownSearchError_IsTerminalUnsupported()
    {
        var body = "{\"type\":\"message\",\"content\":[" +
            "{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"search_1\",\"content\":" +
            "{\"type\":\"web_search_tool_result_error\",\"error_code\":\"future_code\"}}," +
            "{\"type\":\"text\",\"text\":\"Unverified draft.\"}]," +
            "\"stop_reason\":\"end_turn\"}";
        var ex = Assert.Throws<LlmExecutionException>(() =>
            DeepSeekResponseReader.ReadResponse(body, TestLlm.DeepSeekProvider(), 131072));
        Assert.Equal(LlmFailureKind.Permanent, ex.Kind);
        Assert.Equal("unsupported_search_error", ex.Summary);
    }

    [Theory]
    [InlineData(429, LlmFailureKind.Transient)]
    [InlineData(502, LlmFailureKind.Transient)]
    [InlineData(401, LlmFailureKind.Permanent)]
    [InlineData(402, LlmFailureKind.Permanent)]
    public async Task HttpFailures_ClassifiedWithRetryAfter(int code, LlmFailureKind expected)
    {
        var handler = new ScriptHandler();
        handler.Responder = _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)code)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "45");
            return response;
        };
        var thrown = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            Executor(handler).ExecuteAsync(new LlmRequest(Prompt, 131072)));
        Assert.Equal(expected, thrown.Kind);
        if (expected == LlmFailureKind.Transient)
            Assert.Equal(TimeSpan.FromSeconds(45), thrown.RetryAfter);
    }

    [Fact]
    public async Task Stream_StructuralViolations_AreTransient()
    {
        var cases = new[]
        {
            // Delta without a block start.
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}"""),
            // Stop without a block start.
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            // Second message_start.
            Event("message_start", """{"type":"message_start","message":{}}""") + "\n" +
            Event("message_start", """{"type":"message_start","message":{}}"""),
            // Duplicate block start for the same index.
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""") + "\n" +
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        };
        foreach (var events in cases)
        {
            var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
            Assert.Equal(LlmFailureKind.Transient, ex.Kind);
        }
    }

    [Fact]
    public async Task Stream_UnknownDeltaShape_IsAnswerIncomplete()
    {
        var events = string.Join("\n",
            Event("message_start", """{"type":"message_start","message":{}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"future_delta"}}"""));
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() => ExecuteStream(events));
        Assert.Equal(LlmFailureKind.AnswerIncomplete, ex.Kind);
    }

    [Fact]
    public async Task BodyReadTimeout_IsTransient_AndCallerCancellationIsPropagated()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream())
            {
                Headers = { ContentType = new("text/event-stream") }
            }
        });
        var shortExecution = TestLlm.Execution() with { RequestTimeout = TimeSpan.FromMilliseconds(50) };
        var ex = await Assert.ThrowsAsync<LlmExecutionException>(() =>
            Executor(handler, execution: shortExecution).ExecuteAsync(new LlmRequest(Prompt, 131072)));
        Assert.Equal(LlmFailureKind.Transient, ex.Kind);
        Assert.Contains("timed out", ex.Summary);

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Executor(handler).ExecuteAsync(new LlmRequest(Prompt, 131072), caller.Token));
    }

    private static Task<LlmResult> ExecuteStream(string events) =>
        Executor(new Handler(_ => Reply(events, "text/event-stream")))
            .ExecuteAsync(new LlmRequest(Prompt, 131072));

    private static HttpResponseMessage Reply(string body, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType)
    };

    private static DeepSeekLlmExecutor Executor(
        HttpMessageHandler handler, DeepSeekOptions? provider = null, ExecutionOptions? execution = null) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            provider ?? TestLlm.DeepSeekProvider(), execution ?? TestLlm.Execution(), "deepseek-key");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class ScriptHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"type":"message","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""",
                    Encoding.UTF8, "application/json"),
            };

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Responder(request));
        }
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
