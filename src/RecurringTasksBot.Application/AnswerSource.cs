// Answer source bound: accepted answers are preserved exactly and
// measured in Unicode scalar values so supplementary characters are
// charged once. The streaming accumulator enforces the same bound while
// accumulating content.
using System.Text;

namespace RecurringTasksBot.Application;

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
        RequireWithinBound(canonical, ExecutionLimits.AnswerSourceMaxScalars);

    public static string RequireWithinBound(string canonical, int maxScalars)
    {
        if (CountScalars(canonical) > maxScalars)
            throw new PayloadIntegrityException(OccurrenceFailureCodes.AnswerSourceLimit,
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
public sealed class AnswerSourceAccumulator(int maxScalars = ExecutionLimits.AnswerSourceMaxScalars)
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
                    throw new PayloadIntegrityException(OccurrenceFailureCodes.AnswerSourceLimit,
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
