using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace ClipWatch;

public partial class MainWindow : Window
{
    private readonly Controller _controller;
    private readonly Config _config;

    private ClipsView? _clips;
    private SettingsView? _settings;
    private EditorView? _editor;

    private WindowState _restoreTo = WindowState.Maximized;

    public MainWindow(Controller controller, Config config)
    {
        _controller = controller;
        _config = config;

        InitializeComponent();

        TitleLogo.Source = ToastWindow.LoadLogo();
        Icon = ToastWindow.LoadLogo() as System.Windows.Media.Imaging.BitmapSource;

        StateChanged += (_, _) =>
        {
            SyncMaximiseGlyph();
            if (WindowState != WindowState.Minimized) _restoreTo = WindowState;
        };
        SyncMaximiseGlyph();

        _controller.StatusChanged += OnStatusChanged;
        OnStatusChanged();

        ShowClips();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Space) return;

            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.PasswordBox) return;
            e.Handled = true;
            if (!e.IsRepeat && Host.Content == _editor) _editor?.TogglePlayback();
        };
    }

    private void OnStatusChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var connected = _controller.IsObsConnected;
            var buffering = _controller.BufferActive;

            var (text, key) = !connected
                ? ("Waiting for OBS", "Warn")
                : buffering ? ("Clipping", "Live") : ("Idle", "Muted");

            StatusHeadline.Text = text;
            StatusHeadline.Foreground = (Brush)FindResource(key);
            StatusHeadline.ToolTip = string.IsNullOrWhiteSpace(_controller.StatusLine)
                ? null
                : _controller.StatusLine;
        });
    }

    private void NavClips_Checked(object sender, RoutedEventArgs e) => ShowClips();

    private void NavSettings_Checked(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowView(UIElement view)
    {
        Host.Content = view;
        Motion.RiseIn(view);
    }

    public void ShowClips()
    {
        if (!IsInitialized) return;

        _editor?.Stop();
        _clips ??= new ClipsView(_controller);
        _clips.OnEditRequested = OpenEditor;
        ShowView(_clips);
        _ = _clips.RefreshAsync();
    }

    private void ShowSettings()
    {
        if (!IsInitialized) return;

        _editor?.Stop();
        _settings ??= new SettingsView(_controller, _config);
        _settings.Reload();
        ShowView(_settings);
    }

    private void OpenEditor(Clip clip)
    {
        _editor ??= new EditorView(_controller);
        _editor.OnClosed = () =>
        {
            NavClips.IsChecked = true;
        };
        _editor.Load(clip);
        ShowView(_editor);

        NavClips.IsChecked = false;
        NavSettings.IsChecked = false;
    }

    public void OpenClipByPath(string path)
    {
        var clip = _controller.Library.Clips.FirstOrDefault(c =>
            string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

        if (clip == null)
        {
            clip = new Clip { Path = path, DisplayName = Path.GetFileNameWithoutExtension(path), Created = File.GetCreationTime(path) };
            _ = _controller.Library.EnrichAsync(clip);
        }

        OpenEditor(clip);
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximise(); return; }
        if (e.ButtonState != MouseButtonState.Pressed) return;

        if (WindowState == WindowState.Maximized)
        {
            RestoreUnderCursor(e.GetPosition(this));
        }

        DragMove();
    }

    private void RestoreUnderCursor(Point grabInWindow)
    {
        var ratio = ActualWidth > 0 ? grabInWindow.X / ActualWidth : 0.5;

        var device = PointToScreen(grabInWindow);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        var cursor = transform.Transform(device);

        var restoreWidth = RestoreBounds.Width;
        WindowState = WindowState.Normal;

        if (restoreWidth > 0)
        {
            Left = cursor.X - restoreWidth * ratio;
            Top = cursor.Y - grabInWindow.Y;
        }
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) => ToggleMaximise();

    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void SyncMaximiseGlyph()
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x0002;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => Close();

    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        _editor?.Stop();

        if (AllowClose) return;

        e.Cancel = true;
        Hide();
    }

    public void Reveal()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreTo;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
