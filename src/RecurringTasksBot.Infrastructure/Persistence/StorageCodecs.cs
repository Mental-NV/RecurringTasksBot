// Table storage codecs: the segmented-property codec, storage bounds
// accounting, the plan-row JSON codec, and strict row readers shared by
// the operation codec and the occurrence transaction builders. Moved from
// Application: Application keeps pure records and transitions, while all
// Table-specific encoding lives here.
using System.Text;
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Infrastructure.Persistence;

// Strict readers for persisted rows. Missing or mistyped properties are
// corruption, never defaults; unknown schemas fail before
// generation/sending. This is integrity validation, not migration.
public static class TableRow
{
    public static void RequireKind(IDictionary<string, object?> props, string rowKey, string kind)
    {
        if (!props.TryGetValue("EntityKind", out var k) || (string?)k != kind)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                $"{rowKey} is not a {kind} row");
        if (!props.TryGetValue("SchemaVersion", out var v) ||
            !SegmentedProperties.TryToInt(v, out var schema))
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                $"{rowKey} is missing its schema version");
        if (schema != StorageLimits.SchemaVersion)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.UnsupportedPayloadVersion,
                $"{rowKey} has schema version {schema}");
    }

    public static DateTimeOffset GetDto(IDictionary<string, object?> props, string name, string rowKey)
    {
        if (props.TryGetValue(name, out var value))
        {
            if (value is DateTimeOffset dto)
                return dto;
            if (value is DateTime dt)
                return new DateTimeOffset(UtcTime.Utc(dt));
        }
        throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
            $"{rowKey} is missing '{name}'");
    }

    public static string GetString(IDictionary<string, object?> props, string name, string rowKey)
    {
        if (props.TryGetValue(name, out var value) && value is string text)
            return text;
        throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
            $"{rowKey} is missing '{name}'");
    }

    public static int GetInt(IDictionary<string, object?> props, string name, string rowKey)
    {
        if (props.TryGetValue(name, out var value) && SegmentedProperties.TryToInt(value, out var i))
            return i;
        throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
            $"{rowKey} is missing '{name}'");
    }

    public static long GetInt64(IDictionary<string, object?> props, string name, string rowKey)
    {
        if (props.TryGetValue(name, out var value) && value is int or long)
            return Convert.ToInt64(value);
        throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
            $"{rowKey} is missing '{name}'");
    }

    public static string? NullIfEmpty(string text) =>
        string.IsNullOrEmpty(text) ? null : text;
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
        int chunkScalars = StorageLimits.PropertyChunkScalars)
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
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                $"segmented field '{prefix}' is missing or incomplete");
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (!properties.TryGetValue($"{prefix}_{i:D4}", out var chunk) || chunk is not string text)
                throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    $"segmented field '{prefix}' is missing or incomplete");
            sb.Append(text);
        }
        var value = sb.ToString();
        if (!properties.TryGetValue($"{prefix}_Scalars", out var scalarValue) ||
            !TryToInt(scalarValue, out var scalars) || scalars != AnswerSourceBound.CountScalars(value))
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                $"segmented field '{prefix}' scalar count mismatch");
        if (!properties.TryGetValue($"{prefix}_Sha256", out var hash) ||
            hash is not string hex ||
            !string.Equals(hex, DeliveryPlan.HashSource(value), StringComparison.Ordinal))
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
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
public static class TableStorageLimits
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
// content hash must agree with it. This codec round-trips schema-1 leaves
// only; unknown leaf kinds fail as corrupt payloads.
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
            throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
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
                throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    $"plan JSON is incomplete: {ex.Message}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw new PayloadIntegrityException(OccurrenceFailureCodes.PayloadCorrupt,
                    $"plan JSON is invalid: {ex.Message}");
            }
        }
    }

    public static string LeafKindName(PlanLeafKind kind) => kind switch
    {
        PlanLeafKind.Markdown => "markdown",
        PlanLeafKind.LiteralRich => "literal_rich",
        PlanLeafKind.LiteralPlain => "literal_plain",
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
