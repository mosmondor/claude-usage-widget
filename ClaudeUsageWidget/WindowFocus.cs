using System.Runtime.InteropServices;

namespace ClaudeUsageWidget;

/// <summary>
/// Brings a running session's terminal window to the front.
/// <para>
/// claude.exe has no window of its own — it runs inside a shell (pwsh.exe), which in turn may run
/// inside Windows Terminal. So the search walks up the parent chain from the claude process and
/// takes the first top-level window owned by anything in that chain. With Windows Terminal this
/// focuses the window but cannot select the individual tab: there is no API for that.
/// </para>
/// </summary>
internal static class WindowFocus
{
    private const int MaxDepth = 5;
    private const int SwRestore = 9;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    public static bool Focus(int pid)
    {
        if (pid <= 0) return false;
        List<int> chain = ParentChain(pid);
        IntPtr hwnd = FindWindowFor(chain);
        if (hwnd == IntPtr.Zero) return false;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        BringWindowToTop(hwnd);
        // allowed here because the click made this widget the foreground window
        return SetForegroundWindow(hwnd);
    }

    /// <summary>The process itself, then its ancestors, nearest first. Stops at the shell/desktop.</summary>
    private static List<int> ParentChain(int pid)
    {
        List<int> chain = new List<int> { pid };
        Dictionary<int, int> parents = new Dictionary<int, int>();
        Dictionary<int, string> names = new Dictionary<int, string>();

        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return chain;
        try
        {
            PROCESSENTRY32W e = new PROCESSENTRY32W();
            e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
            if (!Process32FirstW(snap, ref e)) return chain;
            do
            {
                parents[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                names[(int)e.th32ProcessID] = e.szExeFile ?? "";
            }
            while (Process32NextW(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }

        int current = pid;
        for (int i = 0; i < MaxDepth; i++)
        {
            int parent;
            if (!parents.TryGetValue(current, out parent) || parent <= 0 || parent == current) break;
            string name;
            names.TryGetValue(parent, out name);
            if (name != null && (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("services.exe", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))) break;
            chain.Add(parent);
            current = parent;
        }
        return chain;
    }

    /// <summary>Prefers a window owned by the process closest to claude itself.</summary>
    private static IntPtr FindWindowFor(List<int> chain)
    {
        IntPtr best = IntPtr.Zero;
        int bestRank = int.MaxValue;

        EnumWindows(new EnumWindowsProc((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (GetWindow(hWnd, 4) != IntPtr.Zero) return true;   // GW_OWNER: skip tool/child windows
            if (GetWindowTextLength(hWnd) == 0) return true;

            uint wpid;
            GetWindowThreadProcessId(hWnd, out wpid);
            int rank = chain.IndexOf((int)wpid);
            if (rank < 0 || rank >= bestRank) return true;

            bestRank = rank;
            best = hWnd;
            return rank > 0;   // a window on claude itself is as good as it gets
        }), IntPtr.Zero);

        return best;
    }
}
