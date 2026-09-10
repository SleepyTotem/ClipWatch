using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Pen = System.Windows.Media.Pen;

namespace ClipWatch;

public sealed class SeekBar : FrameworkElement
{
    private const double RulerHeight = 20;
    private const double StripHeight = 42;
    private double Inset => IsTrimEnabled ? 6 : 1;

    public static double StripCentre => RulerHeight + StripHeight / 2;

    private static readonly Brush StripFill = Frozen(0xFF, 0x10, 0x0F, 0x0E);
    private static readonly Brush AccentFill = Frozen(0xFF, 0xFF, 0x8A, 0x3D);
    private static readonly Brush TickFill = Frozen(0xFF, 0x3A, 0x35, 0x31);
    private static readonly Brush LabelFill = Frozen(0xFF, 0x6B, 0x65, 0x5E);
    private static readonly Brush StripEdge = Frozen(0xFF, 0x2E, 0x2A, 0x27);
    private static readonly Brush KnobInk = Frozen(0xFF, 0x1A, 0x0E, 0x06);

    private static readonly Pen TickPen = FrozenPen(TickFill, 1);
    private static readonly Pen StripBorder = FrozenPen(StripEdge, 1);

    private double _duration = 1;
    private double _position;
    private enum Grab { None, Playhead, In, Out }
    private Grab _dragging;
    private double _inPoint;
    private double _outPoint = 1;
    private CancellationTokenSource? _filmstripCancellation;
    private ImageSource[] _filmstrip = Array.Empty<ImageSource>();

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = double.IsFinite(value) ? Math.Max(0.1, value) : 1;
            _position = 0;
            _inPoint = 0;
            _outPoint = _duration;
            InvalidateVisual();
        }
    }

    public double Position
    {
        get => _position;
        set
        {
            if (IsScrubbing) return;
            _position = Math.Clamp(value, 0, _duration);
            InvalidateVisual();
        }
    }

    public bool IsScrubbing => _dragging != Grab.None;

    public bool IsTrimEnabled { get; set; }
    public double InPoint
    {
        get => _inPoint;
        set { _inPoint = Math.Clamp(value, 0, Math.Max(0, _outPoint - 0.1)); InvalidateVisual(); TrimChanged?.Invoke(); }
    }
    public double OutPoint
    {
        get => _outPoint;
        set { _outPoint = Math.Clamp(value, Math.Min(_duration, _inPoint + 0.1), _duration); InvalidateVisual(); TrimChanged?.Invoke(); }
    }
    public double SelectionLength => _outPoint - _inPoint;
    public event Action? TrimChanged;

    public event Action<double>? Scrubbed;

    public event Action? ScrubStarted;

    public event Action<double>? ScrubEnded;

    public SeekBar()
    {
        Height = RulerHeight + StripHeight;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        Focusable = false;
        Unloaded += (_, _) => CancelFilmstrip();
    }

    public void SetFilmstrip(IEnumerable<ImageSource> frames)
    {
        _filmstrip = frames.ToArray();
        InvalidateVisual();
    }

    public void CancelFilmstrip()
    {
        _filmstripCancellation?.Cancel();
        _filmstripCancellation?.Dispose();
        _filmstripCancellation = null;
    }

    public async Task LoadFilmstripAsync(string file)
    {
        CancelFilmstrip();

        var cached = Filmstrip.Cached(file);
        SetFilmstrip(cached);
        if (cached.Length > 0) return;

        if (!Ffmpeg.Available || !File.Exists(file)) return;

        _filmstripCancellation = new CancellationTokenSource();
        var token = _filmstripCancellation.Token;

        var frames = await Filmstrip.GetAsync(file, Duration, token);
        if (!token.IsCancellationRequested) SetFilmstrip(frames);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = HitTest(e.GetPosition(this));
        CaptureMouse();
        ScrubStarted?.Invoke();
        Apply(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsScrubbing) Apply(e.GetPosition(this).X);
        else Cursor = HitTest(e.GetPosition(this)) is Grab.In or Grab.Out ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsScrubbing) return;
        _dragging = Grab.None;
        ReleaseMouseCapture();
        ScrubEnded?.Invoke(_position);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (!IsScrubbing) return;
        _dragging = Grab.None;
        ScrubEnded?.Invoke(_position);
    }

    private void Apply(double x)
    {
        var seconds = Math.Clamp((x - Inset) / TrackWidth * _duration, 0, _duration);
        if (_dragging == Grab.In)
        {
            InPoint = seconds;
            _position = Math.Max(_position, InPoint);
        }
        else if (_dragging == Grab.Out)
        {
            OutPoint = seconds;
            _position = Math.Min(_position, OutPoint);
        }
        else _position = seconds;
        Scrubbed?.Invoke(_position);
        InvalidateVisual();
    }

    private Grab HitTest(Point point)
    {
        if (!IsTrimEnabled || point.Y < RulerHeight) return Grab.Playhead;
        var left = Math.Abs(point.X - XFor(InPoint));
        var right = Math.Abs(point.X - XFor(OutPoint));
        if (left <= 9 && left <= right) return Grab.In;
        return right <= 9 ? Grab.Out : Grab.Playhead;
    }

    private double TrackWidth => Math.Max(1, ActualWidth - Inset * 2);

    private double XFor(double seconds) => Inset + seconds / _duration * TrackWidth;

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        DrawRuler(dc);
        DrawFilmstrip(dc);
        if (IsTrimEnabled) DrawTrim(dc);
        DrawPlayhead(dc);
    }

    private void DrawTrim(DrawingContext dc)
    {
        var left = XFor(InPoint);
        var right = XFor(OutPoint);
        var shade = Frozen(0xC4, 0x08, 0x07, 0x06);
        dc.DrawRectangle(shade, null, new Rect(Inset, RulerHeight, Math.Max(0, left - Inset), StripHeight));
        dc.DrawRectangle(shade, null, new Rect(right, RulerHeight, Math.Max(0, ActualWidth - Inset - right), StripHeight));
        dc.DrawRectangle(null, FrozenPen(AccentFill, 1), new Rect(left, RulerHeight, Math.Max(0, right - left), StripHeight));
        foreach (var x in new[] { left, right })
        {
            dc.DrawRoundedRectangle(AccentFill, null, new Rect(x - 4, RulerHeight, 8, StripHeight), 3, 3);
            dc.DrawLine(FrozenPen(KnobInk, 1), new Point(x, RulerHeight + 15), new Point(x, RulerHeight + 27));
        }
    }

    private void DrawRuler(DrawingContext dc)
    {
        var step = ChooseStep(_duration, TrackWidth);
        var baseline = RulerHeight - 1;

        for (var t = 0.0; t <= _duration + 0.001; t += step)
        {
            var x = XFor(t);
            var text = Render(FormatTick(t), 10, LabelFill);

            var tx = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            dc.DrawText(text, new Point(tx, 0));

            dc.DrawLine(TickPen, new Point(x + 0.5, baseline - 5), new Point(x + 0.5, baseline));

            for (var m = 1; m < 5; m++)
            {
                var mx = XFor(t + step * m / 5.0);
                if (mx > ActualWidth - Inset) break;
                dc.DrawLine(TickPen, new Point(mx + 0.5, baseline - 2), new Point(mx + 0.5, baseline));
            }
        }
    }

    private void DrawFilmstrip(DrawingContext dc)
    {
        var area = new Rect(Inset, RulerHeight, TrackWidth, StripHeight);

        dc.PushClip(new RectangleGeometry(area, 3, 3));
        dc.DrawRectangle(StripFill, null, area);

        if (_filmstrip.Length > 0)
        {
            var cell = area.Width / _filmstrip.Length;
            for (var i = 0; i < _filmstrip.Length; i++)
                dc.DrawImage(_filmstrip[i],
                    new Rect(area.X + i * cell, area.Y, cell + 1, StripHeight));
        }

        dc.Pop();

        dc.DrawRoundedRectangle(null, StripBorder, area, 3, 3);
    }

    private void DrawPlayhead(DrawingContext dc)
    {
        var x = XFor(_position);

        dc.DrawRectangle(AccentFill, null,
            new Rect(x - 1, RulerHeight - 7, 2, StripHeight + 7));

        dc.DrawRoundedRectangle(AccentFill, null,
            new Rect(x - 4, RulerHeight - 11, 8, 7), 2, 2);
    }

    private static double ChooseStep(double duration, double width)
    {
        var targetLabels = Math.Max(2, width / 90.0);
        var raw = duration / targetLabels;

        foreach (var candidate in new double[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 })
            if (candidate >= raw) return candidate;

        return 3600;
    }

    private static string FormatTick(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string FormatSpan(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    private FormattedText Render(string text, double size, Brush brush) =>
        new(text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
