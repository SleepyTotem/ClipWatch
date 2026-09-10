using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClipWatch;

public sealed class HotkeyManager : IDisposable
{
    public const int SaveHotkeyId = 1;
    public const int LearnHotkeyId = 2;
    public const int PlaybackHotkeyId = 3;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = new();

    public event Action<int>? Pressed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ClipWatchHotkeySink")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool Register(int id, int virtualKey, uint modifiers = 0)
    {
        Unregister(id);

        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, (uint)virtualKey))
            return false;

        _registered.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (!_registered.Remove(id)) return;
        UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        var id = wParam.ToInt32();
        if (!_registered.Contains(id)) return IntPtr.Zero;

        Pressed?.Invoke(id);
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.ToArray()) Unregister(id);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
