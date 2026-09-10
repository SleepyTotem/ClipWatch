using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using System.Windows.Threading;

namespace ClipWatch;

public partial class PlaybackWindow : Window
{
    private readonly Controller _controller;
    private readonly DispatcherTimer _tick;
    private bool _closing;
    private bool _mediaReady;

    private string _tempPath = "";
    private TimeSpan _duration;
    private bool _playing;
    private bool _kept;
    private bool _resumeAfterScrub;

    public string? KeptPath { get; private set; }

    public Action<string>? OnEditRequested { get; set; }
    public Task DiscardCompletion { get; private set; } = Task.CompletedTask;

    public PlaybackWindow(Controller controller)
    {
        _controller = controller;
        InitializeComponent();

        Seek.ScrubStarted += OnScrubStarted;
        Seek.Scrubbed += OnScrubbed;
        Seek.ScrubEnded += OnScrubEnded;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _tick.Tick += (_, _) => SyncPlayhead();

        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) =>
        {
            var top = Math.Max(0, SeekBar.StripCentre - PlayButton.Height / 2);
            PlayButton.Margin = new Thickness(0, top, 14, 0);
        };
    }

    public void Load(string tempPath)
    {
        ReleasePlayer();
        Seek.SetFilmstrip(Array.Empty<ImageSource>());
        _duration = TimeSpan.Zero;
        Seek.Duration = 1;
        UpdateTimeLabel(TimeSpan.Zero);
        _tempPath = tempPath;
        _kept = false;
        KeptPath = null;
        StatusText.Text = "";
        KeepButton.IsEnabled = true;
        TempNotice.Text = "Click outside or press Esc to discard";

        if (!File.Exists(tempPath) || !Ffmpeg.IsPlayable(tempPath))
        {
            Player.Visibility = Visibility.Collapsed;
            PlayerNotice.Visibility = Visibility.Visible;
            PlayerNotice.Text = !File.Exists(tempPath) ? "This replay file has been moved or deleted." :
                $"Windows can't preview {Path.GetExtension(tempPath)} files. Record to MP4 in OBS " +
                "for instant playback, or press Keep and convert it in the editor.";
            return;
        }

        Player.Visibility = Visibility.Visible;
        PlayerNotice.Visibility = Visibility.Collapsed;
        Player.Source = new Uri(tempPath);
        Player.Position = TimeSpan.Zero;
        Player.Play();
        _playing = true;
        PlayButton.Content = "\uE769";
        _tick.Start();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        _duration = Player.NaturalDuration.HasTimeSpan
            ? Player.NaturalDuration.TimeSpan
            : TimeSpan.FromSeconds(ObsBootstrap.RequiredBufferSeconds);

        Seek.Duration = _duration.TotalSeconds;
        UpdateTimeLabel(TimeSpan.Zero);

        FitToVideo();

        _ = Seek.LoadFilmstripAsync(_tempPath);
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        _playing = false;
        PlayButton.Content = "\uE768";
        Seek.Position = _duration.TotalSeconds;
        UpdateTimeLabel(_duration);
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ReleasePlayer();
        Player.Visibility = Visibility.Collapsed;
        PlayerNotice.Visibility = Visibility.Visible;
        PlayerNotice.Text = "Couldn't play this file: " + e.ErrorException?.Message;
    }

    private void Player_Click(object sender, MouseButtonEventArgs e) => TogglePlay();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (!_mediaReady || Seek.IsScrubbing) return;

        if (_playing)
        {
            Player.Pause();
            _playing = false;
            PlayButton.Content = "\uE768";
        }
        else
        {
            if (Player.Position >= _duration - TimeSpan.FromMilliseconds(120))
                Player.Position = TimeSpan.Zero;

            Player.Play();
            _playing = true;
            PlayButton.Content = "\uE769";
        }
    }

    private void OnScrubStarted()
    {
        if (Player.Source == null) return;

        _resumeAfterScrub = _playing;

        if (_playing)
        {
            Player.Pause();
            _playing = false;
            PlayButton.Content = "\uE768";
        }

        _tick.Stop();
    }

    private void OnScrubbed(double seconds)
    {
        UpdateTimeLabel(TimeSpan.FromSeconds(seconds));
    }

    private void OnScrubEnded(double seconds)
    {
        if (!_mediaReady) return;

        Player.Position = TimeSpan.FromSeconds(seconds);
        UpdateTimeLabel(TimeSpan.FromSeconds(seconds));

        if (_resumeAfterScrub)
        {
            _resumeAfterScrub = false;
            Player.Play();
            _playing = true;
            PlayButton.Content = "\uE769";
            _tick.Start();
        }
    }

    private void SyncPlayhead()
    {
        if (Player.Source == null || !_playing || Seek.IsScrubbing) return;
        Seek.Position = Player.Position.TotalSeconds;
        UpdateTimeLabel(Player.Position);
    }

    private void UpdateTimeLabel(TimeSpan position) =>
        TimeLabel.Text = $"{Format(position)} / {Format(_duration)}";

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Keep_Click(object sender, RoutedEventArgs e) => Keep();

    private string? Keep()
    {
        if (_kept) return KeptPath;

        var folder = _controller.Library.Folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusText.Text = "No clips folder — set one in Settings.";
            return null;
        }

        try
        {
            var name = "Replay " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            var target = ClipOps.UniquePath(folder, name, Path.GetExtension(_tempPath));

            ReleasePlayer();
            File.Move(_tempPath, target);

            _kept = true;
            KeptPath = target;
            KeepButton.IsEnabled = false;
            TempNotice.Text = "Saved to your clips folder.";
            StatusText.Text = Path.GetFileName(target);

            _ = _controller.Library.RefreshAsync();
            return target;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't save: " + ex.Message;
            return null;
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e) => Close();

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var path = Keep();
        if (path == null) return;

        Close();
        OnEditRequested?.Invoke(path);
    }

    private void ReleasePlayer()
    {
        _tick.Stop();
        Seek.CancelFilmstrip();
        _mediaReady = false;
        _resumeAfterScrub = false;
        _playing = false;
        PlayButton.Content = "\uE768";
        Player.Stop();
        Player.Close();
        Player.Source = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        ReleasePlayer();

        if (!_kept && !string.IsNullOrEmpty(_tempPath))
        {
            var path = _tempPath;
            DiscardCompletion = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                        return;
                    }
                    catch { await Task.Delay(250); }
                }
            });
        }

        base.OnClosed(e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                if (!e.IsRepeat) TogglePlay();
                e.Handled = true;
                break;

            case Key.Escape:
                e.Handled = true;
                Close();
                break;
        }
    }

    private const double MaxScreenShare = 0.92;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Resize(MaxWidthAllowed, MaxHeightAllowed);
        RoundCorners();
    }

    private static double MaxWidthAllowed => SystemParameters.WorkArea.Width * MaxScreenShare;
    private static double MaxHeightAllowed => SystemParameters.WorkArea.Height * MaxScreenShare;

    private void FitToVideo()
    {
        double videoW = Player.NaturalVideoWidth;
        double videoH = Player.NaturalVideoHeight;
        if (videoW <= 0 || videoH <= 0) return;

        Deck.UpdateLayout();
        var deck = Deck.ActualHeight > 0 ? Deck.ActualHeight : Deck.DesiredSize.Height;

        var edges = BorderThickness.Left + BorderThickness.Right;
        var chrome = deck + BorderThickness.Top + BorderThickness.Bottom;

        var roomW = MaxWidthAllowed - edges;
        var roomH = MaxHeightAllowed - chrome;
        if (roomW <= 0 || roomH <= 0) return;

        var scale = Math.Min(roomW / videoW, roomH / videoH);
        Resize(videoW * scale + edges, videoH * scale + chrome);
    }

    private void Resize(double width, double height)
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Round(width);
        Height = Math.Round(height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible && !_closing) Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }
}
