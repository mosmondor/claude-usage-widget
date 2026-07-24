namespace ClaudeUsageWidget.Core;

public static class ModelNames
{
    /// <summary>True for Claude models (raw id starts with "claude"). Filters out gpt/glm/etc. that
    /// other agents may log into the same ~/.claude folder.</summary>
    public static bool IsClaude(string raw)
    {
        return (raw ?? "").StartsWith("claude", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalize a raw model id to a short display name:
    /// strips the "claude-" prefix and a trailing date suffix of 6+ digits.
    /// e.g. "claude-haiku-4-5-20251001" -> "haiku-4-5", "claude-fable-5" -> "fable-5".</summary>
    public static string Normalize(string raw)
    {
        string m = raw ?? "?";
        if (m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            m = m.Substring("claude-".Length);

        int i = m.LastIndexOf('-');
        if (i > 0 && i < m.Length - 1)
        {
            string tail = m.Substring(i + 1);
            if (tail.Length >= 6 && tail.All(char.IsDigit))
                m = m.Substring(0, i);
        }
        return m.Length == 0 ? "?" : m;
    }
}
