// Direct DeepSeek chat adapter for the Anthropic-compatible messages
// endpoint. All provider specifics live here: API-key authentication,
// request format, thinking/effort mapping, server-search options, and
// error mapping. No OpenRouter authentication or gateway headers are
// ever forwarded, and no client tool is ever executed.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Llm;

public sealed class DeepSeekLlmExecutor(
    HttpClient http,
    DeepSeekOptions provider,
    ExecutionOptions execution,
    string apiKey) : ILlmExecutor
{
    public Task<LlmResult> ExecuteAsync(LlmRequest request, CancellationToken ct = default) =>
        ExecuteMessagesAsync(request.Messages, request.MaxSourceScalars, ct, request);

    public DeepSeekOptions Options => provider;

    // Canonical task effort -> DeepSeek thinking scale, which only
    // distinguishes low, high, and max. A configuration value of
    // "Maximum" has already normalized to "max" before reaching this
    // boundary; it is still accepted here defensively.
    public static string MapReasoningEffort(string configured)
    {
        if (configured.Equals("Maximum", StringComparison.OrdinalIgnoreCase))
            return "max";
        var token = configured.ToLowerInvariant();
        return token switch
        {
            "low" => "low",
            "med" or "high" or "xhigh" => "high",
            "max" => "max",
            _ => throw new InvalidOperationException(
                $"Unsupported DeepSeek reasoning effort '{configured}'. Use 'Maximum' or a task effort token."),
        };
    }

    public async Task<LlmResult> ExecuteMessagesAsync(
        IReadOnlyList<ChatMessage> messages, int maxSourceScalars, CancellationToken ct = default,
        LlmRequest? request = null)
    {
        provider.Validate();
        if (messages is null || messages.Count == 0)
            throw new LlmExecutionException(LlmFailureKind.EmptyResponse, "empty prompt");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new LlmExecutionException(LlmFailureKind.Permanent, "LLM credential is not configured.");
        if (maxSourceScalars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSourceScalars));

        string json;
        try
        {
            json = DeepSeekRequestBuilder.BuildRequestJson(provider, execution, messages, request);
        }
        catch (InvalidOperationException ex)
        {
            // Unsupported per-request combinations fail terminally; never
            // downgraded silently to another effort or search mode.
            throw new LlmExecutionException(LlmFailureKind.Permanent, ex.Message);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(execution.RequestTimeout);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            DeepSeekRequestBuilder.MessagesEndpoint(provider));
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        httpRequest.Headers.TryAddWithoutValidation(
            "anthropic-version", DeepSeekRequestBuilder.AnthropicVersion);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var elapsed = Stopwatch.StartNew();
        var stage = "connecting";
        try
        {
            using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
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
                return await DeepSeekStreamReader.ReadStreamAsync(
                    stream, provider, maxSourceScalars, retryAfter, cts.Token);
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            return DeepSeekResponseReader.ReadResponse(body, provider, maxSourceScalars, retryAfter);
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
