using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class ListSplitterTests
{
    private static OperationSummary Op(string id, string text, OperationStatus status = OperationStatus.Active) =>
        new(id, "0 0 9 * * *", text, status);

    [Fact]
    public void EmptyList_YieldsNoMessages()
    {
        Assert.Empty(ListFormatter.Split([]));
    }

    [Fact]
    public void SingleSmallOp_OneMessage()
    {
        var msgs = ListFormatter.Split([Op("abc", "hi")]);
        Assert.Single(msgs);
        Assert.Contains("abc", msgs[0]);
        Assert.Contains("0 0 9 * * *", msgs[0]);
        Assert.Contains("hi", msgs[0]);
        Assert.Contains("active", msgs[0]);
    }

    [Fact]
    public void Messages_NeverExceed32768()
    {
        var ops = Enumerable.Range(0, 50)
            .Select(i => Op($"op{i:000}", new string('m', 1200)))
            .ToList();
        var msgs = ListFormatter.Split(ops);
        Assert.True(msgs.Count > 1);
        Assert.All(msgs, m => Assert.True(TextLimits.CountChars(m) <= 32768, $"len {m.Length}"));
        var joined = string.Join("\n\n", msgs);
        foreach (var op in ops)
            Assert.Contains(op.OperationId, joined);
    }

    [Fact]
    public void Ops_AreKeptTogether_WherePossible()
    {
        // Two blocks fit in one message only if combined <= 32768.
        var ops = new[] { Op("a", new string('x', 1900)), Op("b", new string('y', 1900)) };
        var blocks = ops.Select(ListFormatter.FormatBlock).ToArray();
        var msgs = ListFormatter.Split(ops);
        if (TextLimits.CountChars(blocks[0]) + 2 + TextLimits.CountChars(blocks[1]) <= 32768)
        {
            Assert.Single(msgs);
        }
        else
        {
            Assert.Equal(2, msgs.Count);
            Assert.Contains("a", msgs[0]);
            Assert.DoesNotContain("\n\nb\n", msgs[0]);
        }
    }

    [Fact]
    public void Boundary_TwoOps_FitExactly()
    {
        // Craft texts so both blocks plus separator fit exactly at the limit.
        var b1 = ListFormatter.FormatBlock(Op("id1", "t"));
        var roomForSecond = 32768 - TextLimits.CountChars(b1) - 2;
        var prefix = ListFormatter.FormatBlock(Op("id2", ""));
        var textLen = roomForSecond - TextLimits.CountChars(prefix);
        Assert.True(textLen > 0);
        var msgs = ListFormatter.Split([Op("id1", "t"), Op("id2", new string('z', textLen))]);
        Assert.Single(msgs);
        Assert.Equal(32768, TextLimits.CountChars(msgs[0]));
    }

    [Fact]
    public void OversizedSingleOp_IsHardSplit()
    {
        var msgs = ListFormatter.Split([Op("big", new string('q', 70000))]);
        Assert.True(msgs.Count >= 3);
        Assert.All(msgs, m => Assert.True(TextLimits.CountChars(m) <= 32768));
        Assert.Equal(70000 + TextLimits.CountChars("big\nSchedule: 0 0 9 * * * (UTC)\nStatus: active\n"),
            msgs.Sum(m => TextLimits.CountChars(m)));
    }

    [Fact]
    public void FailedStatus_ShowsInList()
    {
        var msgs = ListFormatter.Split([Op("f1", "x", OperationStatus.Failed)]);
        Assert.Contains("failed", msgs[0]);
    }
}
