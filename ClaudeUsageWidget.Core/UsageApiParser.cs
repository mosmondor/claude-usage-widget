using System.Text.Json;

namespace ClaudeUsageWidget.Core;

/// <summary>Parses the JSON body of GET https://api.anthropic.com/api/oauth/usage into limit bars.</summary>
public static class UsageApiParser
{
    public static List<LimitRow> Parse(string json)
    {
        List<LimitRow> rows = new List<LimitRow>();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return rows;

        if (root.TryGetProperty("limits", out JsonElement lims) && lims.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement it in lims.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                string kind = GetStr(it, "kind");
                string label;
                switch (kind)
                {
                    case "session":       label = "Session  ·  5h"; break;
                    case "weekly_all":    label = "Weekly  ·  all models"; break;
                    case "weekly_scoped": label = "Weekly  ·  " + ScopeModel(it); break;
                    default:              label = kind; break;
                }
                rows.Add(new LimitRow
                {
                    Label = label,
                    Percent = GetPct(it, "percent"),
                    ResetsAt = GetDate(it, "resets_at"),
                    Severity = GetStr(it, "severity")
                });
            }
        }

        if (root.TryGetProperty("spend", out JsonElement sp) && sp.ValueKind == JsonValueKind.Object &&
            sp.TryGetProperty("enabled", out JsonElement en) && en.ValueKind == JsonValueKind.True)
        {
            rows.Add(new LimitRow { Label = "Extra credits", Percent = GetPct(sp, "percent"), Severity = GetStr(sp, "severity") });
        }

        return rows;
    }

    private static string ScopeModel(JsonElement it)
    {
        if (it.TryGetProperty("scope", out JsonElement sc) && sc.ValueKind == JsonValueKind.Object &&
            sc.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.Object &&
            m.TryGetProperty("display_name", out JsonElement dn) && dn.ValueKind == JsonValueKind.String)
            return dn.GetString() ?? "model";
        return "model";
    }

    private static string GetStr(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static int GetPct(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
            return (int)Math.Round(v.GetDouble());
        return 0;
    }

    private static DateTimeOffset? GetDate(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), out DateTimeOffset d))
            return d;
        return null;
    }
}
