using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace ClipWatch;

public static class Program
{
    private static NotifyIcon? _tray;
    private static Controller? _controller;
    private static Config? _config;
    private static MainWindow? _window;
    private static PlaybackWindow? _playback;

    [STAThread]
    public static void Main()
    {
        using var showRequest = new EventWaitHandle(
            false, EventResetMode.AutoReset, ShowRequestName);

        using var mutex = new Mutex(true, "ClipWatch.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            showRequest.Set();
            return;
        }

        _config = Config.Load();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ClipWatch;component/resources/Theme.xaml", UriKind.Absolute)
        });

        app.DispatcherUnhandledException += (_, e) =>
        {
            _tray?.ShowBalloonTip(4000, "ClipWatch error", e.Exception.Message, ToolTipIcon.Error);
            e.Handled = true;
        };

        _controller = new Controller(_config);
        _controller.StatusChanged += UpdateTrayText;
        _controller.TempReplayReady += path =>
            Application.Current.Dispatcher.InvokeAsync(() => ShowPlayback(path));

        BuildTray();
        ListenForShowRequests(showRequest, app);
        _controller.Start();
        UpdateTrayText();

        if (!_config.StartMinimised) ShowMainWindow();

        app.Run();
    }

    private const string ShowRequestName = "ClipWatch.ShowWindow";

    private static void ListenForShowRequests(EventWaitHandle request, Application app)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    request.WaitOne();
                }
                catch (ObjectDisposedException) { return; }
                catch (AbandonedMutexException) { }

                try
                {
                    app.Dispatcher.InvokeAsync(ShowMainWindow);
                }
                catch (TaskCanceledException) { return; }
                catch (InvalidOperationException) { return; }
            }
        })
        {
            IsBackground = true,
            Name = "ClipWatch second-launch listener"
        };

        thread.Start();
    }

    private static readonly Color TrayInk = Color.FromArgb(0x14, 0x13, 0x11);
    private static readonly Color TrayInkHot = Color.FromArgb(0x23, 0x21, 0x20);
    private static readonly Color TrayLine = Color.FromArgb(0x2E, 0x2A, 0x27);
    private static readonly Color TrayText = Color.FromArgb(0xF5, 0xF2, 0xED);
    private static readonly Color TrayFaint = Color.FromArgb(0x6B, 0x65, 0x5E);

    private sealed class TrayColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => TrayInk;
        public override Color MenuItemSelected => TrayInkHot;
        public override Color MenuItemSelectedGradientBegin => TrayInkHot;
        public override Color MenuItemSelectedGradientEnd => TrayInkHot;
        public override Color MenuItemBorder => TrayInkHot;
        public override Color MenuBorder => TrayLine;
        public override Color MenuItemPressedGradientBegin => TrayInkHot;
        public override Color MenuItemPressedGradientMiddle => TrayInkHot;
        public override Color MenuItemPressedGradientEnd => TrayInkHot;
        public override Color ImageMarginGradientBegin => TrayInk;
        public override Color ImageMarginGradientMiddle => TrayInk;
        public override Color ImageMarginGradientEnd => TrayInk;
        public override Color SeparatorDark => TrayLine;
        public override Color SeparatorLight => TrayLine;
    }

    private sealed class TrayRenderer : ToolStripProfessionalRenderer
    {
        public TrayRenderer() : base(new TrayColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TrayText : TrayFaint;
            base.OnRenderItemText(e);
        }
    }

    private static void BuildTray()
    {
        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new TrayRenderer(),
            BackColor = TrayInk,
            ForeColor = TrayText,
            ShowImageMargin = false,
            DropShadowEnabled = true,
            Font = new Font("Segoe UI", 9f)
        };

        var openItem = new ToolStripMenuItem("Open ClipWatch")
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold)
        };
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());

        var statusItem = new ToolStripMenuItem("Status") { Enabled = false };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var saveItem = new ToolStripMenuItem("Save replay now");
        saveItem.Click += (_, _) => _ = _controller!.SaveReplayAsync();
        menu.Items.Add(saveItem);

        var playbackItem = new ToolStripMenuItem("Instant replay (Shift+F9)");
        playbackItem.Click += (_, _) => _ = _controller!.RequestPlaybackAsync();
        menu.Items.Add(playbackItem);

        var toggleItem = new ToolStripMenuItem("Toggle replay buffer");
        toggleItem.Click += (_, _) => _ = _controller!.ToggleManuallyAsync();
        menu.Items.Add(toggleItem);

        var resetItem = new ToolStripMenuItem("Reset detection");
        resetItem.Click += (_, _) => _controller!.ResetDetection();
        menu.Items.Add(resetItem);

        menu.Items.Add(new ToolStripSeparator());

        var configItem = new ToolStripMenuItem("Open config folder");
        configItem.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Config.Directory) { UseShellExecute = true }); }
            catch { }
        };
        menu.Items.Add(configItem);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += async (_, _) =>
        {
            exitItem.Enabled = false;
            await _controller!.ShutdownAsync();

            if (_window != null)
            {
                _window.AllowClose = true;
                _window.Close();
            }

            _tray!.Visible = false;
            _tray.Dispose();
            _controller.Dispose();
            Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        menu.Opening += (_, _) => statusItem.Text = _controller?.StatusLine ?? "";

        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "ClipWatch",
            ContextMenuStrip = menu
        };

        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static void ShowPlayback(string tempPath)
    {
        if (_controller == null) return;

        _playback?.Close();

        _playback = new PlaybackWindow(_controller);
        _playback.Closed += (_, _) => _playback = null;
        _playback.OnEditRequested = path =>
        {
            ShowMainWindow();
            _window?.OpenClipByPath(path);
        };

        _playback.Load(tempPath);
        _playback.Show();
        _playback.Activate();
    }

    private static void ShowMainWindow()
    {
        if (_controller == null || _config == null) return;

        _window ??= new MainWindow(_controller, _config);
        _window.Reveal();
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(Program).Assembly
                .GetManifestResourceStream("ClipWatch.clipwatch.ico");
            if (stream != null)
                return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch
        {
        }
        return SystemIcons.Application;
    }

    private static void UpdateTrayText()
    {
        if (_tray == null || _controller == null) return;
        var text = "ClipWatch - " + _controller.StatusLine;

        _tray.Text = text.Length > 62 ? text[..62] : text;
    }
}
