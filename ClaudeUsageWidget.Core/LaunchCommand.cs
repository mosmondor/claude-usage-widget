namespace ClaudeUsageWidget.Core;

/// <summary>
/// Builds the command line that resumes a conversation. Kept separate from the UI so it can be
/// unit-tested without starting a process.
/// <para>
/// With Windows Terminal the session opens as a new TAB in the existing window and the tab is
/// named after the project. Without it there is no tab concept, so the fallback is a plain shell
/// window (the caller sets its working directory).
/// </para>
/// </summary>
public static class LaunchCommand
{
    /// <summary>Session ids are uuids; refuse anything else rather than put it on a command line.</summary>
    public static bool IsValidSessionId(string id)
    {
        Guid g;
        return !string.IsNullOrEmpty(id) && Guid.TryParse(id, out g);
    }

    /// <param name="wtExe">Full path to wt.exe, or null/empty when Windows Terminal is absent.</param>
    /// <param name="shellExe">Shell that hosts claude, e.g. pwsh.exe.</param>
    public static (string exe, string args) Resume(string wtExe, string shellExe, string cwd, string sessionId, string title)
    {
        if (!IsValidSessionId(sessionId)) throw new ArgumentException("session id must be a uuid", nameof(sessionId));

        // claude -r <uuid> needs no quoting of its own: a uuid has no spaces, which keeps the
        // nested-quote mess out of the wt command line entirely.
        string inner = shellExe + " -NoExit -Command claude -r " + sessionId;

        if (string.IsNullOrEmpty(wtExe))
            return (shellExe, "-NoExit -Command claude -r " + sessionId);

        string args = "-w 0 nt -d " + Quote(cwd) + " --title " + Quote(SafeTitle(title)) + " " + inner;
        return (wtExe, args);
    }

    /// <summary>A fresh conversation in the same folder.</summary>
    public static (string exe, string args) NewSession(string wtExe, string shellExe, string cwd, string title)
    {
        string inner = shellExe + " -NoExit -Command claude";
        if (string.IsNullOrEmpty(wtExe))
            return (shellExe, "-NoExit -Command claude");
        return (wtExe, "-w 0 nt -d " + Quote(cwd) + " --title " + Quote(SafeTitle(title)) + " " + inner);
    }

    /// <summary>Windows Terminal treats ';' as a command separator, and a quote would end the argument.</summary>
    public static string SafeTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "claude";
        string s = title.Replace(';', ' ').Replace('"', ' ').Trim();
        if (s.Length == 0) return "claude";
        return s.Length > 40 ? s.Substring(0, 40) : s;
    }

    /// <summary>
    /// A trailing backslash would escape the closing quote, so it is dropped — except on a drive
    /// root, where "C:" means "current directory on C:" rather than "C:\". The path itself is passed
    /// verbatim: mangling it would reliably open the wrong folder.
    /// </summary>
    private static string Quote(string s)
    {
        string v = s == null ? "" : s;
        v = v.TrimEnd('\\');
        if (v.Length == 2 && v[1] == ':') v += "\\";
        return "\"" + v + "\"";
    }
}
