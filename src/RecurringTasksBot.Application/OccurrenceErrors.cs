// Occurrence failure codes, user-facing messages, and integrity
// exceptions. Persisted state that retry cannot fix surfaces as
// OccurrenceConsistencyException; unknown schemas and corrupt payloads
// surface as PayloadIntegrityException with a stable machine code.
namespace RecurringTasksBot.Application;

public static class OccurrenceFailureCodes
{
    public const string AnswerSourceLimit = "answer_source_limit";
    public const string AnswerIncomplete = "answer_incomplete";
    public const string ContextBudgetExceeded = "context_budget_exceeded";
    public const string DeliveryPlanLimit = "delivery_plan_limit";
    public const string PayloadCorrupt = "payload_corrupt";
    public const string UnsupportedPayloadVersion = "unsupported_payload_version";
}

public sealed class PayloadIntegrityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Persisted state is inconsistent in a way retry cannot fix.
public sealed class OccurrenceConsistencyException(string message) : Exception(message);

// The operation is missing, deleted, or failed: no new publication.
public sealed class OperationStoppedException(string message) : Exception(message);

// Application-generated terminal generation failure notice. Literal text:
// no IDs, timestamps, provider errors, or sources. Never becomes memory.
public static class OccurrenceMessages
{
    public const string FailureNotice = "This run failed. Future runs remain scheduled.";
}
