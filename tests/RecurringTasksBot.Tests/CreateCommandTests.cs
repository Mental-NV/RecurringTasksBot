using RecurringTasksBot.Core;

namespace RecurringTasksBot.Tests;

public sealed class CreateCommandTests
{
    private static readonly DateTime Now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc); // a Monday

    private static bool Try(string? text, out CreateCommand? cmd, out string? err) =>
        CreateCommandParser.TryParse(text, Now, out cmd, out err);

    [Fact]
    public void ValidCreate_PreservesText()
    {
        Assert.True(Try("/create 0 0 9 * * * Water the plants", out var cmd, out var err));
        Assert.NotNull(cmd);
        Assert.Equal("0 0 9 * * *", cmd.CronExpression);
        Assert.Equal("Water the plants", cmd.Text);
    }

    [Fact]
    public void ValidCreate_PreservesSpacesAndLineBreaks()
    {
        Assert.True(Try("/create 0 0 9 * * *  Water   the\nplants  ", out var cmd, out _));
        Assert.NotNull(cmd);
        Assert.Contains("Water   the\nplants", cmd.Text);
    }

    [Theory]
    [InlineData("/create 0 9 * * Water")]           // five fields
    [InlineData("/create not a cron at all here")]
    [InlineData("/create 0 0 9 * *")]               // five fields + no text
    [InlineData("/create")]
    [InlineData("")]
    [InlineData(null)]
    public void BadShape_IsRejected(string? text)
    {
        Assert.False(Try(text, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void SixFields_PlusText_WithExtraFieldText_IsAccepted()
    {
        // 7th token is message text, not a 7th cron field.
        Assert.True(Try("/create 0 0 9 * * * extra", out var cmd, out _));
        Assert.Equal("extra", cmd!.Text);
    }

    [Theory]
    [InlineData("/create 0 0 9 * * * ok", true)]
    [InlineData("/create 30 0 9 * * * no-seconds-must-be-zero", false)]
    [InlineData("/create */15 0 9 * * * nonzero-step-seconds", false)]
    public void SecondsMustBeZero(string text, bool expected)
    {
        Assert.Equal(expected, Try(text, out _, out var err));
        if (!expected)
            Assert.Contains("seconds must be 0", err, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/create 0 0 25 * * * x")]     // hour 25
    [InlineData("/create 0 61 9 * * * x")]     // minute 61
    [InlineData("/create 0 0 9 * 13 * x")]     // month 13
    [InlineData("/create 0 0 9 * * FOO x")]    // bad weekday
    [InlineData("/create 0-5 * * * * * x")]    // seconds range, also nonzero
    [InlineData("/create * * * * *")]          // five fields
    public void BadNcrontab_IsRejected(string text)
    {
        Assert.False(Try(text, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void ImpossibleDate_HasNoFutureOccurrence()
    {
        // Feb 30 never occurs.
        Assert.False(Try("/create 0 0 9 30 2 * impossible", out _, out var err));
        Assert.Contains("no future occurrence", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeapDay_HasFutureOccurrence()
    {
        Assert.True(Try("/create 0 0 9 29 2 * leap day", out var cmd, out _));
        Assert.NotNull(cmd!.Schedule.GetNextOccurrence(Now));
    }

    [Fact]
    public void EmptyText_IsRejected()
    {
        Assert.False(Try("/create 0 0 9 * * *    ", out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void TextOver2000Chars_IsRejected()
    {
        var text = "/create 0 0 9 * * * " + new string('x', 2001);
        Assert.False(Try(text, out _, out var err));
        Assert.Contains("2000", err);
    }

    [Fact]
    public void TextExactly2000Chars_IsAccepted()
    {
        var text = "/create 0 0 9 * * * " + new string('x', 2000);
        Assert.True(Try(text, out var cmd, out _));
        Assert.Equal(2000, cmd!.Text.Length);
    }

    [Fact]
    public void SingleCharText_IsAccepted()
    {
        Assert.True(Try("/create 0 0 9 * * * x", out var cmd, out _));
        Assert.Equal("x", cmd!.Text);
    }

    [Fact]
    public void Verb_IsCaseInsensitive()
    {
        Assert.True(Try("/CREATE 0 0 9 * * * hi", out _, out _));
    }

    [Fact]
    public void UnknownVerb_ReportsHelp()
    {
        Assert.False(Try("/creat 0 0 9 * * * hi", out _, out var err));
        Assert.Contains("Unknown command", err);
    }
}
