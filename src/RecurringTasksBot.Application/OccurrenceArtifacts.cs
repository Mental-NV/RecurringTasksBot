namespace RecurringTasksBot.Application;

// Immutable occurrence artifacts: the canonical answer, the frozen
// per-occurrence context, and the single mutable last successful answer.
public enum AnswerKind
{
    Answer,
    FailureNotice,
}

// Single mutable last successful answer per (owner, operation).
public sealed record MemoryRecord(
    string OwnerId,
    string OperationId,
    DateTime SourceScheduledUtc,
    DateTime SourceExecutedUtc,
    DateTimeOffset PublishedUtc,
    string AnswerVersion,
    string Answer,
    string SourceSha256,
    int ScalarCount);

// Immutable frozen context for one occurrence. The previous answer is a
// copy: overwriting memory cannot invalidate an in-progress occurrence.
public sealed record FrozenContextRecord(
    string ContextVersion,
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string TaskText,
    string ScheduleCron,
    string OccurrenceId,
    string ScheduledAtUtc,
    string ExecutionStartedAtUtc,
    bool PreviousReplyPresent,
    string? PreviousReplyScheduledAtUtc,
    string? PreviousReplyExecutedAtUtc,
    string? PreviousReplyAnswer,
    string? NoMemoryReason,
    string EffectiveSystemInstruction,
    int TargetAnswerTextChars,
    int MaxRichMessageChars,
    string MemoryMode,
    string EnabledCapabilities,
    string InstructionVersion,
    DateTimeOffset CreatedUtc)
{
    public ExecutionContextSnapshot ToSnapshot() => new(
        TaskText, ScheduleCron, ExecutionLimits.ScheduleTimezone,
        OccurrenceId, ScheduledAtUtc, ExecutionStartedAtUtc,
        PreviousReplyPresent,
        PreviousReplyScheduledAtUtc, PreviousReplyExecutedAtUtc,
        EnabledCapabilities, InstructionVersion);
}

// Immutable canonical answer (or terminal failure notice) for one
// occurrence. Version IDs are generated once per persisted artifact.
public sealed record AnswerArtifact(
    string AnswerVersion,
    AnswerKind Kind,
    string Text,
    string SourceSha256,
    int ScalarCount)
{
    public static AnswerArtifact Create(string answerVersion, AnswerKind kind, string canonicalText)
    {
        if (string.IsNullOrEmpty(answerVersion))
            throw new ArgumentOutOfRangeException(nameof(answerVersion));
        return new AnswerArtifact(answerVersion, kind, canonicalText,
            DeliveryPlan.HashSource(canonicalText), AnswerSourceBound.CountScalars(canonicalText));
    }
}

// Artifact version IDs are generated once per persisted artifact.
public static class ArtifactVersion
{
    public static string New() => Guid.NewGuid().ToString("N");
}
