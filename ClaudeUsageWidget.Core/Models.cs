namespace ClaudeUsageWidget.Core;

/// <summary>Mutable token aggregate.</summary>
public sealed class Agg
{
    public long In { get; set; }
    public long Out { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long Total => In + Out + CacheRead + CacheWrite;

    public void Add(Agg o) { In += o.In; Out += o.Out; CacheRead += o.CacheRead; CacheWrite += o.CacheWrite; }
    public void Add(UsageRecord r) { In += r.In; Out += r.Out; CacheRead += r.CacheRead; CacheWrite += r.CacheWrite; }
}

/// <summary>One assistant usage record parsed from a Claude Code transcript line.</summary>
public sealed class UsageRecord
{
    public string Id { get; set; } = "";        // message id (dedup key); "" when absent
    public string DateLocal { get; set; } = ""; // yyyy-MM-dd in the chosen timezone; "" when no timestamp
    public string Model { get; set; } = "";      // normalized short model name (e.g. "fable-5")
    public long In { get; set; }
    public long Out { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long Total => In + Out + CacheRead + CacheWrite;
}

/// <summary>A plan-usage bar from the /api/oauth/usage endpoint.</summary>
public sealed class LimitRow
{
    public string Label { get; set; } = "";
    public int Percent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public string Severity { get; set; } = "normal";
}
