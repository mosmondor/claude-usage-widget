using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class PricingModelTests
{
    [Theory]
    [InlineData("claude-haiku-4-5-20251001", "haiku-4-5")]
    [InlineData("claude-fable-5", "fable-5")]
    [InlineData("claude-opus-4-8", "opus-4-8")]
    [InlineData("claude-sonnet-5", "sonnet-5")]
    [InlineData("claude-3-5-haiku-20241022", "3-5-haiku")]
    public void Normalize_StripsPrefixAndDateSuffix(string raw, string expected)
    {
        Assert.Equal(expected, ModelNames.Normalize(raw));
    }

    [Theory]
    [InlineData("claude-fable-5", true)]
    [InlineData("Claude-Opus-4-8", true)]
    [InlineData("gpt-5.5", false)]
    [InlineData("glm-4.7", false)]
    [InlineData("", false)]
    public void IsClaude_Works(string raw, bool expected)
    {
        Assert.Equal(expected, ModelNames.IsClaude(raw));
    }

    [Fact]
    public void Pricing_TiersByFamily()
    {
        Assert.Equal(0.80, Pricing.For("haiku-4-5").Input, 3);
        Assert.Equal(3.0, Pricing.For("sonnet-5").Input, 3);
        Assert.Equal(15.0, Pricing.For("opus-4-8").Input, 3);
        Assert.Equal(15.0, Pricing.For("fable-5").Input, 3); // fable priced as opus tier
    }

    [Fact]
    public void Cost_UsesAllFourComponents()
    {
        Agg a = new Agg { In = 1_000_000, Out = 1_000_000, CacheWrite = 1_000_000, CacheRead = 1_000_000 };
        // opus tier: 15 + 75 + 18.75 + 1.5
        Assert.Equal(110.25, Pricing.Cost("opus-4-8", a), 4);
    }
}
