using System.Diagnostics;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

/// <summary>
/// Reads ~/.claude/sessions/&lt;pid&gt;.json — the files Claude Code keeps for its running
/// processes. A file whose process is gone is a leftover, not an open session, so the
/// process is verified before the session counts as live. Nothing is ever written here.
/// </summary>
public static class SessionsReader
{
    /// <summary>
    /// <paramref name="isAlive"/> is injectable for tests; the default checks that a process
    /// with that id exists and is actually a claude process (guards against pid reuse).
    /// </summary>
    public static List<LiveSession> Read(string sessionsDir, Func<int, bool> isAlive = null)
    {
        Func<int, bool> alive = isAlive ?? new Func<int, bool>(IsClaudeProcess);
        List<LiveSession> list = new List<LiveSession>();
        if (!Directory.Exists(sessionsDir)) return list;

        foreach (string path in Directory.EnumerateFiles(sessionsDir, "*.json"))
        {
            LiveSession s = ParseFile(path);
            if (s == null) continue;
            if (!alive(s.Pid)) continue;
            list.Add(s);
        }

        list.Sort(new Comparison<LiveSession>((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt)));
        return list;
    }

    private static LiveSession ParseFile(string path)
    {
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                LiveSession s = new LiveSession();
                s.Pid = Int(root, "pid");
                s.SessionId = Str(root, "sessionId");
                s.Cwd = Str(root, "cwd");
                s.Name = Str(root, "name");
                s.Status = Str(root, "status");
                s.StartedAt = Epoch(root, "startedAt");
                s.UpdatedAt = Epoch(root, "updatedAt");
                if (s.UpdatedAt == DateTime.MinValue) s.UpdatedAt = s.StartedAt;

                // the file name is the pid; trust it when the body has none
                if (s.Pid == 0)
                {
                    int parsed;
                    if (int.TryParse(Path.GetFileNameWithoutExtension(path), out parsed)) s.Pid = parsed;
                }
                if (s.Pid == 0 || s.Cwd.Length == 0) return null;
                return s;
            }
        }
        catch { return null; }
    }

    private static string Str(JsonElement e, string name)
    {
        JsonElement v;
        if (e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return "";
    }

    private static int Int(JsonElement e, string name)
    {
        JsonElement v;
        int i;
        if (e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out i)) return i;
        return 0;
    }

    private static DateTime Epoch(JsonElement e, string name)
    {
        JsonElement v;
        long ms;
        if (e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        return DateTime.MinValue;
    }

    private static bool IsClaudeProcess(int pid)
    {
        try
        {
            using (Process p = Process.GetProcessById(pid))
                return !p.HasExited && p.ProcessName.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
