using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class SessionsReaderTests
{
    private static Func<int, bool> Alive(params int[] pids)
    {
        HashSet<int> set = new HashSet<int>(pids);
        return new Func<int, bool>(pid => set.Contains(pid));
    }

    [Fact]
    public void ReadsLiveSessionsAndSortsByUpdatedDesc()
    {
        string dir = TestUtil.NewTempDir();
        DateTime t = new DateTime(2026, 7, 25, 17, 0, 0);
        TestUtil.WriteSessionFile(dir, 100, TestUtil.Uuid(1), "C:\\Projects\\admin", "busy", t.AddMinutes(55));
        TestUtil.WriteSessionFile(dir, 200, TestUtil.Uuid(2), "C:\\Projects\\fireplay", "idle", t);

        List<LiveSession> rows = SessionsReader.Read(dir, Alive(100, 200));

        Assert.Equal(2, rows.Count);
        Assert.Equal(100, rows[0].Pid);              // most recently updated first
        Assert.Equal("busy", rows[0].Status);
        Assert.Equal("C:\\Projects\\admin", rows[0].Cwd);
        Assert.Equal(t.AddMinutes(55), rows[0].UpdatedAt);
    }

    [Fact]
    public void LeftoverFilesOfDeadProcessesAreIgnored()
    {
        // a file whose process is gone is a leftover, not an open session
        string dir = TestUtil.NewTempDir();
        DateTime t = new DateTime(2026, 7, 25, 17, 0, 0);
        TestUtil.WriteSessionFile(dir, 100, TestUtil.Uuid(1), "C:\\Projects\\admin", "busy", t);
        TestUtil.WriteSessionFile(dir, 999, TestUtil.Uuid(2), "C:\\Projects\\dead", "idle", t);

        List<LiveSession> rows = SessionsReader.Read(dir, Alive(100));

        Assert.Single(rows);
        Assert.Equal(100, rows[0].Pid);
    }

    [Fact]
    public void PidComesFromTheFileNameWhenTheBodyHasNone()
    {
        string dir = TestUtil.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "4242.json"),
            "{\"sessionId\":\"" + TestUtil.Uuid(3) + "\",\"cwd\":\"C:\\\\Projects\\\\x\",\"status\":\"idle\"}");

        List<LiveSession> rows = SessionsReader.Read(dir, Alive(4242));

        Assert.Single(rows);
        Assert.Equal(4242, rows[0].Pid);
    }

    [Fact]
    public void MalformedFilesAndEmptyCwdAreSkipped()
    {
        string dir = TestUtil.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "1.json"), "{ not json");
        File.WriteAllText(Path.Combine(dir, "2.json"), "{\"sessionId\":\"x\",\"cwd\":\"\"}");
        File.WriteAllText(Path.Combine(dir, "3.json"), "[]");

        Assert.Empty(SessionsReader.Read(dir, Alive(1, 2, 3)));
    }

    [Fact]
    public void MissingDirectoryIsNotAnError()
    {
        Assert.Empty(SessionsReader.Read(Path.Combine(TestUtil.NewTempDir(), "nope")));
    }
}
