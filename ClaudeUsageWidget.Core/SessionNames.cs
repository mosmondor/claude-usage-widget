using System.Text.Json;

namespace ClaudeUsageWidget.Core;

/// <summary>
/// Resolves the display name a session was given with <c>/rename</c> or <c>claude -n &lt;name&gt;</c>.
/// The name is not stored in an index anywhere, only inside the session's own transcript
/// (~/.claude/projects/&lt;folder&gt;/&lt;sessionId&gt;.jsonl), so it has to be scanned out of it.
/// <para>
/// Transcripts are large (hundreds of MB in total), so this is bounded twice over: only the
/// sessions actually being displayed are looked up, and a result is cached against the file's
/// mtime+size. A closed session's transcript never changes again, so it is scanned exactly once.
/// Live sessions never reach this class at all — their name comes free from the session file.
/// </para>
/// </summary>
public sealed class SessionNames
{
    private const string RenameMarker = "Session renamed to: ";
    private const string NamedMarker = "named this session ";

    public sealed class Entry
    {
        public long Mtime { get; set; }
        public long Size { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class CacheFile
    {
        public Dictionary<string, Entry> Sessions { get; set; } = new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private readonly string _projectsDir;
    private readonly string _cachePath;
    private CacheFile _cache;

    public SessionNames(string projectsDir, string cachePath)
    {
        _projectsDir = projectsDir;
        _cachePath = cachePath;
        _cache = LoadCache();
    }

    /// <summary>Session id -> name. Ids with no name are simply absent from the result.</summary>
    public Dictionary<string, string> Resolve(IEnumerable<string> sessionIds)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sessionIds == null) return result;

        Dictionary<string, string> index = null;
        bool dirty = false;

        foreach (string id in sessionIds)
        {
            if (string.IsNullOrEmpty(id) || result.ContainsKey(id)) continue;

            if (index == null) index = BuildIndex();
            string path;
            if (!index.TryGetValue(id, out path)) continue;

            long mtime = 0;
            long size = 0;
            try
            {
                FileInfo fi = new FileInfo(path);
                mtime = fi.LastWriteTimeUtc.Ticks;
                size = fi.Length;
            }
            catch { continue; }

            Entry cached;
            if (_cache.Sessions.TryGetValue(id, out cached) && cached.Mtime == mtime && cached.Size == size)
            {
                if (cached.Name.Length > 0) result[id] = cached.Name;
                continue;
            }

            string name = ScanFile(path);
            _cache.Sessions[id] = new Entry { Mtime = mtime, Size = size, Name = name };
            dirty = true;
            if (name.Length > 0) result[id] = name;
        }

        if (dirty) SaveCache();
        return result;
    }

    /// <summary>Session id -> transcript path. File names under a project folder are the session ids.</summary>
    private Dictionary<string, string> BuildIndex()
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_projectsDir)) return map;
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(_projectsDir))
            {
                // top level only: the nested subagents/ and tool-results/ folders hold other things
                foreach (string path in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                    map[Path.GetFileNameWithoutExtension(path)] = path;
            }
        }
        catch { }
        return map;
    }

    /// <summary>Last name wins — a session can be renamed more than once.</summary>
    public static string ScanFile(string path)
    {
        string found = "";
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                string name = ExtractFromLine(line);
                if (name.Length > 0) found = name;
            }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// Both forms Claude Code writes: the /rename command's own output, and the reminder it injects
    /// when the session was named on the command line.
    /// </summary>
    public static string ExtractFromLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";

        int i = line.IndexOf(RenameMarker, StringComparison.Ordinal);
        if (i >= 0) return Clean(line.Substring(i + RenameMarker.Length));

        i = line.IndexOf(NamedMarker, StringComparison.Ordinal);
        if (i >= 0)
        {
            string rest = line.Substring(i + NamedMarker.Length);
            // the transcript is json, so the quotes around the name arrive escaped
            if (rest.StartsWith("\\\"", StringComparison.Ordinal)) rest = rest.Substring(2);
            else if (rest.StartsWith("\"", StringComparison.Ordinal)) rest = rest.Substring(1);
            return Clean(rest);
        }
        return "";
    }

    private static string Clean(string s)
    {
        int end = s.Length;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<' || c == '"' || c == '\\' || c == '\r' || c == '\n') { end = i; break; }
        }
        string name = s.Substring(0, end).Trim();
        if (name.EndsWith(".", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 1).Trim();
        return name.Length > 60 ? name.Substring(0, 60) : name;
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

    private void SaveCache()
    {
        try
        {
            string dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache));
        }
        catch { }
    }
}
