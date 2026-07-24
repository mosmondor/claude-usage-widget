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
                req.Headers.TryAddWithoutValidation("User-Agent", "claude-usage-widget/1.1");
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

/// <summary>Borderless floating card + tray icon. Plan-limit bars from the API, month/today from local transcripts.</summary>
internal sealed class WidgetForm : Form
{
    private const int Pad = 16;
    private const int RowH = 46;
    private const int HeadH = 40;
    private const int FootH = 30;
    private const int W = 340;
    private const int BaseMinutes = 5;

    private static readonly Color Bg = Color.FromArgb(24, 24, 27);
    private static readonly Color Border = Color.FromArgb(64, 64, 70);
    private static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
    private static readonly Color Amber = Color.FromArgb(0xE0, 0xA8, 0x3A);
    private static readonly Color Red = Color.FromArgb(0xE0, 0x55, 0x3B);
    private static readonly Color Track = Color.FromArgb(52, 52, 58);
    private static readonly Color Muted = Color.FromArgb(148, 148, 156);
    private static readonly Color Fg = Color.FromArgb(238, 238, 240);
    private static readonly Color LineC = Color.FromArgb(46, 46, 52);

    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly UsageStore _store;
    private readonly ToolStripMenuItem _topItem;

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
    private Point _dragOrigin;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public WidgetForm()
    {
        _store = new UsageStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget", "cache-v3.json"));
        _limits = UsageApi.LoadCache();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Text = "Claude Usage";
        Size = new Size(W, HeadH + 3 * RowH + 100 + FootH);
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
            _busy = false;
            try { BeginInvoke(new Action(() => Apply(lim, err, scanned, ok, fetched))); }
            catch { }
        }));
    }

    private void Apply(List<LimitRow> lim, string err, bool scanned, bool ok, bool fetched)
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
        _refreshed = DateTime.Now.ToString("HH:mm");

        int rowCount = Math.Max(_limits.Count, 1);
        int shown = Math.Min(_month.Count, 6);
        int monthBlock = 28 + (shown + 1) * 18;
        Height = HeadH + rowCount * RowH + monthBlock + FootH;
        ApplyRegion();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _drag = true; _dragOrigin = e.Location; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) Location = new Point(Location.X + e.X - _dragOrigin.X, Location.Y + e.Y - _dragOrigin.Y);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _drag = false;
        base.OnMouseUp(e);
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
        using (SolidBrush bAccent = new SolidBrush(Accent))
        using (SolidBrush bFg = new SolidBrush(Fg))
        using (SolidBrush bMuted = new SolidBrush(Muted))
        {
            g.DrawString("CLAUDE  USAGE", fTitle, bAccent, Pad, 12);
            SizeF rtW = g.MeasureString(_refreshed, fSmall);
            g.DrawString(_refreshed, fSmall, bMuted, Width - Pad - rtW.Width, 14);

            int y = HeadH;
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
                y += 18;
            }
            else
            {
                string ts = "~$" + _monthTotal.ToString("0.00");
                SizeF tw = g.MeasureString(ts, fBold);
                g.DrawString("total", fBold, bAccent, Pad, y);
                g.DrawString(ts, fBold, bAccent, Width - Pad - tw.Width, y);
                y += 18;
            }

            using (Pen pen = new Pen(LineC)) g.DrawLine(pen, Pad, Height - FootH + 2, Width - Pad, Height - FootH + 2);
            string foot = _todayTok > 0 ? "today  " + FmtTok(_todayTok) + " tok  ·  ~$" + _todayCost.ToString("0.00") : "today  -";
            g.DrawString(foot, fSmall, bMuted, Pad, Height - FootH + 8);
            if (_apiErr.Length > 0 && _limits.Count > 0)
            {
                SizeF ew = g.MeasureString(_apiErr, fSmall);
                using (SolidBrush bWarn = new SolidBrush(Amber))
                    g.DrawString(_apiErr, fSmall, bWarn, Width - Pad - ew.Width, Height - FootH + 8);
            }
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
