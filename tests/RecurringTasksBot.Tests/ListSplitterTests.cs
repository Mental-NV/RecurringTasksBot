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
    public void Messages_NeverExceed4096()
    {
        var ops = Enumerable.Range(0, 50)
            .Select(i => Op($"op{i:000}", new string('m', 300)))
            .ToList();
        var msgs = ListFormatter.Split(ops);
        Assert.True(msgs.Count > 1);
        Assert.All(msgs, m => Assert.True(m.Length <= 4096, $"len {m.Length}"));
        var joined = string.Join("\n\n", msgs);
        foreach (var op in ops)
            Assert.Contains(op.OperationId, joined);
    }

    [Fact]
    public void Ops_AreKeptTogether_WherePossible()
    {
        // Each op ~2000 chars: two fit in one message only if combined <= 4096.
        var ops = new[] { Op("a", new string('x', 1900)), Op("b", new string('y', 1900)) };
        var blocks = ops.Select(ListFormatter.FormatBlock).ToArray();
        var msgs = ListFormatter.Split(ops);
        if (blocks[0].Length + 2 + blocks[1].Length <= 4096)
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
        var roomForSecond = 4096 - b1.Length - 2;
        var prefix = ListFormatter.FormatBlock(Op("id2", ""));
        var textLen = roomForSecond - prefix.Length;
        Assert.True(textLen > 0);
        var msgs = ListFormatter.Split([Op("id1", "t"), Op("id2", new string('z', textLen))]);
        Assert.Single(msgs);
        Assert.Equal(4096, msgs[0].Length);
    }

    [Fact]
    public void OversizedSingleOp_IsHardSplit()
    {
        var msgs = ListFormatter.Split([Op("big", new string('q', 9000))]);
        Assert.True(msgs.Count >= 3);
        Assert.All(msgs, m => Assert.True(m.Length <= 4096));
        Assert.Equal(9000 + "big\nSchedule: 0 0 9 * * * (UTC)\nStatus: active\n".Length,
            msgs.Sum(m => m.Length));
    }

    [Fact]
    public void FailedStatus_ShowsInList()
    {
        var msgs = ListFormatter.Split([Op("f1", "x", OperationStatus.Failed)]);
        Assert.Contains("failed", msgs[0]);
    }
}
