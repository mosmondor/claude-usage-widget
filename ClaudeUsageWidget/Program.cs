using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClaudeUsageWidget.Core;

namespace ClaudeUsageWidget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new WidgetForm());
    }
}

/// <summary>Fetches plan limits from the Claude Code usage endpoint; token is read locally at runtime.</summary>
internal static class UsageApi
{
    private static readonly HttpClient Http = CreateClient();

    public static string Error = "";

    private static string CredPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"); }
    }

    private static string CacheFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", "limits.json"); }
    }

    private static HttpClient CreateClient()
    {
        HttpClient c = new HttpClient();
        c.Timeout = TimeSpan.FromSeconds(20);
        return c;
    }

    public static List<LimitRow> Fetch()
    {
        Error = "";
        List<LimitRow> rows = new List<LimitRow>();
        string tok = ReadToken();
        if (tok == null) { Error = "no token (.credentials.json)"; return rows; }
        try
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage"))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tok);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                req.Headers.TryAddWithoutValidation("User-Agent", "claude-usage-widget/1.2");
                using (HttpResponseMessage resp = Http.Send(req))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        int code = (int)resp.StatusCode;
                        if (code == 401) Error = "token expired? run Claude Code";
                        else if (code == 429) Error = "rate limited - retrying";
                        else Error = "HTTP " + code;
                        return rows;
                    }
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    rows = UsageApiParser.Parse(body);
                    if (rows.Count > 0) SaveCache(rows);
                    return rows;
                }
            }
        }
        catch (Exception e)
        {
            Error = e.Message.Length > 46 ? e.Message.Substring(0, 46) : e.Message;
            return rows;
        }
    }

    public static List<LimitRow> LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile))
                return JsonSerializer.Deserialize<List<LimitRow>>(File.ReadAllText(CacheFile)) ?? new List<LimitRow>();
        }
        catch { }
        return new List<LimitRow>();
    }

    private static void SaveCache(List<LimitRow> rows)
    {
        try
        {
            string dir = Path.GetDirectoryName(CacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(rows));
        }
        catch { }
    }

    private static string ReadToken()
    {
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(CredPath)))
                return Find(doc.RootElement);
        }
        catch { return null; }
    }

    private static string Find(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in e.EnumerateObject())
            {
                if ((p.NameEquals("accessToken") || p.NameEquals("access_token")) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
                string r = Find(p.Value);
                if (r != null) return r;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement it in e.EnumerateArray())
            {
                string r = Find(it);
                if (r != null) return r;
            }
        }
        return null;
    }
}

/// <summary>Finds the terminal and shell used to open a session. Windows Terminal is optional.</summary>
internal static class Terminals
{
    public static string FindWt()
    {
        return Which("wt.exe");
    }

    public static string FindShell()
    {
        string pwsh = Which("pwsh.exe");
        return pwsh ?? "powershell.exe";
    }

    private static string Which(string exe)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                string full;
                try { full = Path.Combine(dir.Trim('"'), exe); }
                catch { continue; }
                if (File.Exists(full)) return full;
            }
        }
        catch { }
        return null;
    }
}

/// <summary>
/// Borderless floating card + tray icon. Two pages: plan limits / token spend (Usage) and the
/// session launcher (Sessions), which lists resumable conversations grouped by project.
/// </summary>
internal sealed class WidgetForm : Form
{
    private const int Pad = 16;
    private const int RowH = 46;
    private const int HeadH = 40;
    private const int TabH = 28;
    private const int FootH = 30;
    private const int W = 340;
    private const int BaseMinutes = 5;

    private const int GroupH = 22;
    private const int SessH = 20;
    private const int ListMax = 430;
    private const int MaxProjects = 8;
    private const int MaxPerProject = 6;

    private const int TabUsage = 0;
    private const int TabSessions = 1;

    private static readonly Color Bg = Color.FromArgb(24, 24, 27);
    private static readonly Color Border = Color.FromArgb(64, 64, 70);
    private static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
    private static readonly Color Amber = Color.FromArgb(0xE0, 0xA8, 0x3A);
    private static readonly Color Red = Color.FromArgb(0xE0, 0x55, 0x3B);
    private static readonly Color Green = Color.FromArgb(0x6E, 0xC2, 0x7A);
    private static readonly Color Track = Color.FromArgb(52, 52, 58);
    private static readonly Color Hover = Color.FromArgb(52, 52, 62);   // must read as "this row is clickable"
    private static readonly Color Muted = Color.FromArgb(148, 148, 156);
    private static readonly Color Fg = Color.FromArgb(238, 238, 240);
    private static readonly Color LineC = Color.FromArgb(46, 46, 52);

    private sealed class Hit
    {
        public Rectangle Rect;      // clickable / highlighted area
        public Rectangle Row;       // full-width row the text is laid out in
        public SessionEntry Session;
        public ProjectGroup Group;
        public bool IsNew;
    }

    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly UsageStore _store;
    private readonly ToolStripMenuItem _topItem;
    private readonly string _sessionsDir;
    private readonly string _historyPath;
    private readonly SessionNames _names;
    private readonly string _wtExe;
    private readonly string _shellExe;
    private readonly object _sessionsLock = new object();
    private readonly List<Hit> _hits = new List<Hit>();

    private volatile bool _busy;
    private List<LimitRow> _limits;
    private string _apiErr = "";
    private long _todayTok;
    private double _todayCost;
    private List<(string model, Agg agg, double cost)> _month = new List<(string, Agg, double)>();
    private double _monthTotal;
    private bool _haveTokenData;
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _nextFetch = DateTime.MinValue;
    private int _errStreak;
    private string _refreshed = "loading...";
    private bool _drag;
    private bool _dragMoved;
    private Point _dragOrigin;

    private int _tab = TabUsage;
    private List<ProjectGroup> _groups = new List<ProjectGroup>();
    private List<SessionEntry> _history = new List<SessionEntry>();
    private long _historyMtime;
    private int _scroll;
    private int _contentH;

    /// <summary>
    /// Which row the mouse is over, identified by content rather than by object: every paint
    /// rebuilds the hit list, so a remembered Hit reference would never match again.
    /// </summary>
    private string _hoverKey = "";
    private Rectangle _pillUsage;
    private Rectangle _pillSessions;
    private string _launchErr = "";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public WidgetForm()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _store = new UsageStore(
            Path.Combine(home, ".claude", "projects"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", "cache-v3.json"));
        _sessionsDir = Path.Combine(home, ".claude", "sessions");
        _historyPath = Path.Combine(home, ".claude", "history.jsonl");
        _names = new SessionNames(
            Path.Combine(home, ".claude", "projects"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", "session-names.json"));
        _wtExe = Terminals.FindWt();
        _shellExe = Terminals.FindShell();
        _limits = UsageApi.LoadCache();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Text = "Claude Usage";
        Size = new Size(W, HeadH + TabH + 3 * RowH + 100 + FootH);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 18, wa.Top + 18);

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Refresh", null, (s, e) => Rescan(true));
        _topItem = new ToolStripMenuItem("Always on top", null, (s, e) => { TopMost = !TopMost; _topItem.Checked = TopMost; });
        _topItem.Checked = true;
        menu.Items.Add(_topItem);
        menu.Items.Add("Show / hide widget", null, (s, e) => ToggleVisible());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => { _tray.Visible = false; Close(); });
        ContextMenuStrip = menu;

        _trayIcon = MakeIcon();
        _tray = new NotifyIcon();
        _tray.Icon = _trayIcon;
        _tray.Text = "Claude Usage";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => ShowWidget();

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = 60 * 1000;
        _timer.Tick += (s, e) => Rescan(false);
        _timer.Start();

        Load += (s, e) => { ApplyRegion(); Rescan(true); };
        VisibleChanged += (s, e) => { if (Visible) Rescan(true); };
    }

    private void ToggleVisible()
    {
        Visible = !Visible;
        if (Visible) ShowWidget();
    }

    private void ShowWidget()
    {
        Visible = true;
        Show();
        BringToFront();
        Activate();
    }

    private void ApplyRegion()
    {
        using (GraphicsPath p = Rounded(new Rectangle(0, 0, Width, Height), 16))
        {
            Region old = Region;
            Region = new Region(p);
            if (old != null) old.Dispose();
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        int d = Math.Max(2, rad * 2);
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void FillRounded(Graphics g, Rectangle r, int rad, Color c)
    {
        using (GraphicsPath p = Rounded(r, rad))
        using (SolidBrush b = new SolidBrush(c))
            g.FillPath(b, p);
    }

    private static void DrawRoundedBorder(Graphics g, Rectangle r, int rad, Color c)
    {
        using (GraphicsPath p = Rounded(r, rad))
        using (Pen pen = new Pen(c))
            g.DrawPath(pen, p);
    }

    private static Icon MakeIcon()
    {
        using (Bitmap bmp = new Bitmap(16, 16))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(0xD9, 0x77, 0x57)))
                {
                    g.FillRectangle(br, 2, 9, 3, 5);
                    g.FillRectangle(br, 6, 5, 3, 9);
                    g.FillRectangle(br, 10, 2, 3, 12);
                }
            }
            IntPtr h = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(h);
            }
        }
    }

    private void Rescan(bool force)
    {
        if (_busy) return;
        if (!force && !Visible) return;   // no polling while hidden
        _busy = true;
        Task.Run(new Action(() =>
        {
            DateTime now = DateTime.Now;
            List<LimitRow> lim = new List<LimitRow>();
            string err = "";
            bool ok = false;
            bool fetched = false;
            if (force || now >= _nextFetch)
            {
                fetched = true;
                lim = UsageApi.Fetch();
                err = UsageApi.Error;
                ok = err.Length == 0 && lim.Count > 0;
                if (ok)
                {
                    _errStreak = 0;
                    _nextFetch = now.AddMinutes(BaseMinutes);
                }
                else
                {
                    if (_errStreak < 6) _errStreak++;
                    double back = Math.Min(60.0, Math.Pow(2, _errStreak - 1)); // 1,2,4,8,16,32 -> cap 60 min
                    _nextFetch = now.AddMinutes(back);
                }
            }
            bool scanned = false;
            if (force || (now - _lastScan).TotalMinutes >= BaseMinutes)
            {
                try { _store.Scan(); _lastScan = now; scanned = true; }
                catch { }
            }
            List<ProjectGroup> groups = LoadGroups();
            _busy = false;
            try { BeginInvoke(new Action(() => Apply(lim, err, scanned, ok, fetched, groups))); }
            catch { }
        }));
    }

    /// <summary>Sessions only: no HTTP, no transcript scan. Used when switching to the Sessions tab.</summary>
    private void RefreshSessions()
    {
        Task.Run(new Action(() =>
        {
            List<ProjectGroup> groups = LoadGroups();
            try { BeginInvoke(new Action(() => { _groups = groups; Relayout(); })); }
            catch { }
        }));
    }

    private List<ProjectGroup> LoadGroups()
    {
        lock (_sessionsLock)
        {
            try
            {
                List<LiveSession> live = SessionsReader.Read(_sessionsDir);
                long mtime = 0;
                FileInfo fi = new FileInfo(_historyPath);
                if (fi.Exists) mtime = fi.LastWriteTimeUtc.Ticks;
                if (mtime != _historyMtime || _history.Count == 0)
                {
                    _history = HistoryReader.Read(_historyPath);
                    _historyMtime = mtime;
                }
                ResolveNames(live);
                return SessionList.Build(_history, live, MaxProjects, MaxPerProject, null, true);
            }
            catch { return new List<ProjectGroup>(); }
        }
    }

    /// <summary>
    /// Names have to be known before grouping, because the list keeps only named sessions.
    /// Running sessions are skipped: their name is already in the session file, and their
    /// transcript is still growing, so scanning it would repeat on every refresh.
    /// </summary>
    private void ResolveNames(List<LiveSession> live)
    {
        HashSet<string> running = new HashSet<string>(StringComparer.Ordinal);
        foreach (LiveSession ls in live) running.Add(ls.SessionId);

        List<string> need = new List<string>();
        foreach (SessionEntry s in _history)
            if (!s.HasName && !running.Contains(s.SessionId)) need.Add(s.SessionId);
        if (need.Count == 0) return;

        Dictionary<string, string> named = _names.Resolve(need);
        foreach (SessionEntry s in _history)
        {
            string n;
            if (!s.HasName && named.TryGetValue(s.SessionId, out n)) s.Name = n;
        }
    }

    private void Apply(List<LimitRow> lim, string err, bool scanned, bool ok, bool fetched, List<ProjectGroup> groups)
    {
        if (fetched)
        {
            if (ok) _limits = lim;
            _apiErr = err;
        }
        if (scanned || !_haveTokenData)
        {
            Agg todayAgg;
            double todayCost;
            (todayAgg, todayCost) = _store.Today();
            _todayTok = todayAgg.Total;
            _todayCost = todayCost;

            List<(string model, Agg agg, double cost)> monthRows;
            double monthTotal;
            (monthRows, monthTotal) = _store.MonthByModel();
            _month = monthRows;
            _monthTotal = monthTotal;
            _haveTokenData = true;
        }
        if (groups != null) _groups = groups;
        _refreshed = DateTime.Now.ToString("HH:mm");
        Relayout();
    }

    private void Relayout()
    {
        if (_tab == TabSessions)
        {
            _contentH = MeasureSessions();
            int listH = Math.Min(_contentH, ListMax);
            if (listH < 60) listH = 60;
            int maxScroll = Math.Max(0, _contentH - listH);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;
            Height = HeadH + TabH + listH + FootH;
        }
        else
        {
            int rowCount = Math.Max(_limits.Count, 1);
            int shown = Math.Min(_month.Count, 6);
            int monthBlock = 28 + (shown + 1) * 18;
            Height = HeadH + TabH + rowCount * RowH + monthBlock + FootH;
        }
        ApplyRegion();
        Invalidate();
    }

    private int MeasureSessions()
    {
        int h = 8;
        foreach (ProjectGroup g in _groups)
            h += GroupH + g.Sessions.Count * SessH + 6;
        if (_groups.Count == 0) h += 24;
        return h;
    }

    private Rectangle ListRect
    {
        get { return new Rectangle(0, HeadH + TabH, Width, Height - HeadH - TabH - FootH); }
    }

    /// <summary>Recomputes the clickable rectangles for the Sessions page (scroll included).</summary>
    private void LayoutHits()
    {
        _hits.Clear();
        if (_tab != TabSessions) return;

        int y = HeadH + TabH + 8 - _scroll;
        foreach (ProjectGroup g in _groups)
        {
            // only the badge on the right starts a NEW session; clicking the project name must not
            Hit head = new Hit();
            head.Row = new Rectangle(Pad - 6, y - 2, Width - 2 * Pad + 12, GroupH);
            head.Rect = new Rectangle(Width - Pad - 46, y - 2, 52, GroupH - 2);
            head.Group = g;
            head.IsNew = true;
            _hits.Add(head);
            y += GroupH;

            foreach (SessionEntry s in g.Sessions)
            {
                Hit hit = new Hit();
                hit.Row = new Rectangle(Pad - 6, y - 2, Width - 2 * Pad + 12, SessH - 2);
                hit.Rect = hit.Row;
                hit.Session = s;
                hit.Group = g;
                _hits.Add(hit);
                y += SessH;
            }
            y += 6;
        }
    }

    private Hit HitTest(Point p)
    {
        if (!ListRect.Contains(p)) return null;
        foreach (Hit h in _hits)
            if (h.Rect.Contains(p)) return h;
        return null;
    }

    private static string HitKey(Hit h)
    {
        if (h == null) return "";
        if (h.IsNew) return h.Group == null ? "" : "new:" + h.Group.Project;
        return h.Session == null ? "" : "session:" + h.Session.SessionId;
    }

    private void SetHover(Hit h)
    {
        string key = Clickable(h) ? HitKey(h) : "";
        if (key == _hoverKey) return;
        _hoverKey = key;
        Cursor = key.Length > 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    private static bool Clickable(Hit h)
    {
        if (h == null) return false;
        if (h.IsNew) return h.Group != null && h.Group.Exists;
        if (h.Session == null) return false;
        if (h.Session.IsLive) return h.Session.Pid > 0;      // click brings its terminal to the front
        return h.Group != null && h.Group.Exists && LaunchCommand.IsValidSessionId(h.Session.SessionId);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _drag = true; _dragMoved = false; _dragOrigin = e.Location; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag)
        {
            // a few pixels of slop, so a click on a row is not swallowed by the window drag
            if (!_dragMoved && (Math.Abs(e.X - _dragOrigin.X) > 3 || Math.Abs(e.Y - _dragOrigin.Y) > 3)) _dragMoved = true;
            if (_dragMoved) Location = new Point(Location.X + e.X - _dragOrigin.X, Location.Y + e.Y - _dragOrigin.Y);
        }
        else if (_tab == TabSessions)
        {
            SetHover(HitTest(e.Location));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasDrag = _dragMoved;
        _drag = false;
        _dragMoved = false;
        if (e.Button == MouseButtons.Left && !wasDrag) HandleClick(e.Location);
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        SetHover(null);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_tab == TabSessions && _contentH > ListRect.Height)
        {
            int maxScroll = Math.Max(0, _contentH - ListRect.Height);
            _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * SessH, 0, maxScroll);
            LayoutHits();
            SetHover(HitTest(e.Location));
            Invalidate();
        }
        base.OnMouseWheel(e);
    }

    private void HandleClick(Point p)
    {
        if (_pillUsage.Contains(p)) { SwitchTab(TabUsage); return; }
        if (_pillSessions.Contains(p)) { SwitchTab(TabSessions); return; }
        if (_tab != TabSessions) return;

        LayoutHits();
        Hit h = HitTest(p);
        if (!Clickable(h)) return;
        if (h.IsNew) Launch(LaunchCommand.NewSession(_wtExe, _shellExe, h.Group.Project, h.Group.ProjectName), h.Group.Project);
        else if (h.Session.IsLive) FocusSession(h.Session);
        else Launch(LaunchCommand.Resume(_wtExe, _shellExe, h.Session.Project, h.Session.SessionId, h.Session.ProjectName), h.Session.Project);
    }

    private void FocusSession(SessionEntry s)
    {
        _launchErr = WindowFocus.Focus(s.Pid) ? "" : "no window for pid " + s.Pid;
        Invalidate();
    }

    private void SwitchTab(int tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        _scroll = 0;
        _hoverKey = "";
        Cursor = Cursors.Default;
        _launchErr = "";
        Relayout();
        if (tab == TabSessions) RefreshSessions();
    }

    private void Launch((string exe, string args) cmd, string cwd)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(cmd.exe, cmd.args);
            psi.UseShellExecute = false;                                    // required to edit the environment
            if (string.IsNullOrEmpty(_wtExe)) psi.WorkingDirectory = cwd;   // no wt: the shell window needs it
            ScrubEnvironment(psi);
            Process.Start(psi);
            _launchErr = "";
        }
        catch (Exception e)
        {
            _launchErr = e.Message.Length > 40 ? e.Message.Substring(0, 40) : e.Message;
        }
        Invalidate();
    }

    /// <summary>
    /// Drops the Claude Code session markers this process may have inherited, so the session being
    /// started is a real top-level one. Without this a widget launched from inside Claude Code
    /// hands CLAUDE_CODE_CHILD_SESSION to the new terminal and its transcript is never saved.
    /// </summary>
    private static void ScrubEnvironment(ProcessStartInfo psi)
    {
        List<string> current = new List<string>(psi.Environment.Keys);
        foreach (string name in ChildEnv.NamesToRemove(current, PersistedEnvNames()))
            psi.Environment.Remove(name);
    }

    private static List<string> PersistedEnvNames()
    {
        List<string> names = new List<string>();
        AddKeyNames(names, Registry.CurrentUser, "Environment");
        AddKeyNames(names, Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment");
        return names;
    }

    private static void AddKeyNames(List<string> into, RegistryKey root, string path)
    {
        try
        {
            using (RegistryKey key = root.OpenSubKey(path))
            {
                if (key == null) return;
                foreach (string name in key.GetValueNames()) into.Add(name);
            }
        }
        catch { }
    }

    private static Color BarColor(int pct, string sev)
    {
        if (sev == "critical" || pct >= 90) return Red;
        if (sev == "warning" || pct >= 70) return Amber;
        return Accent;
    }

    private static string ResetText(DateTimeOffset? r)
    {
        if (r == null) return "";
        TimeSpan ts = r.Value.ToLocalTime() - DateTimeOffset.Now;
        if (ts <= TimeSpan.Zero) return "resets soon";
        if (ts.TotalHours >= 1) return "resets in " + (int)ts.TotalHours + "h " + ts.Minutes + "m";
        return "resets in " + ts.Minutes + "m";
    }

    private static string FmtTok(long n)
    {
        if (n >= 1000000) return (n / 1e6).ToString("0.0") + "M";
        if (n >= 1000) return (n / 1e3).ToString("0.0") + "k";
        return n.ToString();
    }

    private static string FmtWhen(DateTime t)
    {
        if (t == DateTime.MinValue) return "";
        DateTime now = DateTime.Now;
        if (t.Date == now.Date) return t.ToString("HH:mm");
        if (t.Year == now.Year) return t.ToString("d.M.");
        return t.ToString("d.M.yy.");
    }

    private static string Fit(Graphics g, string s, Font f, float max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (g.MeasureString(s, f).Width <= max) return s;
        int lo = 1;
        int hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (g.MeasureString(s.Substring(0, mid) + "…", f).Width <= max) lo = mid;
            else hi = mid - 1;
        }
        return s.Substring(0, lo) + "…";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        FillRounded(g, new Rectangle(0, 0, Width, Height), 16, Bg);
        DrawRoundedBorder(g, new Rectangle(0, 0, Width - 1, Height - 1), 16, Border);

        using (Font fTitle = new Font("Segoe UI", 9.5f, FontStyle.Bold))
        using (Font fLabel = new Font("Segoe UI", 9.5f, FontStyle.Bold))
        using (Font fPct = new Font("Segoe UI Semibold", 11f, FontStyle.Bold))
        using (Font fSmall = new Font("Segoe UI", 8f))
        using (Font fBold = new Font("Segoe UI", 8f, FontStyle.Bold))
        using (Font fTiny = new Font("Segoe UI", 7.5f))
        using (SolidBrush bAccent = new SolidBrush(Accent))
        using (SolidBrush bFg = new SolidBrush(Fg))
        using (SolidBrush bMuted = new SolidBrush(Muted))
        {
            g.DrawString("CLAUDE  USAGE", fTitle, bAccent, Pad, 12);
            SizeF rtW = g.MeasureString(_refreshed, fSmall);
            g.DrawString(_refreshed, fSmall, bMuted, Width - Pad - rtW.Width, 14);

            PaintTabs(g, fBold);

            if (_tab == TabSessions) PaintSessions(g, fBold, fSmall, fTiny, bMuted);
            else PaintUsage(g, fLabel, fPct, fSmall, fBold, bFg, bMuted, bAccent);

            using (Pen pen = new Pen(LineC)) g.DrawLine(pen, Pad, Height - FootH + 2, Width - Pad, Height - FootH + 2);
            PaintFooter(g, fSmall, bMuted);
        }
    }

    private void PaintTabs(Graphics g, Font f)
    {
        int y = HeadH - 4;
        _pillUsage = PaintPill(g, f, "Usage", Pad, y, _tab == TabUsage);
        _pillSessions = PaintPill(g, f, "Sessions", _pillUsage.Right + 6, y, _tab == TabSessions);
    }

    private Rectangle PaintPill(Graphics g, Font f, string text, int x, int y, bool active)
    {
        SizeF sz = g.MeasureString(text, f);
        Rectangle r = new Rectangle(x, y, (int)sz.Width + 18, 20);
        if (active) FillRounded(g, r, 10, Track);
        using (SolidBrush b = new SolidBrush(active ? Fg : Muted))
            g.DrawString(text, f, b, r.X + 9, r.Y + 3);
        return r;
    }

    private void PaintUsage(Graphics g, Font fLabel, Font fPct, Font fSmall, Font fBold, SolidBrush bFg, SolidBrush bMuted, SolidBrush bAccent)
    {
        int y = HeadH + TabH;
        if (_limits.Count == 0)
            g.DrawString(_apiErr.Length > 0 ? _apiErr : "loading...", fSmall, bMuted, Pad, y + 6);

        foreach (LimitRow lr in _limits)
        {
            g.DrawString(lr.Label, fLabel, bFg, Pad, y);
            string pct = lr.Percent + "%";
            SizeF pw = g.MeasureString(pct, fPct);
            Color col = BarColor(lr.Percent, lr.Severity);
            using (SolidBrush bp = new SolidBrush(col))
                g.DrawString(pct, fPct, bp, Width - Pad - pw.Width, y - 2);

            int bx = Pad;
            int by = y + 20;
            int bw = Width - 2 * Pad;
            int bh = 8;
            FillRounded(g, new Rectangle(bx, by, bw, bh), bh / 2, Track);
            int fw = (int)Math.Round(bw * Math.Clamp(lr.Percent, 0, 100) / 100.0);
            if (fw > 0)
                FillRounded(g, new Rectangle(bx, by, Math.Max(fw, bh), bh), bh / 2, col);

            g.DrawString(ResetText(lr.ResetsAt), fSmall, bMuted, Pad, y + 30);
            y += RowH;
        }

        y += 2;
        using (Pen pen = new Pen(LineC)) g.DrawLine(pen, Pad, y, Width - Pad, y);
        y += 8;
        g.DrawString("THIS MONTH  ·  est. $ (API-equiv)", fSmall, bAccent, Pad, y);
        y += 18;

        int shown = Math.Min(_month.Count, 6);
        for (int i = 0; i < shown; i++)
        {
            (string model, Agg agg, double cost) row = _month[i];
            g.DrawString(row.model, fSmall, bFg, Pad, y);
            g.DrawString(FmtTok(row.agg.Total), fSmall, bMuted, Pad + 128, y);
            string cs = "~$" + row.cost.ToString("0.00");
            SizeF cw = g.MeasureString(cs, fSmall);
            g.DrawString(cs, fSmall, bFg, Width - Pad - cw.Width, y);
            y += 18;
        }
        if (_month.Count == 0)
        {
            g.DrawString("(no data)", fSmall, bMuted, Pad, y);
        }
        else
        {
            string ts = "~$" + _monthTotal.ToString("0.00");
            SizeF tw = g.MeasureString(ts, fBold);
            g.DrawString("total", fBold, bAccent, Pad, y);
            g.DrawString(ts, fBold, bAccent, Width - Pad - tw.Width, y);
        }
    }

    private void PaintSessions(Graphics g, Font fBold, Font fSmall, Font fTiny, SolidBrush bMuted)
    {
        LayoutHits();
        Rectangle list = ListRect;
        g.SetClip(list);

        if (_groups.Count == 0)
            g.DrawString("(no sessions found)", fSmall, bMuted, Pad, list.Y + 10);

        foreach (Hit h in _hits)
        {
            if (h.Row.Bottom < list.Y || h.Row.Y > list.Bottom) continue;

            bool hot = _hoverKey.Length > 0 && HitKey(h) == _hoverKey && Clickable(h);
            if (hot) FillRounded(g, h.Rect, 6, Hover);

            if (h.IsNew) PaintGroupHeader(g, h, fBold, fTiny, hot);
            else PaintSessionRow(g, h, fSmall, fTiny, hot);
        }

        g.ResetClip();
        if (_contentH > list.Height) PaintScrollbar(g, list);
    }

    private void PaintGroupHeader(Graphics g, Hit h, Font fBold, Font fTiny, bool hot)
    {
        ProjectGroup grp = h.Group;
        int y = h.Row.Y + 3;
        int x = Pad;

        if (grp.HasLive)
        {
            using (SolidBrush dot = new SolidBrush(LiveColor(grp)))
                g.FillEllipse(dot, x, y + 5, 6, 6);
            x += 11;
        }

        string right = hot ? "+ new" : (grp.HasLive ? "live" : FmtWhen(grp.LastActivity));
        SizeF rw = g.MeasureString(right, fTiny);
        string name = Fit(g, grp.ProjectName, fBold, Width - Pad - x - rw.Width - 10);
        using (SolidBrush b = new SolidBrush(grp.Exists ? Accent : Muted))
            g.DrawString(name, fBold, b, x, y);

        using (SolidBrush b = new SolidBrush(hot ? Accent : (grp.HasLive ? LiveColor(grp) : Muted)))
            g.DrawString(right, fTiny, b, Width - Pad - rw.Width, y + 1);
    }

    private static Color LiveColor(ProjectGroup g)
    {
        foreach (SessionEntry s in g.Sessions)
            if (s.IsLive && string.Equals(s.Status, "busy", StringComparison.OrdinalIgnoreCase)) return Amber;
        return Green;
    }

    private void PaintSessionRow(Graphics g, Hit h, Font fSmall, Font fTiny, bool hot)
    {
        SessionEntry s = h.Session;
        int y = h.Row.Y + 2;
        int x = Pad + 10;

        string right = s.IsLive ? (s.Status.Length > 0 ? s.Status : "open") : FmtWhen(s.LastActivity);
        SizeF rw = g.MeasureString(right, fTiny);

        // a named session is the whole point of the row; an unnamed one falls back to its topic, dimmed
        Color c;
        if (s.HasName) c = hot || s.IsLive ? Fg : Color.FromArgb(214, 214, 220);
        else c = Muted;
        using (SolidBrush b = new SolidBrush(c))
            g.DrawString(Fit(g, s.Label, fSmall, Width - Pad - x - rw.Width - 8), fSmall, b, x, y);

        using (SolidBrush b = new SolidBrush(s.IsLive ? LiveStatusColor(s) : Muted))
            g.DrawString(right, fTiny, b, Width - Pad - rw.Width, y + 1);
    }

    private static Color LiveStatusColor(SessionEntry s)
    {
        return string.Equals(s.Status, "busy", StringComparison.OrdinalIgnoreCase) ? Amber : Green;
    }

    private void PaintScrollbar(Graphics g, Rectangle list)
    {
        int trackH = list.Height - 8;
        if (trackH <= 0) return;
        int thumbH = Math.Max(24, (int)(trackH * (double)list.Height / _contentH));
        int maxScroll = Math.Max(1, _contentH - list.Height);
        int ty = list.Y + 4 + (int)((trackH - thumbH) * (_scroll / (double)maxScroll));
        FillRounded(g, new Rectangle(Width - 7, ty, 3, thumbH), 2, Track);
    }

    private void PaintFooter(Graphics g, Font fSmall, SolidBrush bMuted)
    {
        int y = Height - FootH + 8;
        if (_tab == TabSessions)
        {
            int live = 0;
            int total = 0;
            foreach (ProjectGroup grp in _groups)
            {
                total += grp.Sessions.Count;
                foreach (SessionEntry s in grp.Sessions) if (s.IsLive) live++;
            }
            string left = total + " sessions  ·  " + live + " live";
            g.DrawString(left, fSmall, bMuted, Pad, y);

            string right = _launchErr.Length > 0 ? _launchErr : "click: focus / resume";
            SizeF rw = g.MeasureString(right, fSmall);
            using (SolidBrush b = new SolidBrush(_launchErr.Length > 0 ? Red : Muted))
                g.DrawString(right, fSmall, b, Width - Pad - rw.Width, y);
            return;
        }

        string foot = _todayTok > 0 ? "today  " + FmtTok(_todayTok) + " tok  ·  ~$" + _todayCost.ToString("0.00") : "today  -";
        g.DrawString(foot, fSmall, bMuted, Pad, y);
        if (_apiErr.Length > 0 && _limits.Count > 0)
        {
            SizeF ew = g.MeasureString(_apiErr, fSmall);
            using (SolidBrush bWarn = new SolidBrush(Amber))
                g.DrawString(_apiErr, fSmall, bWarn, Width - Pad - ew.Width, y);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timer != null) _timer.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_trayIcon != null) _trayIcon.Dispose();
            if (Region != null) Region.Dispose();
        }
        base.Dispose(disposing);
    }
}
