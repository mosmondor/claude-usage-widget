using ClaudeUsageWidget.Core;
using Xunit;

namespace ClaudeUsageWidget.Tests;

public class SessionListTests
{
    private static readonly Func<string, bool> AllExist = new Func<string, bool>(p => true);

    private static SessionEntry Hist(string id, string project, string first, string last, DateTime when)
    {
        SessionEntry e = new SessionEntry();
        e.SessionId = id;
        e.Project = project;
        e.FirstPrompt = first;
        e.LastPrompt = last;
        e.LastActivity = when;
        e.Prompts = 3;
        return e;
    }

    private static LiveSession Live(int pid, string id, string cwd, string status, DateTime updated)
    {
        LiveSession s = new LiveSession();
        s.Pid = pid;
        s.SessionId = id;
        s.Cwd = cwd;
        s.Status = status;
        s.UpdatedAt = updated;
        return s;
    }

    [Fact]
    public void ProjectsWithLiveSessionsComeFirst_ThenByRecency()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        List<SessionEntry> hist = new List<SessionEntry>
        {
            Hist(TestUtil.Uuid(1), "C:\\p\\zadnji", "a", "b", t),                  // newest, but closed
            Hist(TestUtil.Uuid(2), "C:\\p\\zivi", "c", "d", t.AddDays(-10))        // old, but running
        };
        List<LiveSession> live = new List<LiveSession> { Live(100, TestUtil.Uuid(2), "C:\\p\\zivi", "busy", t.AddDays(-10)) };

        List<ProjectGroup> groups = SessionList.Build(hist, live, 8, 4, AllExist);

        Assert.Equal(2, groups.Count);
        Assert.Equal("zivi", groups[0].ProjectName);
        Assert.True(groups[0].HasLive);
        Assert.Equal("zadnji", groups[1].ProjectName);
        Assert.False(groups[1].HasLive);
    }

    [Fact]
    public void LiveSessionMissingFromHistoryIsStillListed()
    {
        // running but nothing typed yet -> must not disappear
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        List<LiveSession> live = new List<LiveSession> { Live(77, TestUtil.Uuid(5), "C:\\p\\fresh", "idle", t) };

        List<ProjectGroup> groups = SessionList.Build(new List<SessionEntry>(), live, 8, 4, AllExist);

        Assert.Single(groups);
        Assert.Single(groups[0].Sessions);
        Assert.True(groups[0].Sessions[0].IsLive);
        Assert.Equal(77, groups[0].Sessions[0].Pid);
        Assert.Equal("(new session)", groups[0].Sessions[0].FirstPrompt);
    }

    [Fact]
    public void LiveFlagAndStatusAreOverlaidOnTheHistoryRow()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        string id = TestUtil.Uuid(6);
        List<SessionEntry> hist = new List<SessionEntry> { Hist(id, "C:\\p\\admin", "launcher", "commitaj", t) };
        List<LiveSession> live = new List<LiveSession> { Live(4242, id, "C:\\p\\admin", "busy", t.AddMinutes(30)) };

        List<ProjectGroup> groups = SessionList.Build(hist, live, 8, 4, AllExist);

        SessionEntry row = groups[0].Sessions[0];
        Assert.True(row.IsLive);
        Assert.Equal(4242, row.Pid);
        Assert.Equal("busy", row.Status);
        Assert.Equal("launcher", row.FirstPrompt);              // label survives
        Assert.Equal(t.AddMinutes(30), row.LastActivity);       // live file is fresher
        Assert.Single(groups);                                  // not duplicated
    }

    [Fact]
    public void SessionsAreGroupedByProjectAndCapped()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        List<SessionEntry> hist = new List<SessionEntry>();
        for (int i = 0; i < 6; i++)
            hist.Add(Hist(TestUtil.Uuid(100 + i), "C:\\p\\fireplay", "s" + i, "s" + i, t.AddMinutes(-i)));
        for (int i = 0; i < 3; i++)
            hist.Add(Hist(TestUtil.Uuid(200 + i), "C:\\p\\other" + i, "o" + i, "o" + i, t.AddDays(-1 - i)));

        List<ProjectGroup> groups = SessionList.Build(hist, new List<LiveSession>(), 2, 4, AllExist);

        Assert.Equal(2, groups.Count);                       // maxProjects
        Assert.Equal("fireplay", groups[0].ProjectName);
        Assert.Equal(4, groups[0].Sessions.Count);           // maxPerProject
        Assert.Equal("s0", groups[0].Sessions[0].FirstPrompt); // newest session of the project first
    }

    [Fact]
    public void LiveSessionsSortAboveClosedOnesWithinAProject()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        string liveId = TestUtil.Uuid(7);
        List<SessionEntry> hist = new List<SessionEntry>
        {
            Hist(TestUtil.Uuid(8), "C:\\p\\admin", "novija zatvorena", "x", t),
            Hist(liveId, "C:\\p\\admin", "starija ziva", "y", t.AddDays(-3))
        };
        List<LiveSession> live = new List<LiveSession> { Live(9, liveId, "C:\\p\\admin", "idle", t.AddDays(-3)) };

        List<ProjectGroup> groups = SessionList.Build(hist, live, 8, 4, AllExist);

        Assert.Equal("starija ziva", groups[0].Sessions[0].FirstPrompt);
        Assert.Equal("novija zatvorena", groups[0].Sessions[1].FirstPrompt);
    }

    [Fact]
    public void ProjectWhoseFolderIsGoneIsMarked()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        List<SessionEntry> hist = new List<SessionEntry> { Hist(TestUtil.Uuid(9), "C:\\p\\obrisan", "a", "b", t) };

        List<ProjectGroup> groups = SessionList.Build(hist, new List<LiveSession>(), 8, 4, new Func<string, bool>(p => false));

        Assert.False(groups[0].Exists);
    }

    [Fact]
    public void SameProjectDifferentCasingIsOneGroup()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        List<SessionEntry> hist = new List<SessionEntry>
        {
            Hist(TestUtil.Uuid(10), "C:\\Projects\\Admin", "a", "b", t),
            Hist(TestUtil.Uuid(11), "c:\\projects\\admin", "c", "d", t.AddMinutes(-5))
        };

        List<ProjectGroup> groups = SessionList.Build(hist, new List<LiveSession>(), 8, 4, AllExist);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Sessions.Count);
    }

    [Fact]
    public void ARunningSessionContributesItsOwnName()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        string id = TestUtil.Uuid(30);
        List<SessionEntry> hist = new List<SessionEntry> { Hist(id, "C:\\p\\fireplay", "generiraj playliste", "x", t) };
        LiveSession ls = Live(70500, id, "C:\\p\\fireplay", "idle", t);
        ls.Name = "playlist-generation";

        List<ProjectGroup> groups = SessionList.Build(hist, new List<LiveSession> { ls }, 8, 4, AllExist);

        Assert.Equal("playlist-generation", groups[0].Sessions[0].Name);
        Assert.Equal("playlist-generation", groups[0].Sessions[0].Label);   // name beats the prompt
    }

    [Fact]
    public void LivenessIsClearedBeforeItIsOverlaidAgain()
    {
        // history entries are reused across refreshes: a session that exited must stop being "live"
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        string id = TestUtil.Uuid(31);
        List<SessionEntry> hist = new List<SessionEntry> { Hist(id, "C:\\p\\admin", "a", "b", t) };

        SessionList.Build(hist, new List<LiveSession> { Live(4242, id, "C:\\p\\admin", "busy", t) }, 8, 4, AllExist);
        Assert.True(hist[0].IsLive);

        List<ProjectGroup> after = SessionList.Build(hist, new List<LiveSession>(), 8, 4, AllExist);
        Assert.False(after[0].Sessions[0].IsLive);
        Assert.Equal(0, after[0].Sessions[0].Pid);
        Assert.Equal("", after[0].Sessions[0].Status);
        Assert.False(after[0].HasLive);
    }

    [Fact]
    public void NamedOnlyDropsUnnamedSessionsButNeverRunningOnes()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        string liveId = TestUtil.Uuid(40);
        SessionEntry named = Hist(TestUtil.Uuid(41), "C:\\p\\admin", "a", "b", t.AddMinutes(-1));
        named.Name = "cyprus-admin";
        List<SessionEntry> hist = new List<SessionEntry>
        {
            named,
            Hist(TestUtil.Uuid(42), "C:\\p\\admin", "neimenovana sesija", "x", t.AddMinutes(-2)),
            Hist(liveId, "C:\\p\\admin", "takoder neimenovana", "y", t)
        };
        List<LiveSession> live = new List<LiveSession> { Live(500, liveId, "C:\\p\\admin", "busy", t) };

        List<ProjectGroup> groups = SessionList.Build(hist, live, 8, 6, AllExist, true);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Sessions.Count);
        Assert.True(groups[0].Sessions[0].IsLive);                  // unnamed but running -> kept
        Assert.Equal("cyprus-admin", groups[0].Sessions[1].Name);
    }

    [Fact]
    public void NamedOnlyCanEmptyTheListEntirely()
    {
        DateTime t = new DateTime(2026, 7, 25, 12, 0, 0);
        List<SessionEntry> hist = new List<SessionEntry> { Hist(TestUtil.Uuid(45), "C:\\p\\x", "bez imena", "y", t) };

        Assert.Empty(SessionList.Build(hist, new List<LiveSession>(), 8, 6, AllExist, true));
        Assert.Single(SessionList.Build(hist, new List<LiveSession>(), 8, 6, AllExist, false));
    }

    [Fact]
    public void EmptyInputsAreHandled()
    {
        Assert.Empty(SessionList.Build(null, null, 8, 4, AllExist));
    }
}
