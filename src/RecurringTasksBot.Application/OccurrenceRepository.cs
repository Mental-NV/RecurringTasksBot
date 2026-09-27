namespace RecurringTasksBot.Application;

public enum PlanFallbackKind
{
    ToLiteralRich,
    ToSmallLiteral,
    ToPlain,
}

// Coordinated occurrence repository: every publication reads the current
// receipt, active operation, and memory, then commits atomically under the
// receipt claim. 409/412 conflicts reread and retry (at most eight local
// attempts) before surfacing as retryable storage conflicts.
public sealed record FrozenContextInputs(
    string EffectiveSystemInstruction,
    int TargetAnswerTextChars,
    int MaxRichMessageChars,
    string MemoryMode,
    DateTime ExecutionStartedUtc);

public sealed record FrozenContextRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    FrozenContextInputs Inputs);

public sealed record ContextInitResult(FrozenContextRecord Context, bool Created);

public sealed record ReceiptUsage(
    string Provider,
    string ModelName,
    long PromptTokens,
    long CompletionTokens,
    int SearchResults,
    bool SearchUsed);

public sealed record PersistGenerationRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    AnswerArtifact Answer,
    DeliveryPlanDoc InitialPlan,
    DateTime SourceExecutedUtc,
    ReceiptUsage Usage,
    string? ErrorSummary = null);

public sealed record ConfirmLeafRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    string LeafId,
    long MessageId,
    int DeliveryFailures = 0);

public sealed record ReplaceLeafRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    string LeafId,
    PlanFallbackKind Fallback,
    int DeliveryFailures = 0);

public sealed record PlanReplacement(DeliveryPlanDoc Plan, IReadOnlyList<PlanLeaf> Fresh);

public sealed record CompleteRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    int DeliveryFailures = 0);

public sealed record TrackGenerationRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    string Summary);

public sealed record TrackDeliveryRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    string Summary);

public sealed record FailRequest(
    string OwnerId,
    string OperationId,
    DateTime ScheduledUtc,
    string ClaimId,
    string ErrorSummary,
    int HttpAttempts = 0,
    int DeliveryFailures = 0);

public sealed record ProgressRead(DeliveryPlanDoc Plan, AnswerArtifact Answer);

public interface IOccurrenceRepository
{
    // A missing memory row is normal (null). Storage errors and malformed
    // rows are failures, never proof of absence.
    Task<MemoryRecord?> ReadPreviousReplyAsync(
        string ownerId, string operationId, CancellationToken ct = default);

    // Initializes the frozen context under the current receipt claim exactly
    // once; a second call reads and reuses the committed snapshot.
    Task<ContextInitResult> InitializeContextAsync(
        FrozenContextRequest request, CancellationToken ct = default);

    // Persists the generated answer and its initial plan under that claim.
    // Terminal failure notices use AnswerKind.FailureNotice.
    Task PersistGenerationAsync(
        PersistGenerationRequest request, CancellationToken ct = default);

    // Confirms one leaf (idempotent by leaf ID/message ID) or replaces the
    // rejected unsent leaf, persisting progress before the next send.
    Task<DeliveryPlanDoc> ConfirmLeafAsync(
        ConfirmLeafRequest request, CancellationToken ct = default);

    Task<PlanReplacement> ReplaceLeafAsync(
        ReplaceLeafRequest request, CancellationToken ct = default);

    // Reads the committed plan and answer for resuming delivery.
    Task<ProgressRead> ReadProgressAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        CancellationToken ct = default);

    // Completes a fully confirmed answer and conditionally publishes memory
    // atomically. Failure notices complete without memory publication.
    Task CompleteAsync(CompleteRequest request, CancellationToken ct = default);

    // Separate retry counters and terminal failure state. Track calls follow
    // exactly one failed attempt each; Fail persists an occurrence failure
    // without sending anything further.
    Task<int> TrackGenerationAsync(
        TrackGenerationRequest request, CancellationToken ct = default);

    Task<int> TrackDeliveryAsync(
        TrackDeliveryRequest request, CancellationToken ct = default);

    Task FailAsync(FailRequest request, CancellationToken ct = default);

    // The repository owns the occurrence receipt and its lease: reads,
    // claims, renewals, and releases all go through these operations.
    // Stale or foreign claims surface as ClaimLostException, never as data.
    Task<DeliveryReceipt?> GetReceiptAsync(
        string ownerId, string operationId, DateTime scheduledUtc,
        CancellationToken ct = default);

    Task<bool> TryClaimAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        CancellationToken ct = default);

    Task<bool> RenewClaimAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        CancellationToken ct = default);

    Task ReleaseClaimAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        CancellationToken ct = default);

    // Rejects an unknown persisted schema under the current claim, retaining
    // the row for inspection. Nothing regenerates or sends afterwards.
    Task MarkUnsupportedVersionAsync(
        string ownerId, string operationId, DateTime scheduledUtc, string claimId,
        int attempts, CancellationToken ct = default);
}
