namespace ClaudeUsageWidget.Core;

/// <summary>
/// Merges the prompt history with the live session files and groups the result by project:
/// live sessions first, then the most recently used ones. This is the list the Sessions tab draws.
/// </summary>
public static class SessionList
{
    /// <param name="namedOnly">
    /// Keep only sessions that were named with /rename or -n. Running sessions are always kept
    /// regardless — an unnamed session you have open still has to be visible.
    /// </param>
    public static List<ProjectGroup> Build(
        List<SessionEntry> history,
        List<LiveSession> live,
        int maxProjects = 8,
        int maxPerProject = 4,
        Func<string, bool> dirExists = null,
        bool namedOnly = false)
    {
        Func<string, bool> exists = dirExists ?? new Func<string, bool>(Directory.Exists);

        Dictionary<string, SessionEntry> byId = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
        if (history != null)
        {
            foreach (SessionEntry e in history)
            {
                if (e.SessionId.Length == 0 || e.Project.Length == 0) continue;
                // entries are reused across refreshes, so liveness must be cleared before it is
                // overlaid again -- otherwise a session that has since exited stays "live" forever
                e.IsLive = false;
                e.Pid = 0;
                e.Status = "";
                byId[e.SessionId] = e;
            }
        }

        if (live != null)
        {
            foreach (LiveSession ls in live)
            {
                SessionEntry e;
                if (!byId.TryGetValue(ls.SessionId, out e))
                {
                    // running but nothing typed yet (or older than the history window)
                    e = new SessionEntry();
                    e.SessionId = ls.SessionId;
                    e.Project = ls.Cwd;
                    e.FirstPrompt = "(new session)";
                    e.LastActivity = ls.UpdatedAt;
                    byId[ls.SessionId] = e;
                }
                // a running session carries its own name, so it never needs a transcript scan
                if (ls.Name.Length > 0) e.Name = ls.Name;
                e.IsLive = true;
                e.Pid = ls.Pid;
                e.Status = ls.Status;
                if (ls.UpdatedAt > e.LastActivity) e.LastActivity = ls.UpdatedAt;
                if (e.Project.Length == 0) e.Project = ls.Cwd;
            }
        }

        Dictionary<string, ProjectGroup> groups = new Dictionary<string, ProjectGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (SessionEntry e in byId.Values)
        {
            if (namedOnly && !e.HasName && !e.IsLive) continue;

            ProjectGroup g;
            if (!groups.TryGetValue(e.Project, out g))
            {
                g = new ProjectGroup();
                g.Project = e.Project;
                groups[e.Project] = g;
            }
            g.Sessions.Add(e);
            if (e.IsLive) g.HasLive = true;
            if (e.LastActivity > g.LastActivity) g.LastActivity = e.LastActivity;
        }

        List<ProjectGroup> list = new List<ProjectGroup>(groups.Values);
        foreach (ProjectGroup g in list)
        {
            g.Sessions.Sort(new Comparison<SessionEntry>(CompareSessions));
            if (g.Sessions.Count > maxPerProject) g.Sessions.RemoveRange(maxPerProject, g.Sessions.Count - maxPerProject);
            g.Exists = exists(g.Project);
        }

        list.Sort(new Comparison<ProjectGroup>(CompareGroups));
        if (list.Count > maxProjects) list.RemoveRange(maxProjects, list.Count - maxProjects);
        return list;
    }

    private static int CompareSessions(SessionEntry a, SessionEntry b)
    {
        if (a.IsLive != b.IsLive) return a.IsLive ? -1 : 1;
        int c = b.LastActivity.CompareTo(a.LastActivity);
        if (c != 0) return c;
        return string.CompareOrdinal(a.SessionId, b.SessionId);
    }

    private static int CompareGroups(ProjectGroup a, ProjectGroup b)
    {
        if (a.HasLive != b.HasLive) return a.HasLive ? -1 : 1;
        int c = b.LastActivity.CompareTo(a.LastActivity);
        if (c != 0) return c;
        return string.Compare(a.Project, b.Project, StringComparison.OrdinalIgnoreCase);
    }
}
