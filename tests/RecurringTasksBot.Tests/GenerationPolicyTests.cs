// Generation policy: HTTP classification, retry budgets, delays, and key redaction.
using RecurringTasksBot.Application;

namespace RecurringTasksBot.Tests;

public sealed class GenerationPolicyTests
{
    [Theory]
    [InlineData(408, LlmFailureKind.Transient)]
    [InlineData(429, LlmFailureKind.Transient)]
    [InlineData(500, LlmFailureKind.Transient)]
    [InlineData(503, LlmFailureKind.Transient)]
    [InlineData(401, LlmFailureKind.Permanent)]
    [InlineData(402, LlmFailureKind.Permanent)]
    [InlineData(400, LlmFailureKind.Permanent)]
    public void HttpClassification(int status, LlmFailureKind expected)
        {
        Assert.Equal(expected, GenerationPolicy.ClassifyHttpStatus(status));
    }

    [Fact]
    public void Retries_AtMostTwice()
        {
        Assert.True(GenerationPolicy.ShouldRetry(LlmFailureKind.Transient, 1));
        Assert.True(GenerationPolicy.ShouldRetry(LlmFailureKind.Transient, 2));
        Assert.False(GenerationPolicy.ShouldRetry(LlmFailureKind.Transient, 3));
        Assert.False(GenerationPolicy.ShouldRetry(LlmFailureKind.EmptyResponse, 3));
        Assert.False(GenerationPolicy.ShouldRetry(LlmFailureKind.Permanent, 1));
    }

    [Fact]
    public void RetryDelay_Increases_HonoursRetryAfter()
        {
        var first = GenerationPolicy.RetryDelay(0);
        var second = GenerationPolicy.RetryDelay(1);
        Assert.True(first < second);
        Assert.Equal(TimeSpan.FromSeconds(120),
            GenerationPolicy.RetryDelay(0, TimeSpan.FromSeconds(120)));
        Assert.Equal(first, GenerationPolicy.RetryDelay(0, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Sanitize_RedactsKeyMaterial()
        {
        var summary = GenerationPolicy.Sanitize("failed with sk-or-v1-abcdef1234567890 tail");
        Assert.DoesNotContain("abcdef1234567890", summary);
        Assert.True(summary.Length <= 280);
    }

    [Fact]
    public void OptionsValidation_FailsClearly()
        {
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Provider() with { Provider = "Other" }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Provider() with { Model = "" }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Provider() with { ReasoningEffort = "" }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Execution() with { CompletionTokenBudget = 0 }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (TestLlm.Execution() with { GenerationRetries = -1 }).Validate());
    }
}
