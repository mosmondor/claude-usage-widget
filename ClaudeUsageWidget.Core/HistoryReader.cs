using System.Text;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

/// <summary>
/// Reads ~/.claude/history.jsonl — Claude Code's prompt log. Each line carries the prompt text,
/// the absolute project path, the session id and a millisecond timestamp, which is everything
/// needed to list resumable conversations with a label a human recognises.
/// <para>
/// Picking that label is the whole point: the last prompt of a tidily closed session is "/exit",
/// and plenty of others are "[Pasted text #1 +75 lines]", "!git status" or "continue" — so the
/// topic is the first prompt with actual substance, and "where I stopped" is the last real one.
/// </para>
/// <para>
/// The project path comes from the record itself, not from the encoded folder name under
/// ~/.claude/projects (that encoding replaces separators with '-' and is therefore lossy).
/// </para>
/// </summary>
public static class HistoryReader
{
    public const int MaxLabel = 160;

    /// <summary>Only the last <paramref name="maxLines"/> prompts are considered, so work stays bounded.</summary>
    public static List<SessionEntry> Read(string historyPath, int maxLines = 4000)
    {
        List<SessionEntry> result = new List<SessionEntry>();
        if (!File.Exists(historyPath)) return result;

        Queue<string> tail = new Queue<string>();
        try
        {
            foreach (string line in File.ReadLines(historyPath))
            {
                tail.Enqueue(line);
                if (tail.Count > maxLines) tail.Dequeue();
            }
        }
        catch { return result; }

        Dictionary<string, Builder> byId = new Dictionary<string, Builder>(StringComparer.Ordinal);
        foreach (string line in tail)
        {
            Prompt p = ParseLine(line);
            if (p == null) continue;

            Builder b;
            if (!byId.TryGetValue(p.SessionId, out b))
            {
                b = new Builder();
                b.SessionId = p.SessionId;
                b.Project = p.Project;
                byId[p.SessionId] = b;
            }
            b.Feed(p);
        }

        foreach (Builder b in byId.Values) result.Add(b.ToEntry());
        result.Sort(new Comparison<SessionEntry>((a, x) => x.LastActivity.CompareTo(a.LastActivity)));
        return result;
    }

    /// <summary>Slash commands are not a description of anything — /exit, /compact, /model.</summary>
    public static bool IsSlashCommand(string display)
    {
        return display.Length > 0 && display[0] == '/';
    }

    /// <summary>
    /// Prompts that say nothing about the conversation: slash commands, "!" shell passthrough and
    /// the placeholders Claude Code logs instead of pasted or attached content.
    /// </summary>
    public static bool IsNoise(string display)
    {
        if (string.IsNullOrEmpty(display)) return true;
        if (IsSlashCommand(display)) return true;
        if (display[0] == '!') return true;
        if (display.StartsWith("[Pasted text", StringComparison.OrdinalIgnoreCase)) return true;
        if (display.StartsWith("[Image", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Good enough to be the "what was this about" label. The length floor is what rules out
    /// "continue", "ok", "da" — typed often, and useless for recognising a conversation later.
    /// </summary>
    public static bool IsDescriptive(string display)
    {
        return !IsNoise(display) && display.Length >= 12;
    }

    /// <summary>Single line, collapsed whitespace, capped — ready to be drawn.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        StringBuilder sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length >= MaxLabel) break;
        }
        return sb.ToString();
    }

    private sealed class Prompt
    {
        public string SessionId = "";
        public string Project = "";
        public string Display = "";
        public DateTime When;
    }

    private sealed class Builder
    {
        public string SessionId = "";
        public string Project = "";

        private int _count;
        private DateTime _firstAt = DateTime.MaxValue;
        private DateTime _lastAt = DateTime.MinValue;
        private string _firstAny = "";
        private string _lastAny = "";
        private DateTime _firstRealAt = DateTime.MaxValue;
        private DateTime _lastRealAt = DateTime.MinValue;
        private string _firstReal = "";
        private string _lastReal = "";
        private DateTime _firstDescAt = DateTime.MaxValue;
        private string _firstDesc = "";

        public void Feed(Prompt p)
        {
            _count++;
            if (p.When <= _firstAt) { _firstAt = p.When; _firstAny = p.Display; }
            if (p.When >= _lastAt) { _lastAt = p.When; _lastAny = p.Display; }
            if (p.Project.Length > 0) Project = p.Project;

            if (IsNoise(p.Display)) return;
            if (p.When <= _firstRealAt) { _firstRealAt = p.When; _firstReal = p.Display; }
            if (p.When >= _lastRealAt) { _lastRealAt = p.When; _lastReal = p.Display; }

            if (IsDescriptive(p.Display) && p.When <= _firstDescAt) { _firstDescAt = p.When; _firstDesc = p.Display; }
        }

        public SessionEntry ToEntry()
        {
            SessionEntry e = new SessionEntry();
            e.SessionId = SessionId;
            e.Project = Project;
            e.Prompts = _count;
            e.LastActivity = _lastAt;
            // topic needs substance; "where I stopped" is simply the last thing actually said
            e.FirstPrompt = Normalize(Pick(_firstDesc, _firstReal, _firstAny));
            e.LastPrompt = Normalize(Pick(_lastReal, _lastAny));
            return e;
        }

        private static string Pick(params string[] candidates)
        {
            foreach (string c in candidates)
                if (!string.IsNullOrEmpty(c)) return c;
            return "";
        }
    }

    private static Prompt ParseLine(string line)
    {
        if (line == null || line.Length < 20) return null;
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(line))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                JsonElement v;
                if (!root.TryGetProperty("sessionId", out v) || v.ValueKind != JsonValueKind.String) return null;
                Prompt p = new Prompt();
                p.SessionId = v.GetString();
                if (p.SessionId.Length == 0) return null;

                if (root.TryGetProperty("project", out v) && v.ValueKind == JsonValueKind.String) p.Project = v.GetString();
                if (root.TryGetProperty("display", out v) && v.ValueKind == JsonValueKind.String) p.Display = v.GetString().Trim();

                long ms;
                if (root.TryGetProperty("timestamp", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out ms) && ms > 0)
                    p.When = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

                if (p.Display.Length == 0) return null;
                return p;
            }
        }
        catch { return null; }
    }
}
