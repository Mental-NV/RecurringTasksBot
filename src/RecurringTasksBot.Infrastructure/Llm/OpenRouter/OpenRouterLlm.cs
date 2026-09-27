// OpenRouter chat-completions adapter. All provider specifics live here:
// authentication, request format, reasoning mapping, search options, and
// error mapping. A future direct Alibaba/Qwen integration adds another
// adapter without touching scheduling, storage, or Telegram behavior.
//
// Verified against the live OpenRouter docs and model metadata during
// implementation:
// - reasoning: { effort: "max", exclude: true } — "max" is the documented
//   gateway-level maximum (~95% of completion tokens); exclude keeps
//   reasoning out of the response (still billed, still shares the
//   completion-token budget).
// - Completion budget uses max_completion_tokens (max_tokens deprecated).
// - Search uses the openrouter:web_search server tool with the Exa engine;
//   the web plugin and :online suffix are deprecated and never sent.
// - deepseek/deepseek-v4.1-flash exists with max_completion_tokens 393216,
//   so the 131,072 budget fits; supported_parameters includes reasoning,
//   reasoning_effort, tools, and max_completion_tokens.
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public sealed class OpenRouterLlmExecutor(
    HttpClient http,
    OpenRouterOptions provider,
    ExecutionOptions execution,
    string apiKey) : ILlmExecutor
{
    public Task<LlmResult> ExecuteAsync(LlmRequest request, CancellationToken ct = default) =>
        ExecuteMessagesAsync(request.Messages, request.MaxSourceScalars, ct);

    public const string ServerToolType = "openrouter:web_search";
    public const string SearchEngineExa = "exa";

    private static readonly HashSet<string> SupportedEfforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "max", "xhigh", "high", "medium", "low", "minimal", "none",
    };

    public OpenRouterOptions Options => provider;

    // Semantic config value -> provider API value. Only "Maximum" is a
    // supported configuration; anything else fails clearly instead of
    // silently running at a lower effort.
    public static string MapReasoningEffort(string configured)
    {
        if (configured.Equals("Maximum", StringComparison.OrdinalIgnoreCase))
            return "max";
        if (SupportedEfforts.Contains(configured))
            return configured.ToLowerInvariant();
        throw new InvalidOperationException(
            $"Unsupported LLM reasoning effort '{configured}'. Use 'Maximum'.");
    }

    public async Task<LlmResult> ExecuteMessagesAsync(
        IReadOnlyList<ChatMessage> messages, int maxSourceScalars, CancellationToken ct = default)
    {
        provider.Validate();
        if (messages is null || messages.Count == 0)
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty prompt");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LlmExecutionException(LlmFailureKind.Permanent, "LLM credential is not configured.");
        if (maxSourceScalars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSourceScalars));

        var json = OpenRouterRequestBuilder.BuildRequestJson(provider, execution, messages);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(execution.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            provider.BaseUrl.TrimEnd('/') + "/chat/completions");
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
                return await OpenRouterStreamReader.ReadStreamAsync(
                    stream, provider, maxSourceScalars, retryAfter, cts.Token);
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return OpenRouterResponseReader.ReadResponse(body, provider, maxSourceScalars, retryAfter);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmExecutionException(LlmFailureKind.Transient,
                $"LLM request timed out after {elapsed.Elapsed.TotalSeconds:F0}s " +
                $"(limit {execution.RequestTimeout.TotalSeconds:F0}s; {stage}).");
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

    private static LlmExecutionException TransportFailure(Exception error, string stage, TimeSpan elapsed)
    {
        // Exception messages can contain URLs, prompts, or credentials.
        // Keep only framework categories and numeric/socket error codes.
        var category = error is HttpRequestException httpError
            ? httpError.HttpRequestError.ToString() : "ResponseReadError";
        string? socketCode = null;
        var tls = false;
        for (var cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is SocketException socket) socketCode = socket.SocketErrorCode.ToString();
            if (cause is System.Security.Authentication.AuthenticationException) tls = true;
        }
        return new LlmExecutionException(LlmFailureKind.Transient,
            $"LLM transport failure after {elapsed.TotalSeconds:F0}s ({stage}; {category}" +
            (socketCode is null ? "" : $"; socket={socketCode}") + (tls ? "; TLS" : "") + ").");
    }

    private static TimeSpan? RetryAfterFromHeaders(HttpResponseMessage response, DateTimeOffset nowUtc)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
            return GenerationPolicy.ParseRetryAfter(values.FirstOrDefault(), nowUtc);
        return null;
    }
}
