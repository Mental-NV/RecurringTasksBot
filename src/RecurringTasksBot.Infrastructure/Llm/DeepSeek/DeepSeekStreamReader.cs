// DeepSeek streaming reader for the Anthropic SSE lifecycle: indexed
// content blocks, deltas, cumulative usage updates, errors, and a
// validated final message stop. Transport EOF alone is never a
// successful answer, and OpenRouter's [DONE] sentinel is never treated
// as message_stop. Streaming deduplicates sources by exact URL,
// matching the existing OpenRouter stream behavior for this mode.
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public static class DeepSeekStreamReader
{
    public static async Task<LlmResult> ReadStreamAsync(Stream stream, DeepSeekOptions provider,
        int maxSourceScalars, TimeSpan? retryAfter = null, CancellationToken ct = default)
    {
        var state = new StreamState(provider, maxSourceScalars, retryAfter);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    if (state.DispatchEvent(data.ToString()) is { } done)
                        return done;
                    data.Clear();
                }
                else if (line[0] == ':')
                {
                    // Comment/keepalive line: ignored.
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var field = line[5..];
                    data.Append(field.StartsWith(' ') ? field[1..] : field).Append('\n');
                }
            }
            if (data.Length > 0 && state.DispatchEvent(data.ToString()) is { } trailing)
                return trailing;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
        }

        throw new LlmExecutionException(LlmFailureKind.Transient, "Incomplete DeepSeek stream.");
    }

    private sealed class StreamState(DeepSeekOptions provider, int maxSourceScalars, TimeSpan? retryAfter)
    {
        private readonly Dictionary<int, DeepSeekAssembledBlock> _open = new();
        private readonly Dictionary<int, DeepSeekAssembledBlock> _closed = new();
        private bool _messageStarted;
        private long _promptTokens;
        private long _completionTokens;
        private string? _stopReason;
        // Bound enforcement for the current final-answer candidate: text
        // accumulates here incrementally so an oversized segment trips
        // SourceLimit immediately instead of at finalization. Each
        // server-tool/result boundary discards the preceding candidate,
        // matching final-segment selection.
        // Created on message_start; every use site requires a started
        // message first, so this is never consumed uninitialized.
        private AnswerSourceAccumulator _segment = null!;

        // Returns the finalized result on message_stop; otherwise null.
        // message_stop is the only success path: EOF, [DONE], or a
        // truncated event sequence always falls through to the
        // incomplete-stream failure in the caller.
        public LlmResult? DispatchEvent(string payload)
        {
            var text = payload.TrimEnd('\n');
            if (text.Length == 0)
                return null;
            using var doc = ParsePayload(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            switch (type.GetString())
            {
                case "message_start":
                    OnMessageStart(root);
                    return null;
                case "content_block_start":
                    OnBlockStart(root);
                    return null;
                case "content_block_delta":
                    OnBlockDelta(root);
                    return null;
                case "content_block_stop":
                    OnBlockStop(root);
                    return null;
                case "message_delta":
                    OnMessageDelta(root);
                    return null;
                case "message_stop":
                    return OnMessageStop();
                case "ping":
                    return null;
                case "error":
                    OnError(root);
                    return null;
                default:
                    throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            }
        }

        private static JsonDocument ParsePayload(string text)
        {
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            }
        }

        private void OnMessageStart(JsonElement root)
        {
            if (_messageStarted)
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            _messageStarted = true;
            _segment = new AnswerSourceAccumulator(maxSourceScalars);
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    (_promptTokens, _completionTokens) = ExtractUsage(usage);
            }
        }

        private void RequireStarted()
        {
            if (!_messageStarted)
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
        }

        private void AppendSegmentText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            try
            {
                _segment.Append(text);
            }
            catch (PayloadIntegrityException)
            {
                throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
            }
            if (_segment.IsOverLimit)
                throw new LlmExecutionException(LlmFailureKind.SourceLimit, "answer_source_limit");
        }

        private void OnBlockStart(JsonElement root)
        {
            RequireStarted();
            var index = RequiredIndex(root);
            if (_open.ContainsKey(index) || _closed.ContainsKey(index))
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            if (!root.TryGetProperty("content_block", out var contentBlock) ||
                contentBlock.ValueKind != JsonValueKind.Object)
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            var block = new DeepSeekAssembledBlock
            {
                Type = contentBlock.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty : string.Empty,
            };
            if (block.Type.Equals("text", StringComparison.Ordinal))
            {
                if (contentBlock.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    block.Text.Append(text.GetString());
                    AppendSegmentText(text.GetString());
                }
                if (contentBlock.TryGetProperty("citations", out var citations) &&
                    citations.ValueKind == JsonValueKind.Array)
                {
                    foreach (var citation in citations.EnumerateArray())
                        DeepSeekMessageReader.AddCitation(block, citation);
                }
            }
            else if (block.Type.Equals("web_search_tool_result", StringComparison.Ordinal))
            {
                // A new tool boundary discards the preceding answer
                // candidate, matching final-segment selection.
                _segment = new AnswerSourceAccumulator(maxSourceScalars);
                if (!contentBlock.TryGetProperty("content", out var content))
                    throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
                DeepSeekMessageReader.ReadSearchResultContent(block, content, present: true);
            }
            else if (block.Type.Equals("server_tool_use", StringComparison.Ordinal))
            {
                _segment = new AnswerSourceAccumulator(maxSourceScalars);
            }
            _open[index] = block;
        }

        private void OnBlockDelta(JsonElement root)
        {
            RequireStarted();
            var index = RequiredIndex(root);
            if (!_open.TryGetValue(index, out var block))
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            var deltaType = delta.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            switch (deltaType)
            {
                case "text_delta":
                    if (delta.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        block.Text.Append(text.GetString());
                        if (block.Type.Equals("text", StringComparison.Ordinal))
                            AppendSegmentText(text.GetString());
                    }
                    break;
                case "input_json_delta":
                    // Tool input accumulates for protocol completeness but is
                    // never answer text and never executed client-side.
                    break;
                case "thinking_delta":
                case "signature_delta":
                    break;
                case "citations_delta":
                    if (delta.TryGetProperty("citation", out var citation))
                        DeepSeekMessageReader.AddCitation(block, citation);
                    break;
                default:
                    throw new LlmExecutionException(LlmFailureKind.AnswerIncomplete, "answer_incomplete");
            }
        }

        private void OnBlockStop(JsonElement root)
        {
            RequireStarted();
            var index = RequiredIndex(root);
            if (!_open.Remove(index, out var block))
                throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
            _closed[index] = block;
        }

        private void OnMessageDelta(JsonElement root)
        {
            RequireStarted();
            if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String)
                    _stopReason = stop.GetString();
            }
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                // Usage values are cumulative: replace only present counters.
                var (prompt, completion) = ExtractUsage(usage, _promptTokens, _completionTokens);
                _promptTokens = prompt;
                _completionTokens = completion;
            }
        }

        private LlmResult OnMessageStop()
        {
            RequireStarted();
            if (_open.Count > 0)
                throw new LlmExecutionException(LlmFailureKind.Transient, "Incomplete DeepSeek stream.");
            var ordered = _closed.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            return DeepSeekMessageReader.Finalize(
                ordered, _promptTokens, _completionTokens, _stopReason,
                provider, maxSourceScalars, deduplicateSources: true, retryAfter: retryAfter);
        }

        private void OnError(JsonElement root)
        {
            var errorType = root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("type", out var nested) && nested.ValueKind == JsonValueKind.String
                ? nested.GetString() : null;
            throw DeepSeekMessageReader.MapTopLevelError(errorType, retryAfter);
        }

        private static int RequiredIndex(JsonElement root)
        {
            if (root.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number &&
                index.TryGetInt32(out var value))
                return value;
            throw new LlmExecutionException(LlmFailureKind.Transient, "Malformed DeepSeek stream.");
        }

        private static (long PromptTokens, long CompletionTokens) ExtractUsage(
            JsonElement usage, long promptFallback = 0, long completionFallback = 0)
        {
            var prompt = usage.TryGetProperty("input_tokens", out var p) && p.TryGetInt64(out var pv)
                ? pv : promptFallback;
            var completion = usage.TryGetProperty("output_tokens", out var c) && c.TryGetInt64(out var cv)
                ? cv : completionFallback;
            return (prompt, completion);
        }
    }
}
