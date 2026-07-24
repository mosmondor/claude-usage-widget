using System.Text.Json;

namespace ClaudeUsageWidget.Core;

/// <summary>
/// Scans Claude Code transcripts and exposes CURRENT-MONTH aggregates (today + month-to-date).
/// Bounded work: only files modified in the current month are read, and only current-month records
/// are aggregated / cached, so memory and cache stay small regardless of total history. Handles
/// month rollover (cache is stamped with its month and rebuilt when the month changes). The cache
/// is only rewritten when something actually changed.
/// </summary>
public sealed class UsageStore
{
    public sealed class FileEntry
    {
        public long Mtime { get; set; }
        public long Size { get; set; }
        public List<UsageRecord> Records { get; set; } = new List<UsageRecord>();
    }

    public sealed class CacheFile
    {
        public string Month { get; set; } = "";
        public Dictionary<string, FileEntry> Files { get; set; } = new Dictionary<string, FileEntry>();
    }

    private readonly string _projectsDir;
    private readonly string _cachePath;
    private readonly Func<DateTime> _now;
    private readonly TimeZoneInfo _tz;

    /// <summary>Current-month aggregates: key "yyyy-MM-dd|model" -> Agg.</summary>
    public Dictionary<string, Agg> Data { get; private set; } = new Dictionary<string, Agg>(StringComparer.Ordinal);
    public string Month { get; private set; } = "";

    public UsageStore(string projectsDir, string cachePath, Func<DateTime> now = null, TimeZoneInfo tz = null)
    {
        _projectsDir = projectsDir;
        _cachePath = cachePath;
        _now = now ?? new Func<DateTime>(() => DateTime.Now);
        _tz = tz ?? TimeZoneInfo.Local;
    }

    public void Scan()
    {
        DateTime today = _now().Date;
        string monthPrefix = today.ToString("yyyy-MM");
        DateTime monthStart = new DateTime(today.Year, today.Month, 1);

        CacheFile cache = LoadCache();
        bool dirty = cache.Month != monthPrefix;      // month rollover -> rebuild
        if (dirty) cache = new CacheFile { Month = monthPrefix };

        Dictionary<string, FileEntry> next = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        if (Directory.Exists(_projectsDir))
        {
            foreach (string path in Directory.EnumerateFiles(_projectsDir, "*.jsonl", SearchOption.AllDirectories))
            {
                FileInfo fi;
                try { fi = new FileInfo(path); }
                catch { continue; }

                if (fi.LastWriteTime < monthStart) continue;   // cannot contain current-month records

                long mtime = fi.LastWriteTimeUtc.Ticks;
                long size = fi.Length;
                if (cache.Files.TryGetValue(path, out FileEntry cached) && cached.Mtime == mtime && cached.Size == size)
                {
                    next[path] = cached;
                }
                else
                {
                    next[path] = ParseFile(path, mtime, size, monthPrefix);
                    dirty = true;
                }
            }
        }
        if (next.Count != cache.Files.Count) dirty = true;     // some files aged out / removed

        Dictionary<string, Agg> data = new Dictionary<string, Agg>(StringComparer.Ordinal);
        List<UsageRecord> all = new List<UsageRecord>();
        foreach (FileEntry e in next.Values)
            all.AddRange(e.Records);

        foreach (UsageRecord r in Dedup.Canonical(all))
        {
            if (r.DateLocal.Length < 7 || !r.DateLocal.StartsWith(monthPrefix, StringComparison.Ordinal)) continue;
            string key = r.DateLocal + "|" + r.Model;
            if (!data.TryGetValue(key, out Agg a))
            {
                a = new Agg();
                data[key] = a;
            }
            a.Add(r);
        }

        Data = data;
        Month = monthPrefix;

        if (dirty) SaveCache(new CacheFile { Month = monthPrefix, Files = next });
    }

    private FileEntry ParseFile(string path, long mtime, long size, string monthPrefix)
    {
        List<UsageRecord> recs = new List<UsageRecord>();
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                UsageRecord r = TranscriptParser.TryParse(line, _tz);
                if (r == null) continue;
                if (!r.DateLocal.StartsWith(monthPrefix, StringComparison.Ordinal)) continue; // month scope
                recs.Add(r);
            }
        }
        catch { /* unreadable file -> empty */ }

        // per-file canonical de-dup keeps the cache compact; global de-dup still runs at aggregation
        return new FileEntry { Mtime = mtime, Size = size, Records = Dedup.Canonical(recs) };
    }

    public (Agg agg, double cost) Today()
    {
        string today = _now().Date.ToString("yyyy-MM-dd");
        Agg total = new Agg();
        double cost = 0;
        foreach (KeyValuePair<string, Agg> kv in Data)
        {
            string[] parts = kv.Key.Split('|', 2);
            if (parts[0] != today) continue;
            total.Add(kv.Value);
            cost += Pricing.Cost(parts.Length > 1 ? parts[1] : "", kv.Value);
        }
        return (total, cost);
    }

    public (List<(string model, Agg agg, double cost)> rows, double total) MonthByModel()
    {
        Dictionary<string, Agg> by = new Dictionary<string, Agg>(StringComparer.Ordinal);
        Dictionary<string, double> cost = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Agg> kv in Data)
        {
            string[] parts = kv.Key.Split('|', 2);
            string model = parts.Length > 1 ? parts[1] : "?";
            if (!by.TryGetValue(model, out Agg a))
            {
                a = new Agg();
                by[model] = a;
            }
            a.Add(kv.Value);
            cost[model] = (cost.TryGetValue(model, out double prev) ? prev : 0) + Pricing.Cost(model, kv.Value);
        }

        List<(string model, Agg agg, double cost)> rows = by
            .Select(kv => (kv.Key, kv.Value, cost[kv.Key]))
            .OrderByDescending(x => x.Item3)
            .ThenBy(x => x.Item1, StringComparer.Ordinal)
            .ToList();

        double totalCost = 0;
        foreach (double c in cost.Values) totalCost += c;
        return (rows, totalCost);
    }

    private CacheFile LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
                return JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath)) ?? new CacheFile();
        }
        catch { }
        return new CacheFile();
    }

    private void SaveCache(CacheFile c)
    {
        try
        {
            string dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(c));
        }
        catch { }
    }
}
