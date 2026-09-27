// Answer-source bound and streaming accumulator: scalar counting, chunk-boundary runes, whitespace edge cases.
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class AnswerSourceTests
{

    [Fact]
    public void SourceBound_AcceptsExactly131072Scalars()
    {
        var canonical = new string('a', ExecutionLimits.AnswerSourceMaxScalars);
        Assert.Same(canonical, AnswerSourceBound.RequireWithinBound(canonical));
    }


    [Fact]
    public void SourceBound_RejectsOneScalarBeyondWithoutTruncation()
    {
        var canonical = new string('a', ExecutionLimits.AnswerSourceMaxScalars + 1);
        var ex = Assert.Throws<PayloadIntegrityException>(() => AnswerSourceBound.RequireWithinBound(canonical));
        Assert.Equal(OccurrenceFailureCodes.AnswerSourceLimit, ex.Code);
    }


    [Fact]
    public void Accumulator_CountsAcrossChunksWithoutSplittingScalars()
    {
        // Decoded SSE chunks never end mid-scalar; each chunk's runes count
        // once, including supplementary characters and combining sequences.
        var acc = new AnswerSourceAccumulator();
        acc.Append("a\U0001F600");
        acc.Append("e\u0301b");
        Assert.Equal(5, acc.EffectiveScalarCount);
        Assert.False(acc.IsOverLimit);
        Assert.Equal("a\U0001F600e\u0301b", acc.GetCanonical());
    }


    [Fact]
    public void Accumulator_EdgeWhitespaceCannotCauseFalseRejection()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append("   \n");
        acc.Append(new string('a', ExecutionLimits.AnswerSourceMaxScalars));
        acc.Append(new string(' ', 5000));
        Assert.False(acc.IsOverLimit);
        Assert.Equal(new string('a', ExecutionLimits.AnswerSourceMaxScalars), acc.GetCanonical());
    }


    [Fact]
    public void Accumulator_ContentBeyondBoundIsTerminal()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append(new string('b', ExecutionLimits.AnswerSourceMaxScalars));
        Assert.False(acc.IsOverLimit);
        acc.Append("c");
        Assert.True(acc.IsOverLimit);
        Assert.Throws<PayloadIntegrityException>(() => acc.GetCanonical());
    }


    [Fact]
    public void Accumulator_WhitespaceFloodStaysBoundedAndTrimsExactly()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append("x");
        acc.Append(new string(' ', 1_000_001));
        Assert.Equal(1, acc.EffectiveScalarCount);
        Assert.False(acc.IsOverLimit);
        Assert.True(acc.HasContent);
        Assert.Equal("x", acc.GetCanonical());
    }


    [Fact]
    public void Accumulator_ContentAfterExcessWhitespaceTripsSourceLimit()
    {
        var acc = new AnswerSourceAccumulator();
        acc.Append("x");
        acc.Append(new string(' ', ExecutionLimits.AnswerSourceMaxScalars + 2));
        var ex = Assert.Throws<PayloadIntegrityException>(() => acc.Append("y"));
        Assert.Equal(OccurrenceFailureCodes.AnswerSourceLimit, ex.Code);
    }
}
