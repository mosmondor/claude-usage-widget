using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class UsageApiParserTests
{
    private const string Sample = @"{
      ""five_hour"": { ""utilization"": 14.0, ""resets_at"": ""2026-07-24T22:29:59+00:00"" },
      ""seven_day"": { ""utilization"": 14.0, ""resets_at"": ""2026-07-26T20:59:59+00:00"" },
      ""limits"": [
        { ""kind"": ""session"", ""group"": ""session"", ""percent"": 14, ""severity"": ""normal"", ""resets_at"": ""2026-07-24T22:29:59+00:00"" },
        { ""kind"": ""weekly_all"", ""group"": ""weekly"", ""percent"": 14, ""severity"": ""normal"", ""resets_at"": ""2026-07-26T20:59:59+00:00"" },
        { ""kind"": ""weekly_scoped"", ""group"": ""weekly"", ""percent"": 18, ""severity"": ""normal"", ""resets_at"": ""2026-07-26T20:59:59+00:00"", ""scope"": { ""model"": { ""display_name"": ""Fable"" } } }
      ],
      ""spend"": { ""percent"": 8, ""severity"": ""normal"", ""enabled"": true }
    }";

    [Fact]
    public void ParsesAllLimitBars()
    {
        List<LimitRow> rows = UsageApiParser.Parse(Sample);
        Assert.Equal(4, rows.Count);

        Assert.Equal("Session  ·  5h", rows[0].Label);
        Assert.Equal(14, rows[0].Percent);
        Assert.NotNull(rows[0].ResetsAt);

        Assert.Equal("Weekly  ·  all models", rows[1].Label);
        Assert.Equal(14, rows[1].Percent);

        Assert.Equal("Weekly  ·  Fable", rows[2].Label);
        Assert.Equal(18, rows[2].Percent);

        Assert.Equal("Extra credits", rows[3].Label);
        Assert.Equal(8, rows[3].Percent);
    }

    [Fact]
    public void FloatPercentIsRounded()
    {
        List<LimitRow> rows = UsageApiParser.Parse(@"{ ""limits"": [ { ""kind"": ""session"", ""percent"": 13.6 } ] }");
        Assert.Single(rows);
        Assert.Equal(14, rows[0].Percent);
    }

    [Fact]
    public void DisabledSpendIsOmitted()
    {
        List<LimitRow> rows = UsageApiParser.Parse(@"{ ""limits"": [], ""spend"": { ""percent"": 8, ""enabled"": false } }");
        Assert.Empty(rows);
    }

    [Fact]
    public void EmptyOrJunkDoesNotThrow()
    {
        Assert.Empty(UsageApiParser.Parse("{}"));
        Assert.Empty(UsageApiParser.Parse("[]"));
    }
}
