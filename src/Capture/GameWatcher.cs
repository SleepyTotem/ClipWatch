using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace ClipWatch;

public sealed class GameWatcher
{
    private readonly Config _config;
    private readonly GameList _games;
    private readonly DispatcherTimer _timer;

    private DateTime? _pendingStopSince;
    private int _consecutiveActivePolls;

    public bool GameActive { get; private set; }
    public string? ActiveProcessName { get; private set; }

    public string? LastForegroundProcess { get; private set; }

    public event Action<bool, string?>? StateChanged;

    public GameWatcher(Config config, GameList games)
    {
        _config = config;
        _games = games;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(250, config.PollIntervalMs))
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void PollNow() => Poll();

    public void ResetLock()
    {
        _consecutiveActivePolls = 0;
        _pendingStopSince = null;
        if (!GameActive) return;

        var was = ActiveProcessName;
        GameActive = false;
        ActiveProcessName = null;
        StateChanged?.Invoke(false, was);
    }

    private void Poll()
    {
        try { LastForegroundProcess = GetForegroundProcessName(); }
        catch { LastForegroundProcess = null; }

        if (GameActive)
        {
            if (IsProcessStillRunning(ActiveProcessName))
            {
                _pendingStopSince = null;
                return;
            }

            _pendingStopSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _pendingStopSince.Value >= TimeSpan.FromSeconds(_config.StopDelaySeconds))
                ResetLock();

            return;
        }

        string? processName = null;
        var looksLikeGame = false;

        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                processName = GetProcessName(hwnd);
                if (processName != null)
                    looksLikeGame = Evaluate(hwnd, processName);
            }
        }
        catch
        {
        }

        if (!looksLikeGame)
        {
            _consecutiveActivePolls = 0;
            return;
        }

        _consecutiveActivePolls++;

        if (_consecutiveActivePolls >= 2)
        {
            GameActive = true;
            ActiveProcessName = processName;
            _pendingStopSince = null;
            StateChanged?.Invoke(true, processName);
        }
    }

    private bool Evaluate(IntPtr hwnd, string processName)
    {
        if (_games.Contains(processName)) return true;

        if (!_config.UseFullscreenHeuristic) return false;

        if (IsIgnored(processName)) return false;

        if (_config.GameProcessWhitelist.Count > 0)
        {
            return _config.GameProcessWhitelist
                .Any(p => string.Equals(TrimExe(p), processName, StringComparison.OrdinalIgnoreCase));
        }

        return IsFullscreenOnItsMonitor(hwnd);
    }

    private bool IsIgnored(string processName) =>
        _config.IgnoredProcesses.Any(p =>
            string.Equals(TrimExe(p), processName, StringComparison.OrdinalIgnoreCase));

    private static string TrimExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsProcessStillRunning(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFullscreenOnItsMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var win)) return false;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var mon = info.rcMonitor;

        const int slop = 2;
        return win.Left <= mon.Left + slop &&
               win.Top <= mon.Top + slop &&
               win.Right >= mon.Right - slop &&
               win.Bottom >= mon.Bottom - slop;
    }

    public static string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            return hwnd == IntPtr.Zero ? null : GetProcessName(hwnd);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                var size = sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref size))
                    return Path.GetFileNameWithoutExtension(sb.ToString());
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    #region Win32

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
