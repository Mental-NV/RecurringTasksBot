// Phase 3 protocol constants, answer source bound, streaming accumulator,
// and the text-only capability gate. Pure and dependency-free; Core stays
// independent of Azure/HTTP SDK types.
using System.Text;

namespace RecurringTasksBot.Core;

public static class Phase3Limits
{
    public const int RichTextChars = 32768;
    public const int RichBlocks = 500;
    public const int RichNestingLevels = 16;
    public const int RichTableColumns = 20;
    public const int AnswerSourceMaxScalars = 131072;
    public const int SystemInstructionMaxScalars = 16384;
    public const int PropertyChunkScalars = 16000;
    public const int PlainFallbackMaxUnits = 4096;
    public const int MaxPlanLeaves = 128;
    public const int LiteralNewlineWindow = 512;
    public const int SchemaVersion = 3;
    public const string InstructionVersion = "phase3-v1";
    public const string ScheduleTimezone = "UTC";
    public const string EnabledCapabilities = "rich_text, formulas, details, links, unicode_symbols";

    public static readonly TimeSpan ActivityWorkBudget = TimeSpan.FromSeconds(540);
    public static readonly TimeSpan TelegramRequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TelegramProgressReserve = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MaxLlmTimeout = TimeSpan.FromSeconds(510);
}

public static class Phase3FailureCodes
{
    public const string AnswerSourceLimit = "answer_source_limit";
    public const string AnswerIncomplete = "answer_incomplete";
    public const string ContextBudgetExceeded = "context_budget_exceeded";
    public const string DeliveryPlanLimit = "delivery_plan_limit";
    public const string PayloadCorrupt = "payload_corrupt";
    public const string UnsupportedPayloadVersion = "unsupported_payload_version";
}

public sealed class Phase3PayloadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Application-generated terminal generation failure notice. Literal text:
// no IDs, timestamps, provider errors, or sources. Never becomes memory.
public static class Phase3DeliveryText
{
    public const string FailureNotice = "This run failed. Future runs remain scheduled.";
}

public static class AnswerSourceBound
{
    public static int CountScalars(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    public static string Canonicalize(string answer) => answer.Trim();

    // Accepted answers are preserved exactly; never truncated here.
    public static string RequireWithinBound(string canonical) =>
        RequireWithinBound(canonical, Phase3Limits.AnswerSourceMaxScalars);

    public static string RequireWithinBound(string canonical, int maxScalars)
    {
        if (CountScalars(canonical) > maxScalars)
            throw new Phase3PayloadException(Phase3FailureCodes.AnswerSourceLimit,
                $"answer exceeds {maxScalars} Unicode scalar values");
        return canonical;
    }
}

// Enforces the source bound while accumulating SSE visible content as well
// as on the final response. Scalar counts span chunk boundaries (runes are
// never split), and leading/trailing whitespace discarded by the canonical
// outer trim cannot cause a false rejection: only the effective lower bound
// (total minus edge whitespace) can trip the limit, and that bound grows
// monotonically, so a trip is terminal. Retention is bounded: at most the
// bound plus one effective scalar plus a bounded pending-whitespace run is
// ever stored. A pending run beyond the cap is counted, not retained; stream
// content arriving after dropped whitespace cannot be reconstructed exactly,
// so it trips the same source limit (the final canonical text would exceed
// the bound as interior whitespace). A stream that ends instead trims the
// pending run away exactly.
public sealed class AnswerSourceAccumulator(int maxScalars = Phase3Limits.AnswerSourceMaxScalars)
{
    private readonly int _maxScalars = maxScalars;
    private readonly StringBuilder _buffer = new();
    private int _totalScalars;
    private int _leadingWsScalars;
    private int _pendingWsScalars;
    private int _droppedWsScalars;
    private bool _seenContent;

    public void Append(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;
        foreach (var rune in chunk.EnumerateRunes())
        {
            _totalScalars++;
            if (!_seenContent && Rune.IsWhiteSpace(rune))
            {
                _leadingWsScalars++;
                continue;
            }
            _seenContent = true;
            if (!Rune.IsWhiteSpace(rune))
            {
                if (_droppedWsScalars > 0)
                    throw new Phase3PayloadException(Phase3FailureCodes.AnswerSourceLimit,
                        "answer whitespace run exceeds the source bound");
                _pendingWsScalars = 0;
                _buffer.Append(rune.ToString());
                continue;
            }
            _pendingWsScalars++;
            if (_pendingWsScalars <= _maxScalars + 1)
                _buffer.Append(rune.ToString());
            else
                _droppedWsScalars++;
        }
    }

    public bool HasContent => _seenContent;

    public int EffectiveScalarCount => _totalScalars - _leadingWsScalars - _pendingWsScalars;

    public bool IsOverLimit => EffectiveScalarCount > _maxScalars;

    public string GetCanonical() =>
        AnswerSourceBound.RequireWithinBound(_buffer.ToString().Trim(), _maxScalars);
}

// Conservative pre-send source scan. A hit delivers the entire answer as
// literal text; there is no partial stripping or format conversion. The scan
// intentionally also matches examples inside code blocks.
public static class Phase3CapabilityGate
{
    // Exact HTML tag names need a terminator; Telegram tg- tags are blocked
    // by prefix, so <tg-map-view /> and any longer tg- name stay literal.
    private static readonly System.Text.RegularExpressions.Regex BlockedTag = new(
        @"<\s*(?:(img|video|audio|source|iframe|script)(?=[\s/>])|tg-(?:button|map|collage|slideshow|document|emoji|thinking))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool RequiresLiteral(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
            return false;
        if (answer.Contains("![", StringComparison.Ordinal))
            return true;
        return BlockedTag.IsMatch(answer);
    }
}
