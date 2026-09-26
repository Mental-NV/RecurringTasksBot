// Phase 3 storage contracts: schema-3 records, segmented-property codec,
// row keys, storage bounds, and the coordinated occurrence repository
// interface. Provider-neutral: no TableEntity, ETags, or SDK objects here;
// the host adapter maps these to Azure transactions.
using System.Text;

namespace RecurringTasksBot.Core;

public interface IPhase3Clock
{
    DateTimeOffset UtcNow { get; }
}

// Persisted state is inconsistent in a way retry cannot fix.
public sealed class Phase3ConsistencyException(string message) : Exception(message);

// The operation is missing, deleted, or failed: no new publication.
public sealed class Phase3OperationStoppedException(string message) : Exception(message);

public enum AnswerKind
{
    Answer,
    FailureNotice,
}

public enum PlanFallbackKind
{
    ToLiteralRich,
    ToSmallLiteral,
    ToPlain,
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
    public Phase3ContextSnapshot ToSnapshot() => new(
        TaskText, ScheduleCron, Phase3Limits.ScheduleTimezone,
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

public static class Phase3Versions
{
    public static string New() => Guid.NewGuid().ToString("N");
}

public static class Phase3RowKeys
{
    public static string Memory(string operationId) => $"memory_{operationId}";

    public static string Context(string operationId, DateTime scheduledUtc, string contextVersion) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__v3context_{contextVersion}";

    public static string Answer(string operationId, DateTime scheduledUtc, string answerVersion) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__v3answer_{answerVersion}";

    public static string Plan(string operationId, DateTime scheduledUtc, string planVersion) =>
        $"delivery_{operationId}_{scheduledUtc.Ticks}__v3plan_{planVersion}";
}

// Numbered properties within one entity (Answer_0000, Answer_0001, ...)
// with a chunk count, scalar count, and SHA-256 of UTF-8 content. Split at
// 16,000 scalars per property so even supplementary characters stay under
// the 64-KiB property limit. Reading requires every index and the hash;
// corruption is rejected, never concatenated partially.
public static class SegmentedProperties
{
    public static void Write(
        IDictionary<string, object?> properties, string prefix, string value,
        int chunkScalars = Phase3Limits.PropertyChunkScalars)
    {
        if (chunkScalars <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkScalars));
        var runes = value.EnumerateRunes().ToArray();
        var count = (runes.Length + chunkScalars - 1) / chunkScalars;
        for (var i = 0; i < count; i++)
        {
            var slice = runes.Skip(i * chunkScalars).Take(chunkScalars);
            var sb = new StringBuilder();
            foreach (var rune in slice)
                sb.Append(rune.ToString());
            properties[$"{prefix}_{i:D4}"] = sb.ToString();
        }
        properties[$"{prefix}_Count"] = count;
        properties[$"{prefix}_Scalars"] = runes.Length;
        properties[$"{prefix}_Sha256"] = DeliveryPlan.HashSource(value);
    }

    public static string Read(IReadOnlyDictionary<string, object?> properties, string prefix)
    {
        if (!properties.TryGetValue($"{prefix}_Count", out var countValue) ||
            !TryToInt(countValue, out var count) || count < 0)
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"segmented field '{prefix}' is missing or incomplete");
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (!properties.TryGetValue($"{prefix}_{i:D4}", out var chunk) || chunk is not string text)
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                    $"segmented field '{prefix}' is missing or incomplete");
            sb.Append(text);
        }
        var value = sb.ToString();
        if (!properties.TryGetValue($"{prefix}_Scalars", out var scalarValue) ||
            !TryToInt(scalarValue, out var scalars) || scalars != AnswerSourceBound.CountScalars(value))
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"segmented field '{prefix}' scalar count mismatch");
        if (!properties.TryGetValue($"{prefix}_Sha256", out var hash) ||
            hash is not string hex ||
            !string.Equals(hex, DeliveryPlan.HashSource(value), StringComparison.Ordinal))
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"segmented field '{prefix}' hash mismatch");
        return value;
    }

    public static bool TryToInt(object? value, out int result)
    {
        if (value is int i) { result = i; return true; }
        if (value is long l && l >= int.MinValue && l <= int.MaxValue) { result = (int)l; return true; }
        result = 0;
        return false;
    }
}

// Worst-case storage accounting. String properties are measured in UTF-16
// bytes plus names; scalar counts use Runes so supplementary characters are
// charged double. Entities must stay below 900 KiB of property data with at
// most 128 custom properties; batches stay far below the 4-MiB request cap.
public static class Phase3StorageBounds
{
    public const long MaxEntityPropertyBytes = 900 * 1024;
    public const int MaxCustomProperties = 128;
    public const long MaxTransactionBytes = 4 * 1024 * 1024;

    public static long PropertyBytes(string name, object? value)
    {
        var bytes = (long)Encoding.Unicode.GetByteCount(name) + 16;
        if (value is string text)
            return bytes + Encoding.Unicode.GetByteCount(text);
        return bytes + 8;
    }

    public static void CheckEntityFits(IReadOnlyDictionary<string, object?> properties, string what)
    {
        if (properties.Count > MaxCustomProperties)
            throw new InvalidOperationException(
                $"{what} needs {properties.Count} properties (limit {MaxCustomProperties})");
        var total = properties.Sum(p => PropertyBytes(p.Key, p.Value));
        if (total > MaxEntityPropertyBytes)
            throw new InvalidOperationException(
                $"{what} needs {total} property bytes (limit {MaxEntityPropertyBytes})");
    }

    public static void CheckBatchFits(IReadOnlyList<IReadOnlyDictionary<string, object?>> entities, string what)
    {
        foreach (var entity in entities)
            CheckEntityFits(entity, what);
        var total = entities.Sum(e => e.Sum(p => PropertyBytes(p.Key, p.Value)));
        if (total > MaxTransactionBytes)
            throw new InvalidOperationException(
                $"{what} transaction needs {total} bytes (limit {MaxTransactionBytes})");
    }
}

// JSON codec for plan rows. Ranges reference the immutable answer row; the
// content hash must agree with it. Legacy HTML compatibility plans are out
// of scope here: this codec round-trips schema-3 leaves only.
public static class PlanRowCodec
{
    public static string Serialize(DeliveryPlanDoc plan)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", plan.SchemaVersion);
            writer.WriteString("answer_version", plan.AnswerVersion);
            writer.WriteString("source_sha256", plan.SourceSha256);
            writer.WriteNumber("revision", plan.Revision);
            writer.WriteStartArray("leaves");
            foreach (var leaf in plan.Leaves)
            {
                writer.WriteStartObject();
                writer.WriteString("id", leaf.Id);
                writer.WriteString("kind", LeafKindName(leaf.Kind));
                writer.WriteNumber("start_utf16", leaf.StartUtf16);
                writer.WriteNumber("length_utf16", leaf.LengthUtf16);
                writer.WriteString("fallback_stage", StageName(leaf.FallbackStage));
                writer.WriteBoolean("confirmed", leaf.Confirmed);
                if (leaf.MessageId.HasValue)
                    writer.WriteNumber("message_id", leaf.MessageId.Value);
                else
                    writer.WriteNull("message_id");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static DeliveryPlanDoc Deserialize(string json, string source)
    {
        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                $"plan JSON is malformed: {ex.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            var leaves = new List<PlanLeaf>();
            try
            {
                foreach (var element in root.GetProperty("leaves").EnumerateArray())
                {
                    long? messageId = null;
                    if (element.TryGetProperty("message_id", out var id) &&
                        id.ValueKind == System.Text.Json.JsonValueKind.Number)
                        messageId = id.GetInt64();
                    leaves.Add(new PlanLeaf(
                        element.GetProperty("id").GetString() ?? string.Empty,
                        ParseKind(element.GetProperty("kind").GetString()),
                        element.GetProperty("start_utf16").GetInt32(),
                        element.GetProperty("length_utf16").GetInt32(),
                        ParseStage(element.GetProperty("fallback_stage").GetString()),
                        element.GetProperty("confirmed").GetBoolean(),
                        messageId));
                }
                var plan = new DeliveryPlanDoc(
                    root.GetProperty("schema_version").GetInt32(),
                    root.GetProperty("answer_version").GetString() ?? string.Empty,
                    root.GetProperty("source_sha256").GetString() ?? string.Empty,
                    root.GetProperty("revision").GetInt32(),
                    leaves);
                DeliveryPlan.Validate(plan, source);
                return plan;
            }
            catch (KeyNotFoundException ex)
            {
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                    $"plan JSON is incomplete: {ex.Message}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw new Phase3PayloadException(Phase3FailureCodes.PayloadCorrupt,
                    $"plan JSON is invalid: {ex.Message}");
            }
        }
    }

    public static string LeafKindName(PlanLeafKind kind) => kind switch
    {
        PlanLeafKind.Markdown => "markdown",
        PlanLeafKind.LiteralRich => "literal_rich",
        PlanLeafKind.LiteralPlain => "literal_plain",
        PlanLeafKind.LegacyHtml => "legacy_html",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string StageName(PlanFallbackStage stage) => stage switch
    {
        PlanFallbackStage.Native => "native",
        PlanFallbackStage.RichFull => "rich_full",
        PlanFallbackStage.RichSmall => "rich_small",
        PlanFallbackStage.Plain => "plain",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static PlanLeafKind ParseKind(string? name) => name switch
    {
        "markdown" => PlanLeafKind.Markdown,
        "literal_rich" => PlanLeafKind.LiteralRich,
        "literal_plain" => PlanLeafKind.LiteralPlain,
        "legacy_html" => PlanLeafKind.LegacyHtml,
        _ => throw new InvalidOperationException($"unknown leaf kind '{name}'"),
    };

    private static PlanFallbackStage ParseStage(string? name) => name switch
    {
        "native" => PlanFallbackStage.Native,
        "rich_full" => PlanFallbackStage.RichFull,
        "rich_small" => PlanFallbackStage.RichSmall,
        "plain" => PlanFallbackStage.Plain,
        _ => throw new InvalidOperationException($"unknown fallback stage '{name}'"),
    };
}

// Coordinated occurrence repository: every publication reads the current
// receipt, active operation, and memory, then commits atomically under the
// receipt claim. 409/412 conflicts reread and retry (at most eight local
// attempts) before surfacing as retryable storage conflicts.
public static class Phase3Times
{
    public static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}

// A same-occurrence/same-hash memory row is idempotent; a newer stored
// reply wins over an older receipt; anything else publishes.
public static class MemoryPublication
{
    public static MemoryRecord? Decide(
        MemoryRecord? memory, string ownerId, string operationId,
        DateTime scheduled, DateTime executed, AnswerArtifact answer, DateTimeOffset now)
    {
        if (memory is null)
            return new MemoryRecord(ownerId, operationId, scheduled, executed, now,
                answer.AnswerVersion, answer.Text, answer.SourceSha256, answer.ScalarCount);
        if (Phase3Times.Utc(memory.SourceScheduledUtc) > scheduled)
            return null;
        if (Phase3Times.Utc(memory.SourceScheduledUtc) == scheduled)
        {
            if (memory.SourceSha256 == answer.SourceSha256)
                return null;
            throw new Phase3ConsistencyException("memory row for the same occurrence has a different hash");
        }
        return memory with
        {
            SourceScheduledUtc = scheduled,
            SourceExecutedUtc = executed,
            PublishedUtc = now,
            AnswerVersion = answer.AnswerVersion,
            Answer = answer.Text,
            SourceSha256 = answer.SourceSha256,
            ScalarCount = answer.ScalarCount,
        };
    }
}
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
}

// Existing sent-parts/message-ID summary fields are derived from the plan
// and updated in the same transaction as the plan itself.
public static class DeliveryPlanProgress
{
    public static DeliveryReceipt ApplyToReceipt(DeliveryReceipt receipt, DeliveryPlanDoc plan)
    {
        var confirmed = plan.Leaves.TakeWhile(l => l.Confirmed).ToList();
        return receipt with
        {
            // Each progress write follows exactly one Telegram send, so the
            // attempt count is send telemetry, never a failure count.
            Attempts = receipt.Attempts + 1,
            SentParts = confirmed.Count,
            TotalParts = plan.Leaves.Count,
            MessageIds = string.Join(',', confirmed.Select(l => l.MessageId!.Value)),
            TelegramMessageId = confirmed.Count == 0 ? null : confirmed[^1].MessageId,
        };
    }
}
