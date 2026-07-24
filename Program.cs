using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace ClaudeUsageWidget;

// ---- local token-usage data (JSONL, for month/today) ----
sealed class Agg { public long In, Out, Cr, Cw; public void Add(Agg o){ In+=o.In; Out+=o.Out; Cr+=o.Cr; Cw+=o.Cw; } }
sealed class Row { public string Id {get;set;} = ""; public string Date {get;set;} = ""; public string Model {get;set;} = ""; public long In {get;set;} public long Out {get;set;} public long Cr {get;set;} public long Cw {get;set;} }
sealed class FileEntry { public long Mtime {get;set;} public long Size {get;set;} public List<Row> Rows {get;set;} = new(); }

// ---- plan-limit row (from /api/oauth/usage) ----
sealed class LimitRow { public string Label = ""; public int Percent; public DateTimeOffset? ResetsAt; public string Sev = "normal"; }

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new WidgetForm());
    }
}

// ---- plan usage API (session % / weekly % / per-model) ----
static class UsageApi
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static string CredPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static string Error = "";

    public static List<LimitRow> Fetch()
    {
        Error = "";
        var rows = new List<LimitRow>();
        string? tok = ReadToken();
        if (tok == null) { Error = "no token (.credentials.json)"; return rows; }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tok);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("User-Agent", "claude-usage-widget/1.0");
            using var resp = Http.Send(req);
            if (!resp.IsSuccessStatusCode)
            {
                Error = (int)resp.StatusCode switch
                {
                    401 => "token expired? run Claude Code",
                    429 => "rate limited (429) - backing off",
                    var c => "HTTP " + c
                };
                return rows;
            }
            using var s = resp.Content.ReadAsStream();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;

            if (root.TryGetProperty("limits", out var lims) && lims.ValueKind == JsonValueKind.Array)
                foreach (var it in lims.EnumerateArray())
                {
                    string kind = Str(it, "kind");
                    int pct = it.TryGetProperty("percent", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : 0;
                    DateTimeOffset? reset = null;
                    if (it.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(ra.GetString(), out var d)) reset = d;
                    string label = kind switch
                    {
                        "session"       => "Session  ·  5h",
                        "weekly_all"    => "Weekly  ·  all models",
                        "weekly_scoped" => "Weekly  ·  " + ModelName(it),
                        _               => kind
                    };
                    rows.Add(new LimitRow { Label = label, Percent = pct, ResetsAt = reset, Sev = Str(it, "severity") });
                }

            if (root.TryGetProperty("spend", out var sp) && sp.ValueKind == JsonValueKind.Object &&
                sp.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True)
            {
                int pct = sp.TryGetProperty("percent", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt32() : 0;
                rows.Add(new LimitRow { Label = "Extra credits", Percent = pct, Sev = Str(sp, "severity") });
            }
            return rows;
        }
        catch (Exception e) { Error = e.Message.Length > 46 ? e.Message[..46] : e.Message; return rows; }
    }

    static string ModelName(JsonElement it)
        => it.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object &&
           sc.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object &&
           m.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String
           ? (dn.GetString() ?? "model") : "model";

    static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    static string? ReadToken()
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllText(CredPath)); return Find(doc.RootElement); }
        catch { return null; }
    }
    static string? Find(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if ((p.NameEquals("accessToken") || p.NameEquals("access_token")) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
                var r = Find(p.Value); if (r != null) return r;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var it in e.EnumerateArray()) { var r = Find(it); if (r != null) return r; }
        return null;
    }
}

// ---- price table (per 1M tokens; ROUGH API-equivalent estimate) ----
// NB: Fable's public price is unknown -> priced as Opus tier (runs high). $ is notional.
static class Pricing
{
    public static (double i, double o, double cw, double cr) For(string model)
    {
        var m = (model ?? "").ToLowerInvariant();
        if (m.Contains("haiku"))  return (0.80, 4.0, 1.00, 0.08);
        if (m.Contains("sonnet")) return (3.0, 15.0, 3.75, 0.30);
        return (15.0, 75.0, 18.75, 1.50); // opus / fable / default
    }
    public static double Cost(string model, Agg a)
    { var p = For(model); return a.In/1e6*p.i + a.Out/1e6*p.o + a.Cw/1e6*p.cw + a.Cr/1e6*p.cr; }
}

// ---- scanner: local ~/.claude/projects JSONL; GLOBAL dedup by message id; Claude models only ----
static class Scanner
{
    static readonly string Base =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
    static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget");
    static readonly string CachePath = Path.Combine(CacheDir, "cache-v2.json");

    public static Dictionary<string, Agg> Data = new();

    public static void Scan()
    {
        var cache = LoadCache();
        var next = new Dictionary<string, FileEntry>();
        var result = new Dictionary<string, Agg>();
        var seen = new HashSet<string>();  // GLOBAL dedup by message id

        if (Directory.Exists(Base))
            foreach (var path in Directory.EnumerateFiles(Base, "*.jsonl", SearchOption.AllDirectories))
            {
                FileEntry entry;
                try
                {
                    var fi = new FileInfo(path);
                    long mtime = fi.LastWriteTimeUtc.Ticks, size = fi.Length;
                    entry = cache.TryGetValue(path, out var c) && c.Mtime == mtime && c.Size == size ? c : ParseFile(path, mtime, size);
                }
                catch { continue; }
                next[path] = entry;
                foreach (var r in entry.Rows)
                {
                    if (r.Id.Length > 0 && !seen.Add(r.Id)) continue;  // duplicate across files -> skip
                    var key = r.Date + "|" + r.Model;
                    if (!result.TryGetValue(key, out var a)) result[key] = a = new Agg();
                    a.In += r.In; a.Out += r.Out; a.Cr += r.Cr; a.Cw += r.Cw;
                }
            }
        Data = result;
        SaveCache(next);
    }

    // per-message rows (with id) so cross-file dedup works at merge; Claude-only
    static FileEntry ParseFile(string path, long mtime, long size)
    {
        var rows = new List<Row>();
        var inFile = new HashSet<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 20 || !line.Contains("output_tokens")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                string orig = msg.TryGetProperty("model", out var mEl) ? (mEl.GetString() ?? "") : "";
                if (!orig.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) continue;  // Claude only
                string id = msg.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                string rid = root.TryGetProperty("requestId", out var ridEl) ? (ridEl.GetString() ?? "") : "";
                string key = id.Length > 0 ? id + "|" + rid : "";
                if (key.Length > 0 && !inFile.Add(key)) continue;  // dedup within file
                long inp = L(u, "input_tokens"), outp = L(u, "output_tokens"), cr = L(u, "cache_read_input_tokens"), cw = L(u, "cache_creation_input_tokens");
                if (inp == 0 && outp == 0 && cr == 0 && cw == 0) continue;
                rows.Add(new Row { Id = key, Date = DateKey(root), Model = ShortModel(orig), In = inp, Out = outp, Cr = cr, Cw = cw });
            }
            catch { }
        }
        return new FileEntry { Mtime = mtime, Size = size, Rows = rows };
    }

    static long L(JsonElement u, string name) => u.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : 0;
    static string DateKey(JsonElement root)
        => root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(t.GetString(), out var dto)
           ? dto.ToLocalTime().ToString("yyyy-MM-dd") : DateTime.Now.ToString("yyyy-MM-dd");
    static string ShortModel(string m)
    {
        m = m.Replace("claude-", "");
        int i = m.LastIndexOf('-');
        if (i > 0 && i < m.Length - 1 && m[(i + 1)..].All(char.IsDigit) && m.Length - i - 1 >= 6) m = m[..i];
        return m;
    }
    static Dictionary<string, FileEntry> LoadCache()
    {
        try { if (File.Exists(CachePath)) return JsonSerializer.Deserialize<Dictionary<string, FileEntry>>(File.ReadAllText(CachePath)) ?? new(); }
        catch { }
        return new();
    }
    static void SaveCache(Dictionary<string, FileEntry> c)
    { try { Directory.CreateDirectory(CacheDir); File.WriteAllText(CachePath, JsonSerializer.Serialize(c)); } catch { } }
}

static class Summary
{
    public static (Agg agg, double cost) Today()
    {
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        var total = new Agg(); double cost = 0;
        foreach (var kv in Scanner.Data)
        {
            var parts = kv.Key.Split('|', 2);
            if (parts[0] != today) continue;
            total.Add(kv.Value);
            cost += Pricing.Cost(parts.Length > 1 ? parts[1] : "?", kv.Value);
        }
        return (total, cost);
    }

    public static (List<(string model, Agg a, double cost)> rows, double total) MonthByModel()
    {
        string pre = DateTime.Today.ToString("yyyy-MM");
        var by = new Dictionary<string, Agg>(); var cost = new Dictionary<string, double>();
        foreach (var kv in Scanner.Data)
        {
            var parts = kv.Key.Split('|', 2);
            if (!parts[0].StartsWith(pre)) continue;
            var model = parts.Length > 1 ? parts[1] : "?";
            if (!by.TryGetValue(model, out var a)) by[model] = a = new Agg();
            a.Add(kv.Value);
            cost[model] = cost.GetValueOrDefault(model) + Pricing.Cost(model, kv.Value);
        }
        var rows = by.Select(kv => (kv.Key, kv.Value, cost[kv.Key])).OrderByDescending(x => x.Item3).ToList();
        return (rows, cost.Values.Sum());
    }

    public static string FmtTok(long n)
        => n >= 1_000_000 ? (n / 1e6).ToString("0.0") + "M" : n >= 1_000 ? (n / 1e3).ToString("0.0") + "k" : n.ToString();
}

// ---- widget ----
sealed class WidgetForm : Form
{
    readonly System.Windows.Forms.Timer _timer;
    readonly NotifyIcon _tray;
    volatile bool _busy;
    ToolStripMenuItem _topItem = null!;

    List<LimitRow> _limits = new();
    string _apiErr = "";
    long _todayTok; double _todayCost;
    List<(string model, Agg a, double cost)> _month = new();
    double _monthTotal;
    DateTime _lastScan = DateTime.MinValue;
    DateTime _apiBackoffUntil = DateTime.MinValue;
    string _refreshed = "loading...";
    bool _drag; Point _dragOrigin;

    const int Pad = 16, RowH = 46, HeadH = 40, FootH = 30, W = 340;

    static readonly Color Bg     = Color.FromArgb(24, 24, 27);
    static readonly Color Border = Color.FromArgb(64, 64, 70);
    static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
    static readonly Color Amber  = Color.FromArgb(0xE0, 0xA8, 0x3A);
    static readonly Color Red    = Color.FromArgb(0xE0, 0x55, 0x3B);
    static readonly Color Track  = Color.FromArgb(52, 52, 58);
    static readonly Color Muted  = Color.FromArgb(148, 148, 156);
    static readonly Color Fg     = Color.FromArgb(238, 238, 240);
    static readonly Color Line   = Color.FromArgb(46, 46, 52);

    public WidgetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Text = "Claude Usage";
        Size = new Size(W, HeadH + 3 * RowH + 100 + FootH);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 18, wa.Top + 18);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh", null, (_, _) => Rescan());
        _topItem = new ToolStripMenuItem("Always on top", null, (_, _) => { TopMost = !TopMost; _topItem.Checked = TopMost; }) { Checked = true };
        menu.Items.Add(_topItem);
        menu.Items.Add("Show / hide widget", null, (_, _) => ToggleVisible());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Close(); });
        ContextMenuStrip = menu;

        _tray = new NotifyIcon { Icon = MakeIcon(), Visible = true, Text = "Claude Usage", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWidget();

        _timer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 }; // gentle: 5 min
        _timer.Tick += (_, _) => Rescan();
        _timer.Start();

        Load += (_, _) => { ApplyRegion(); Rescan(); };
    }

    void ApplyRegion() => Region = new Region(Rounded(new Rectangle(0, 0, Width, Height), 16));

    static GraphicsPath Rounded(Rectangle r, int rad)
    {
        int d = Math.Max(2, rad * 2);
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Icon MakeIcon()
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var br = new SolidBrush(Color.FromArgb(0xD9, 0x77, 0x57));
            g.FillRectangle(br, 2, 9, 3, 5);
            g.FillRectangle(br, 6, 5, 3, 9);
            g.FillRectangle(br, 10, 2, 3, 12);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    void ToggleVisible() { Visible = !Visible; if (Visible) ShowWidget(); }
    void ShowWidget() { Visible = true; Show(); BringToFront(); Activate(); }

    void Rescan()
    {
        if (_busy) return;
        _busy = true;
        Task.Run(() =>
        {
            var now = DateTime.Now;
            List<LimitRow> lim = new(); string err = ""; bool fetched = false;
            if (now >= _apiBackoffUntil)
            {
                lim = UsageApi.Fetch();
                err = UsageApi.Error;
                fetched = true;
                if (err.Contains("429")) _apiBackoffUntil = now.AddMinutes(30);   // rate limited -> long back-off
                else if (err.Length > 0) _apiBackoffUntil = now.AddMinutes(5);     // other error -> short back-off
            }
            bool scanned = false;
            if ((now - _lastScan).TotalMinutes >= 5)
                try { Scanner.Scan(); _lastScan = now; scanned = true; } catch { }
            _busy = false;
            try { BeginInvoke(() => Apply(lim, err, scanned, fetched)); } catch { }
        });
    }

    void Apply(List<LimitRow> lim, string err, bool scanned, bool fetched)
    {
        if (fetched)   // only touch limits/err when we actually called the API (keep last-good during back-off)
        {
            if (lim.Count > 0 || err.Length == 0) _limits = lim;
            _apiErr = err;
        }
        if (scanned || _todayTok == 0)
        {
            var (a, c) = Summary.Today();
            _todayTok = a.In + a.Out + a.Cr + a.Cw; _todayCost = c;
            var (mrows, mtot) = Summary.MonthByModel();
            _month = mrows; _monthTotal = mtot;
        }
        _refreshed = DateTime.Now.ToString("HH:mm");
        int rows = Math.Max(_limits.Count, 1);
        int shown = Math.Min(_month.Count, 6);
        int monthBlock = 28 + (shown + 1) * 18;
        Height = HeadH + rows * RowH + monthBlock + FootH;
        ApplyRegion();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _drag = true; _dragOrigin = e.Location; } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) Location = new Point(Location.X + e.X - _dragOrigin.X, Location.Y + e.Y - _dragOrigin.Y); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; base.OnMouseUp(e); }

    Color BarColor(int pct, string sev)
        => sev == "critical" || pct >= 90 ? Red : sev == "warning" || pct >= 70 ? Amber : Accent;

    static string ResetText(DateTimeOffset? r)
    {
        if (r == null) return "";
        var ts = r.Value.ToLocalTime() - DateTimeOffset.Now;
        if (ts <= TimeSpan.Zero) return "resets soon";
        return ts.TotalHours >= 1 ? $"resets in {(int)ts.TotalHours}h {ts.Minutes}m" : $"resets in {ts.Minutes}m";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Bg)) g.FillPath(bg, Rounded(new Rectangle(0, 0, Width, Height), 16));
        using (var pen = new Pen(Border)) g.DrawPath(pen, Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 16));

        using var fTitle = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var fLabel = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        using var fPct   = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        using var fSmall = new Font("Segoe UI", 8f);
        using var fB     = new Font("Segoe UI", 8f, FontStyle.Bold);
        using var bAccent = new SolidBrush(Accent);
        using var bFg = new SolidBrush(Fg);
        using var bMuted = new SolidBrush(Muted);

        g.DrawString("CLAUDE  USAGE", fTitle, bAccent, Pad, 12);
        var rtW = g.MeasureString(_refreshed, fSmall);
        g.DrawString(_refreshed, fSmall, bMuted, Width - Pad - rtW.Width, 14);

        int y = HeadH;
        if (_limits.Count == 0)
            g.DrawString(_apiErr.Length > 0 ? _apiErr : "loading...", fSmall, bMuted, Pad, y + 6);
        foreach (var lr in _limits)
        {
            g.DrawString(lr.Label, fLabel, bFg, Pad, y);
            string pct = lr.Percent + "%";
            var pw = g.MeasureString(pct, fPct);
            using (var bp = new SolidBrush(BarColor(lr.Percent, lr.Sev)))
                g.DrawString(pct, fPct, bp, Width - Pad - pw.Width, y - 2);
            int bx = Pad, by = y + 20, bw = Width - 2 * Pad, bh = 8;
            using (var bt = new SolidBrush(Track)) g.FillPath(bt, Rounded(new Rectangle(bx, by, bw, bh), bh / 2));
            int fw = (int)Math.Round(bw * Math.Clamp(lr.Percent, 0, 100) / 100.0);
            if (fw > 0) using (var bf = new SolidBrush(BarColor(lr.Percent, lr.Sev)))
                g.FillPath(bf, Rounded(new Rectangle(bx, by, Math.Max(fw, bh), bh), bh / 2));
            g.DrawString(ResetText(lr.ResetsAt), fSmall, bMuted, Pad, y + 30);
            y += RowH;
        }

        // ---- this month, per model ($ estimate) ----
        y += 2;
        using (var pen = new Pen(Line)) g.DrawLine(pen, Pad, y, Width - Pad, y);
        y += 8;
        g.DrawString("THIS MONTH  ·  est. $ (API-equiv)", fSmall, bAccent, Pad, y);
        y += 18;
        int shown = Math.Min(_month.Count, 6);
        for (int i = 0; i < shown; i++)
        {
            var (model, a, cost) = _month[i];
            long tk = a.In + a.Out + a.Cr + a.Cw;
            g.DrawString(model, fSmall, bFg, Pad, y);
            g.DrawString(Summary.FmtTok(tk), fSmall, bMuted, Pad + 128, y);
            string cs = "~$" + cost.ToString("0.00");
            var cw2 = g.MeasureString(cs, fSmall);
            g.DrawString(cs, fSmall, bFg, Width - Pad - cw2.Width, y);
            y += 18;
        }
        if (_month.Count == 0) { g.DrawString("(no data)", fSmall, bMuted, Pad, y); y += 18; }
        else
        {
            string ts = "~$" + _monthTotal.ToString("0.00");
            var tw = g.MeasureString(ts, fB);
            g.DrawString("total", fB, bAccent, Pad, y);
            g.DrawString(ts, fB, bAccent, Width - Pad - tw.Width, y);
            y += 18;
        }

        // footer
        using (var pen = new Pen(Line)) g.DrawLine(pen, Pad, Height - FootH + 2, Width - Pad, Height - FootH + 2);
        string foot = _todayTok > 0 ? $"today  {Summary.FmtTok(_todayTok)} tok  ·  ~${_todayCost:0.00}" : "today  -";
        g.DrawString(foot, fSmall, bMuted, Pad, Height - FootH + 8);
        if (_apiErr.Length > 0 && _limits.Count > 0)
        {
            var ew = g.MeasureString(_apiErr, fSmall);
            using var bw3 = new SolidBrush(Amber);
            g.DrawString(_apiErr, fSmall, bw3, Width - Pad - ew.Width, Height - FootH + 8);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray.Visible = false; _tray.Dispose(); }
        base.Dispose(disposing);
    }
}
