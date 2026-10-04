// One selected-adapter factory shared by the Functions host and the
// smoke tool: the selector switches adapter, model, endpoint, and key
// together from the already validated selected configuration. No
// provider-specific construction lives in callers.
using RecurringTasksBot.Application;
using RecurringTasksBot.Infrastructure.Configuration;

namespace RecurringTasksBot.Infrastructure.Llm;

public sealed record SelectedLlmAdapter(ILlmExecutor Executor, string ProviderName, string ModelName);

public static class LlmAdapterFactory
{
    public static SelectedLlmAdapter CreateSelected(
        HttpClient http, ExecutionOptions execution, SelectedLlmConfiguration selected, string apiKey)
    {
        execution.Validate();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"Missing credential for LLM profile '{selected.ActiveProfile}'.");
        if (selected.OpenRouter is not null)
        {
            selected.OpenRouter.Validate();
            return new SelectedLlmAdapter(
                new OpenRouterLlmExecutor(http, selected.OpenRouter, execution, apiKey),
                selected.Provider, selected.OpenRouter.Model);
        }
        if (selected.DeepSeek is not null)
        {
            selected.DeepSeek.Validate();
            return new SelectedLlmAdapter(
                new DeepSeekLlmExecutor(http, selected.DeepSeek, execution, apiKey),
                selected.Provider, selected.DeepSeek.Model);
        }
        throw new InvalidOperationException(
            $"LLM profile '{selected.ActiveProfile}' selected provider '{selected.Provider}' " +
            "but has no adapter options.");
    }
}
