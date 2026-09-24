using System.Diagnostics;
using System.Text.Json;
using RecurringTasksBot.Core;

// This deliberately uses the exact adapter used by scheduled activities.
// One paid request; no writes to Telegram, schedules, or application storage.
var key = Environment.GetEnvironmentVariable("RecurringTasksBot__Llm__ApiKey");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Missing required env var: RecurringTasksBot__Llm__ApiKey");
    return 1;
}
var config = new Dictionary<string, string>();
foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
{
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    if (doc.RootElement.GetProperty("RecurringTasksBot").TryGetProperty("Llm", out var llm))
        foreach (var property in llm.EnumerateObject())
            config["RecurringTasksBot:Llm:" + property.Name] = property.Value.ToString();
}
var options = LlmConfig.Read(name => Environment.GetEnvironmentVariable(name.Replace(":", "__"))
    ?? config.GetValueOrDefault(name));
if (Environment.GetEnvironmentVariable("SMOKE_MODEL") is { Length: > 0 } model)
    options = options with { Model = model };
var prompt = Environment.GetEnvironmentVariable("SMOKE_PROMPT_FILE") is { Length: > 0 } path
    ? await File.ReadAllTextAsync(path)
    : "What were the three most important AI announcements in the past 24 hours? Include source links.";
using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var executor = new OpenRouterLlmExecutor(client, options, key);
var elapsed = Stopwatch.StartNew();
try
{
    Console.WriteLine($"model={options.Model} reasoning={options.ReasoningEffort} timeout={options.RequestTimeout.TotalSeconds}s transport=SSE");
    var result = await executor.ExecuteAsync(new LlmPrompt(prompt, DateTime.UtcNow, DateTime.UtcNow));
    var parts = AnswerComposer.Compose("smoke", DateTime.UtcNow, DateTime.UtcNow, result.AnswerText, result.Sources);
    var valid = result.SearchUsed && result.Sources.Count <= options.MaxTotalResults &&
        parts.Count > 0 && parts.All(p => RichMessageParts.CountRenderedChars(p) <= TextLimits.MaxAnswerChars);
    Console.WriteLine($"elapsed={elapsed.Elapsed.TotalSeconds:F1}s answerChars={TextLimits.CountChars(result.AnswerText)} " +
        $"sources={result.Sources.Count} parts={parts.Count} promptTokens={result.Usage.PromptTokens} completionTokens={result.Usage.CompletionTokens}");
    Console.WriteLine(valid ? "PASS: complete answer, search citations, valid rich-message limits" : "FAIL: search/citation or rich-message checks");
    return valid ? 0 : 1;
}
catch (LlmExecutionException ex)
{
    Console.Error.WriteLine($"SMOKE FAIL: kind={ex.Kind} elapsed={elapsed.Elapsed.TotalSeconds:F1}s {ex.Summary}");
    return 1;
}
