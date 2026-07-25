namespace ClaudeUsageWidget.Core;

/// <summary>
/// A session started from the widget has to begin with a clean environment.
/// <para>
/// Claude Code marks its own child processes with variables like CLAUDE_CODE_CHILD_SESSION and
/// CLAUDE_CODE_SESSION_ID. If the widget itself was started from inside a Claude Code session it
/// inherits those, hands them to the terminal it spawns, and the new session decides it is a child:
/// transcript saving turns off. That is quiet and expensive — those transcripts are exactly what
/// this widget reads for names and token counts.
/// </para>
/// <para>
/// Variables the user has set persistently (registry: user or machine environment) are kept —
/// those are configuration, e.g. CLAUDE_CODE_MAX_OUTPUT_TOKENS, not inherited markers. Anything
/// else CLAUDE-prefixed in the current process only got there by inheritance, so it goes.
/// </para>
/// </summary>
public static class ChildEnv
{
    public const string Prefix = "CLAUDE";

    public static List<string> NamesToRemove(IEnumerable<string> current, IEnumerable<string> persisted)
    {
        HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (persisted != null)
        {
            foreach (string p in persisted)
                if (!string.IsNullOrEmpty(p)) keep.Add(p);
        }

        List<string> remove = new List<string>();
        if (current == null) return remove;
        foreach (string name in current)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (keep.Contains(name)) continue;
            remove.Add(name);
        }
        return remove;
    }
}
