using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class HistoryReaderTests
{
    private static string WriteHistory(string[] lines)
    {
        string path = Path.Combine(TestUtil.NewTempDir(), "history.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void FirstAndLastPrompt_IgnoreSlashCommands()
    {
        // the real failure mode: a session closed with /exit would be labelled "/exit"
        DateTime t = new DateTime(2026, 7, 20, 10, 0, 0);
        string sid = TestUtil.Uuid(1);
        string path = WriteHistory(new string[]
        {
            TestUtil.HistoryLine(sid, "C:\\Projects\\admin", "napravi mi launcher", t),
            TestUtil.HistoryLine(sid, "C:\\Projects\\admin", "/compact", t.AddMinutes(5)),
            TestUtil.HistoryLine(sid, "C:\\Projects\\admin", "prosao je, commitaj", t.AddMinutes(10)),
            TestUtil.HistoryLine(sid, "C:\\Projects\\admin", "/exit", t.AddMinutes(15))
        });

        List<SessionEntry> rows = HistoryReader.Read(path);

        Assert.Single(rows);
        Assert.Equal("napravi mi launcher", rows[0].FirstPrompt);
        Assert.Equal("prosao je, commitaj", rows[0].LastPrompt);
        Assert.Equal(4, rows[0].Prompts);
        Assert.Equal(t.AddMinutes(15), rows[0].LastActivity);
        Assert.Equal("admin", rows[0].ProjectName);
    }

    [Fact]
    public void OnlySlashCommands_StillProducesALabel()
    {
        DateTime t = new DateTime(2026, 7, 20, 10, 0, 0);
        string path = WriteHistory(new string[]
        {
            TestUtil.HistoryLine(TestUtil.Uuid(2), "C:\\Projects\\x", "/exit", t)
        });

        List<SessionEntry> rows = HistoryReader.Read(path);

        Assert.Single(rows);
        Assert.Equal("/exit", rows[0].FirstPrompt);   // better than an empty row
        Assert.Equal("/exit", rows[0].LastPrompt);
    }

    [Fact]
    public void TopicSkipsPlaceholdersAndFillerButLastPromptDoesNot()
    {
        // real labels seen in the wild: "[Pasted text #1 +75 lines]", "!start .", "continue"
        DateTime t = new DateTime(2026, 7, 20, 10, 0, 0);
        string sid = TestUtil.Uuid(20);
        string path = WriteHistory(new string[]
        {
            TestUtil.HistoryLine(sid, "C:\\p\\a", "[Pasted text #1 +75 lines]", t),
            TestUtil.HistoryLine(sid, "C:\\p\\a", "continue", t.AddMinutes(1)),
            TestUtil.HistoryLine(sid, "C:\\p\\a", "!git status", t.AddMinutes(2)),
            TestUtil.HistoryLine(sid, "C:\\p\\a", "deployaj fireplay-hub na webapps", t.AddMinutes(3)),
            TestUtil.HistoryLine(sid, "C:\\p\\a", "commitaj", t.AddMinutes(4))
        });

        List<SessionEntry> rows = HistoryReader.Read(path);

        Assert.Equal("deployaj fireplay-hub na webapps", rows[0].FirstPrompt);  // first prompt with substance
        Assert.Equal("commitaj", rows[0].LastPrompt);                          // short, but it IS where I stopped
    }

    [Fact]
    public void NoiseAndDescriptiveRules()
    {
        Assert.True(HistoryReader.IsNoise("/exit"));
        Assert.True(HistoryReader.IsNoise("!git status"));
        Assert.True(HistoryReader.IsNoise("[Pasted text #1 +75 lines]"));
        Assert.True(HistoryReader.IsNoise("[Image #2]"));
        Assert.True(HistoryReader.IsNoise(""));
        Assert.False(HistoryReader.IsNoise("napravi launcher"));

        Assert.False(HistoryReader.IsDescriptive("continue"));   // filler
        Assert.False(HistoryReader.IsDescriptive("ok"));
        Assert.False(HistoryReader.IsDescriptive("/compact"));
        Assert.True(HistoryReader.IsDescriptive("napravi mi launcher"));
    }

    [Fact]
    public void AllNoiseFallsBackRatherThanShowingNothing()
    {
        DateTime t = new DateTime(2026, 7, 20, 10, 0, 0);
        string sid = TestUtil.Uuid(21);
        string path = WriteHistory(new string[]
        {
            TestUtil.HistoryLine(sid, "C:\\p\\a", "[Pasted text #1 +9 lines]", t),
            TestUtil.HistoryLine(sid, "C:\\p\\a", "ok", t.AddMinutes(1))
        });

        List<SessionEntry> rows = HistoryReader.Read(path);

        Assert.Equal("ok", rows[0].FirstPrompt);   // not descriptive, but not noise either -> better than blank
        Assert.Equal("ok", rows[0].LastPrompt);
    }

    [Fact]
    public void Normalize_CollapsesWhitespaceAndCaps()
    {
        Assert.Equal("a b c", HistoryReader.Normalize("  a\r\n  b\t\tc  "));
        Assert.Equal("", HistoryReader.Normalize(""));
        Assert.Equal(HistoryReader.MaxLabel, HistoryReader.Normalize(new string('x', 500)).Length);
    }

    [Fact]
    public void SessionsAreGroupedByIdAndSortedNewestFirst()
    {
        DateTime t = new DateTime(2026, 7, 1, 8, 0, 0);
        string a = TestUtil.Uuid(3);
        string b = TestUtil.Uuid(4);
        string path = WriteHistory(new string[]
        {
            TestUtil.HistoryLine(a, "C:\\p\\a", "stara sesija", t),
            TestUtil.HistoryLine(b, "C:\\p\\b", "nova sesija", t.AddDays(5)),
            TestUtil.HistoryLine(a, "C:\\p\\a", "jos malo", t.AddMinutes(1))
        });

        List<SessionEntry> rows = HistoryReader.Read(path);

        Assert.Equal(2, rows.Count);
        Assert.Equal(b, rows[0].SessionId);   // newest first
        Assert.Equal(a, rows[1].SessionId);
        Assert.Equal(2, rows[1].Prompts);
    }

    [Fact]
    public void OnlyTheTailIsRead()
    {
        DateTime t = new DateTime(2026, 7, 20, 10, 0, 0);
        string path = WriteHistory(new string[]
        {
            TestUtil.HistoryLine(TestUtil.Uuid(5), "C:\\p\\old", "davno", t),
            TestUtil.HistoryLine(TestUtil.Uuid(6), "C:\\p\\mid", "srednje", t.AddHours(1)),
            TestUtil.HistoryLine(TestUtil.Uuid(7), "C:\\p\\new", "nedavno", t.AddHours(2))
        });

        List<SessionEntry> rows = HistoryReader.Read(path, 2);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.ProjectName == "old");
    }

    [Fact]
    public void JunkAndIncompleteLinesAreSkipped()
    {
        DateTime t = new DateTime(2026, 7, 20, 10, 0, 0);
        string path = WriteHistory(new string[]
        {
            "not json at all",
            "{\"display\":\"bez sessionId-a\",\"timestamp\":1784994955070}",
            "{\"sessionId\":\"" + TestUtil.Uuid(8) + "\",\"project\":\"C:\\\\p\",\"display\":\"\",\"timestamp\":1}",
            "",
            TestUtil.HistoryLine(TestUtil.Uuid(9), "C:\\p\\ok", "jedini dobar", t)
        });

        List<SessionEntry> rows = HistoryReader.Read(path);

        Assert.Single(rows);
        Assert.Equal("jedini dobar", rows[0].FirstPrompt);
    }

    [Fact]
    public void MissingFileIsNotAnError()
    {
        Assert.Empty(HistoryReader.Read(Path.Combine(TestUtil.NewTempDir(), "nope.jsonl")));
    }
}
