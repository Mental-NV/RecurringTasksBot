// Phase 3 OpenRouter request construction: the frozen effective system
// instruction plus an optional archived assistant turn. Provider transport
// (reasoning mapping, streaming, search tools, error mapping) is unchanged.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RecurringTasksBot.Core;

public sealed partial class OpenRouterLlmExecutor
{
    // Messages are already fully constructed (system + optional archived
    // user/assistant turns + current user envelope); they are serialized
    // verbatim with no interpolation or delimiter tricks.
    public static string BuildPhase3RequestJson(LlmOptions opts, IReadOnlyList<ChatMessage> messages)
    {
        opts.Validate();
        if (messages is null || messages.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(messages));
        var effort = MapReasoningEffort(opts.ReasoningEffort);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", opts.Model);
            writer.WriteStartArray("messages");
            foreach (var message in messages)
            {
                if (string.IsNullOrEmpty(message.Role) || message.Content is null)
                    throw new ArgumentOutOfRangeException(nameof(messages));
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("reasoning");
            writer.WriteString("effort", effort);
            writer.WriteBoolean("exclude", true);
            writer.WriteEndObject();
            writer.WriteNumber("max_completion_tokens", opts.CompletionTokenBudget);
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("max_tool_calls", opts.MaxSearches);
            writer.WriteStartArray("tools");
            writer.WriteStartObject();
            writer.WriteString("type", ServerToolType);
            writer.WriteStartObject("parameters");
            writer.WriteString("engine", opts.SearchEngine);
            writer.WriteNumber("max_results", opts.MaxResultsPerSearch);
            writer.WriteNumber("max_total_results", opts.MaxTotalResults);
            writer.WriteNumber("max_uses", opts.MaxSearches);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async Task<LlmResult> ExecuteMessagesAsync(
        IReadOnlyList<ChatMessage> messages, int maxSourceScalars, CancellationToken ct = default)
    {
        options.Validate();
        if (messages is null || messages.Count == 0)
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty prompt");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LlmExecutionException(LlmFailureKind.Permanent, "LLM credential is not configured.");
        if (maxSourceScalars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSourceScalars));

        var json = BuildPhase3RequestJson(options, messages);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            options.BaseUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("X-Title", "RecurringTasksBot");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var elapsed = Stopwatch.StartNew();
        var stage = "connecting";
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            stage = "reading response";
            var retryAfter = RetryAfterFromHeaders(response, DateTimeOffset.UtcNow);
            if (!response.IsSuccessStatusCode)
            {
                var kind = GenerationPolicy.ClassifyHttpStatus((int)response.StatusCode);
                throw new LlmExecutionException(kind,
                    $"LLM request failed (HTTP {(int)response.StatusCode}).",
                    kind == LlmFailureKind.Transient ? retryAfter : null);
            }

            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                return await ParsePhase3StreamAsync(stream, options, maxSourceScalars, retryAfter, cts.Token);
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return ParsePhase3Response(body, options, maxSourceScalars, retryAfter);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmExecutionException(LlmFailureKind.Transient,
                $"LLM request timed out after {elapsed.Elapsed.TotalSeconds:F0}s " +
                $"(limit {options.RequestTimeout.TotalSeconds:F0}s; {stage}).");
        }
        catch (HttpRequestException ex)
        {
            throw TransportFailure(ex, stage, elapsed.Elapsed);
        }
        catch (IOException ex)
        {
            throw TransportFailure(ex, stage, elapsed.Elapsed);
        }
    }

    // Streaming twin of ParseStreamAsync: the source bound is enforced while
    // accumulating visible content (runes are never split across chunks), a
    // provider finish reason of length is terminal, and no partial answer is
    // ever returned.
    public static async Task<LlmResult> ParsePhase3StreamAsync(Stream stream, LlmOptions opts,
        int maxSourceScalars, TimeSpan? retryAfter = null, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        var bound = new AnswerSourceAccumulator(maxSourceScalars);
        var sources = new List<LlmSource>();
        long promptTokens = 0, completionTokens = 0;
        string? finishReason = null;

        bool ProcessEvent()
        {
            if (data.Length == 0) return false;
            var payload = data.ToString().TrimEnd('\n');
            data.Clear();
            if (payload == "[DONE]") return true;
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            CheckProviderError(root, retryAfter);
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                (promptTokens, completionTokens) = ExtractUsage(root);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0) continue;
                CheckProviderError(choice, retryAfter);
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                    finishReason = finish.GetString();
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                var increment = ExtractContent(delta);
                try
                {
                    bound.Append(increment);
                }
                catch (Phase3PayloadException)
                {
                    throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
                }
                if (bound.IsOverLimit)
                    throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
                foreach (var source in ExtractSources(delta, opts.MaxTotalResults))
                    if (sources.Count < opts.MaxTotalResults && !sources.Any(s => s.Url == source.Url))
                        sources.Add(source);
            }
            return false;
        }

        var done = false;
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    if (ProcessEvent()) { done = true; break; }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var field = line[5..];
                    data.Append(field.StartsWith(' ') ? field[1..] : field).Append('\n');
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed LLM stream.");
        }

        if (!done || finishReason is not ("stop" or "length"))
            throw new LlmExecutionException(LlmFailureKind.Transient, "Incomplete LLM stream.");
        if (finishReason == "length")
            throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
        if (!bound.HasContent)
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty LLM response");
        string canonical;
        try
        {
            canonical = bound.GetCanonical();
        }
        catch (Phase3PayloadException)
        {
            throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
        }
        return new LlmResult(canonical, sources,
            new LlmUsage(promptTokens, completionTokens, sources.Count, sources.Count > 0),
            opts.Provider, opts.Model, sources.Count > 0);
    }

    public static LlmResult ParsePhase3Response(
        string body, LlmOptions opts, int maxSourceScalars, TimeSpan? retryAfter = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable LLM response");
        }

        using (doc)
        {
            var root = doc.RootElement;
            CheckProviderError(root, retryAfter);
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable LLM response");
            CheckProviderError(choices[0], retryAfter);
            if (choices[0].TryGetProperty("finish_reason", out var finish) &&
                finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
                throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
            var message = choices[0].TryGetProperty("message", out var m) ? m : default;
            if (message.ValueKind != JsonValueKind.Object)
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "unusable LLM response");

            var answer = ExtractContent(message);
            var sources = ExtractSources(message, opts.MaxTotalResults);
            var (promptTokens, completionTokens) = ExtractUsage(root);
            if (string.IsNullOrWhiteSpace(answer))
                throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty LLM response");
            try
            {
                AnswerSourceBound.RequireWithinBound(answer.Trim(), maxSourceScalars);
            }
            catch (Phase3PayloadException)
            {
                throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
            }
            return new LlmResult(answer.Trim(), sources,
                new LlmUsage(promptTokens, completionTokens, sources.Count, sources.Count > 0),
                opts.Provider, opts.Model, sources.Count > 0);
        }
    }
}
