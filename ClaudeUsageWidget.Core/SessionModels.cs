namespace ClaudeUsageWidget.Core;

/// <summary>
/// A live Claude Code process, as recorded by Claude Code itself in
/// ~/.claude/sessions/&lt;pid&gt;.json. The file name is the process id.
/// </summary>
public sealed class LiveSession
{
    public int Pid { get; set; }
    public string SessionId { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One conversation. Reconstructed from ~/.claude/history.jsonl (prompt log) and, when the
/// process is still running, enriched from the live session file.
/// <para>
/// <see cref="FirstPrompt"/> answers "what was this about", <see cref="LastPrompt"/> answers
/// "where did I stop". Both deliberately ignore slash commands: a session closed with /exit
/// would otherwise be labelled "/exit", which is exactly the session you cannot recognise.
/// </para>
/// </summary>
public sealed class SessionEntry
{
    public string SessionId { get; set; } = "";
    public string Project { get; set; } = "";

    /// <summary>The name given with /rename or -n. Empty when the session was never named.</summary>
    public string Name { get; set; } = "";

    public string FirstPrompt { get; set; } = "";
    public string LastPrompt { get; set; } = "";
    public DateTime LastActivity { get; set; }
    public int Prompts { get; set; }
    public bool IsLive { get; set; }
    public int Pid { get; set; }
    public string Status { get; set; } = "";

    public string ProjectName
    {
        get { return Paths.LeafName(Project); }
    }

    /// <summary>What the row shows: the name you gave the session, or its topic if you never named it.</summary>
    public string Label
    {
        get
        {
            if (Name.Length > 0) return Name;
            if (FirstPrompt.Length > 0) return FirstPrompt;
            return "(unnamed)";
        }
    }

    public bool HasName
    {
        get { return Name.Length > 0; }
    }
}

/// <summary>Sessions of one project, newest first, live ones on top.</summary>
public sealed class ProjectGroup
{
    public string Project { get; set; } = "";
    public DateTime LastActivity { get; set; }
    public bool HasLive { get; set; }
    public bool Exists { get; set; } = true;
    public List<SessionEntry> Sessions { get; set; } = new List<SessionEntry>();

    public string ProjectName
    {
        get { return Paths.LeafName(Project); }
    }
}

public static class Paths
{
    /// <summary>Last path segment, e.g. "C:\Projects\fireplay\FirePlay2020" -> "FirePlay2020".</summary>
    public static string LeafName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return path;
        int i = trimmed.LastIndexOfAny(new char[] { '\\', '/' });
        string leaf = i >= 0 ? trimmed.Substring(i + 1) : trimmed;
        return leaf.Length > 0 ? leaf : trimmed;
    }
}
