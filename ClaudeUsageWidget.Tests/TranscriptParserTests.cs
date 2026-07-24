using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class TranscriptParserTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    // fixed +02:00 zone, independent of the test machine
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("t+2", TimeSpan.FromHours(2), "t+2", "t+2");

    [Theory]
    [InlineData("2026-07-24T23:30:00Z", "2026-07-25")]   // 23:30 UTC -> 01:30 next day in +2
    [InlineData("2026-07-24T10:00:00Z", "2026-07-24")]
    [InlineData("2026-07-31T23:00:00Z", "2026-08-01")]   // month rollover in +2
    public void DateInZone_ConvertsToTargetZone(string iso, string expected)
    {
        Assert.Equal(expected, TranscriptParser.DateInZone(iso, Plus2));
    }

    [Fact]
    public void DateInZone_Utc()
    {
        Assert.Equal("2026-07-24", TranscriptParser.DateInZone("2026-07-24T23:30:00Z", Utc));
    }

    [Fact]
    public void DateInZone_InvalidOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", TranscriptParser.DateInZone("", Utc));
        Assert.Equal("", TranscriptParser.DateInZone("not-a-date", Utc));
    }

    [Fact]
    public void ParsesClaudeAssistantUsageLine()
    {
        string line = TestUtil.Line("msg_1", "claude-fable-5", "2026-07-24T10:00:00Z", 100, 200, 3000, 40);
        UsageRecord r = TranscriptParser.TryParse(line, Utc);
        Assert.NotNull(r);
        Assert.Equal("msg_1", r.Id);
        Assert.Equal("fable-5", r.Model);
        Assert.Equal("2026-07-24", r.DateLocal);
        Assert.Equal(100, r.In);
        Assert.Equal(200, r.Out);
        Assert.Equal(3000, r.CacheRead);
        Assert.Equal(40, r.CacheWrite);
        Assert.Equal(3340, r.Total);
    }

    [Fact]
    public void SkipsNonClaudeModels()
    {
        string line = TestUtil.Line("msg_2", "gpt-5.5", "2026-07-24T10:00:00Z", 100, 200, 0, 0);
        Assert.Null(TranscriptParser.TryParse(line, Utc));
    }

    [Fact]
    public void SkipsLinesWithoutUsage()
    {
        Assert.Null(TranscriptParser.TryParse(@"{""type"":""user"",""message"":{""role"":""user""}}", Utc));
        Assert.Null(TranscriptParser.TryParse("garbage output_tokens but not json", Utc));
    }

    [Fact]
    public void SkipsZeroTokenRecords()
    {
        string line = TestUtil.Line("msg_3", "claude-opus-4-8", "2026-07-24T10:00:00Z", 0, 0, 0, 0);
        Assert.Null(TranscriptParser.TryParse(line, Utc));
    }
}
