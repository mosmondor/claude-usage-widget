namespace ClaudeUsageWidget.Core;

public static class Dedup
{
    /// <summary>
    /// Collapse duplicate transcript records that share the same message id.
    ///
    /// CANONICAL RULE (deterministic, independent of input / filesystem order):
    /// among all records with the same non-empty <see cref="UsageRecord.Id"/>, keep the one with
    /// the greatest <see cref="UsageRecord.Total"/>; ties are broken by Out desc, then In desc,
    /// then CacheRead desc, then CacheWrite desc. If two records are identical on every field the
    /// choice is irrelevant (same result). Records with an empty id cannot be keyed, so each is
    /// kept as-is.
    ///
    /// Rationale: Claude Code writes the same assistant message into several files (sidechains,
    /// compacted copies) and retries/streamed partials report monotonically growing usage, so the
    /// max-Total record is the complete / final one.
    /// </summary>
    public static List<UsageRecord> Canonical(IEnumerable<UsageRecord> records)
    {
        Dictionary<string, UsageRecord> byId = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        List<UsageRecord> noId = new List<UsageRecord>();
        foreach (UsageRecord r in records)
        {
            if (string.IsNullOrEmpty(r.Id))
            {
                noId.Add(r);
                continue;
            }
            if (!byId.TryGetValue(r.Id, out UsageRecord cur) || Compare(r, cur) > 0)
                byId[r.Id] = r;
        }

        List<UsageRecord> result = new List<UsageRecord>(byId.Count + noId.Count);
        result.AddRange(byId.Values);
        result.AddRange(noId);
        return result;
    }

    /// <summary>&gt;0 when <paramref name="a"/> should win over <paramref name="b"/>.</summary>
    public static int Compare(UsageRecord a, UsageRecord b)
    {
        int c = a.Total.CompareTo(b.Total);
        if (c != 0) return c;
        c = a.Out.CompareTo(b.Out);
        if (c != 0) return c;
        c = a.In.CompareTo(b.In);
        if (c != 0) return c;
        c = a.CacheRead.CompareTo(b.CacheRead);
        if (c != 0) return c;
        return a.CacheWrite.CompareTo(b.CacheWrite);
    }
}
