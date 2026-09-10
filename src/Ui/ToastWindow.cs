using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Image = System.Windows.Controls.Image;

namespace ClipWatch;

public enum ToastKind { Info, Success, Warning, Error }

public sealed class ToastWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x8000000;

    private const double TopOffset = 56;

    private const double SlideOvershoot = 12;

    private readonly Config _config;
    private readonly Image _logo;
    private readonly Border _accent;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly DispatcherTimer _hideTimer;

    private readonly TranslateTransform _slide = new(-9999, 0);

    private bool _visible;
    private double _restingLeft;
    private static ImageSource? _logoSource;

    public ToastWindow(Config config)
    {
        _config = config;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;

        _logo = new Image
        {
            Width = 38,
            Height = 38,
            Margin = new Thickness(0, 0, 15, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = LoadLogo(),
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_logo, BitmapScalingMode.HighQuality);

        _title = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF2, 0xED)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _subtitle = new TextBlock
        {
            FontSize = 13,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0x9F, 0x95)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
            Visibility = Visibility.Collapsed
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(_title);
        textStack.Children.Add(_subtitle);

        _accent = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(20, 0, 0, 0),
            Background = Brushes.Transparent
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_logo, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(_accent, 2);
        grid.Children.Add(_logo);
        grid.Children.Add(textStack);
        grid.Children.Add(_accent);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x14, 0x13, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x2A, 0x27)),

            BorderThickness = new Thickness(0, 1, 1, 1),

            CornerRadius = new CornerRadius(0, 16, 16, 0),
            Padding = new Thickness(18, 15, 18, 15),

            Margin = new Thickness(0, 22, 26, 22),
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Direction = 0,
                Opacity = 0.55,
                Color = Colors.Black
            },
            Child = grid,

            RenderTransform = _slide
        };

        Content = card;

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => Hide();

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    public static ImageSource? LoadLogo()
    {
        if (_logoSource != null) return _logoSource;

        try
        {
            using var stream = typeof(ToastWindow).Assembly
                .GetManifestResourceStream("ClipWatch.clipwatch.ico");
            if (stream == null) return null;

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var frame = decoder.Frames
                .Where(f => f.PixelWidth <= 128)
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault() ?? decoder.Frames[0];

            frame.Freeze();
            _logoSource = frame;
        }
        catch
        {
        }

        return _logoSource;
    }

    public void Show(string title, string? subtitle, ToastKind kind, ToastSound sound = ToastSound.None)
    {
        Beeper.Play(sound, _config);

        if (!_config.ShowToasts) return;

        _title.Text = title;
        _subtitle.Text = subtitle ?? "";
        _subtitle.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _accent.Background = new SolidColorBrush(kind switch
        {
            ToastKind.Success => Color.FromRgb(0x56, 0xD6, 0x8A),
            ToastKind.Warning => Color.FromRgb(0xE7, 0xB8, 0x4E),
            ToastKind.Error   => Color.FromRgb(0xF0, 0x55, 0x5B),
            _                 => Color.FromRgb(0xFF, 0x8A, 0x3D)
        });

        var wasVisible = _visible;

        if (!_visible)
        {
            _visible = true;
            base.Show();
        }

        UpdateLayout();
        ResolvePosition();

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(800, _config.ToastDurationMs));
        _hideTimer.Start();

        if (wasVisible) return;

        SlideIn();
    }

    private void SlideIn()
    {
        var start = OffscreenX();

        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.X = start;

        var slide = new DoubleAnimation(start, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        _slide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private double OffscreenX()
    {
        var width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        return -(width + SlideOvershoot);
    }

    public new void Hide()
    {
        _hideTimer.Stop();
        if (!_visible) return;

        var slide = new DoubleAnimation(_slide.X, OffscreenX(), TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        slide.Completed += (_, _) =>
        {
            _visible = false;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = -9999;
            base.Hide();
        };

        _slide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void ResolvePosition()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            var screen = hwnd != IntPtr.Zero
                ? System.Windows.Forms.Screen.FromHandle(hwnd)
                : System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var area = screen.WorkingArea;

            _restingLeft = area.Left / dpi.DpiScaleX;
            Left = _restingLeft;
            Top = area.Top / dpi.DpiScaleY + TopOffset;
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

    private static int GetWindowLong(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? (int)GetWindowLongPtr(hWnd, index).ToInt64() : GetWindowLong32(hWnd, index);

    private static void SetWindowLong(IntPtr hWnd, int index, int value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(hWnd, index, new IntPtr(value));
        else SetWindowLong32(hWnd, index, value);
    }
}
