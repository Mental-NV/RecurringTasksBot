// Owned execution options: memory and context/answer budgets plus the
// generation time/retry/instruction knobs the occurrence handler consumes.
// Provider transport settings live in OpenRouterOptions (Infrastructure);
// the host composes both. Malformed explicit values fail at load time;
// code defaults apply only when a key is absent.
namespace RecurringTasksBot.Application;

public sealed record ExecutionOptions(
    string MemoryMode,
    int TargetAnswerTextChars,
    int MaxAnswerSourceChars,
    int DeclaredContextTokens,
    int SearchContextReserveTokens,
    int ContextEnvelopeReserveTokens,
    TimeSpan RequestTimeout,
    int CompletionTokenBudget,
    int GenerationRetries,
    string SystemInstruction)
{
    public const string PreviousSuccessfulReply = "PreviousSuccessfulReply";
    public const string None = "None";

    public void Validate()
    {
        if (!MemoryMode.Equals(PreviousSuccessfulReply, StringComparison.Ordinal) &&
            !MemoryMode.Equals(None, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Unknown Memory:Mode '{MemoryMode}'. Use '{PreviousSuccessfulReply}' or '{None}'.");
        if (TargetAnswerTextChars <= 0 || TargetAnswerTextChars > TelegramLimits.RichTextChars)
            throw new InvalidOperationException(
                $"Llm:TargetAnswerTextChars must be positive and at most {TelegramLimits.RichTextChars}.");
        if (MaxAnswerSourceChars <= 0 || MaxAnswerSourceChars > ExecutionLimits.AnswerSourceMaxScalars)
            throw new InvalidOperationException(
                $"Llm:MaxAnswerSourceChars must be positive and at most {ExecutionLimits.AnswerSourceMaxScalars} for this storage design.");
        if (DeclaredContextTokens <= 0)
            throw new InvalidOperationException("Llm:DeclaredContextTokens must be positive.");
        if (SearchContextReserveTokens < 0 || ContextEnvelopeReserveTokens < 0)
            throw new InvalidOperationException("LLM context reserves must not be negative.");
        if (RequestTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("LLM request timeout must be positive.");
        if (CompletionTokenBudget <= 0)
            throw new InvalidOperationException("LLM completion token budget must be positive.");
        if (GenerationRetries < 0)
            throw new InvalidOperationException("LLM generation retries must not be negative.");
    }
}
