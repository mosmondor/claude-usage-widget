using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageWidget.Core;

/// <summary>Parses a single Claude Code transcript (.jsonl) line into a <see cref="UsageRecord"/>.</summary>
public static class TranscriptParser
{
    /// <summary>Parse one line. Returns null unless it is a Claude assistant message carrying usage.
    /// Dates are resolved in <paramref name="tz"/> (defaults to the local zone).</summary>
    public static UsageRecord TryParse(string line, TimeZoneInfo tz = null)
    {
        if (line.Length < 20 || !line.Contains("output_tokens")) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("message", out JsonElement msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("usage", out JsonElement u) || u.ValueKind != JsonValueKind.Object) return null;

            string orig = msg.TryGetProperty("model", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String ? (mEl.GetString() ?? "") : "";
            if (!ModelNames.IsClaude(orig)) return null;

            long inp = Num(u, "input_tokens");
            long outp = Num(u, "output_tokens");
            long cr = Num(u, "cache_read_input_tokens");
            long cw = Num(u, "cache_creation_input_tokens");
            if (inp == 0 && outp == 0 && cr == 0 && cw == 0) return null;

            string id = msg.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String ? (idEl.GetString() ?? "") : "";
            string iso = root.TryGetProperty("timestamp", out JsonElement t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "") : "";

            return new UsageRecord
            {
                Id = id,
                DateLocal = DateInZone(iso, tz ?? TimeZoneInfo.Local),
                Model = ModelNames.Normalize(orig),
                In = inp,
                Out = outp,
                CacheRead = cr,
                CacheWrite = cw
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Convert an ISO-8601 timestamp to a yyyy-MM-dd date string in the given timezone.
    /// Returns "" for unparseable input. Deterministic and unit-testable.</summary>
    public static string DateInZone(string iso, TimeZoneInfo tz)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
            return "";
        return TimeZoneInfo.ConvertTime(dto, tz).ToString("yyyy-MM-dd");
    }

    private static long Num(JsonElement u, string name)
    {
        if (u.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number)
            return (long)el.GetDouble();
        return 0;
    }
}
