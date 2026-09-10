using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipWatch;

public static class Motion
{
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly IEasingFunction Hard = new QuarticEase { EasingMode = EasingMode.EaseOut };

    private static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    public static void RiseIn(UIElement element, double from = 12, double delayMs = 0)
    {
        var slide = new TranslateTransform(0, from);
        element.RenderTransform = slide;
        element.Opacity = 0;

        var begin = TimeSpan.FromMilliseconds(delayMs);

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = Ms(200),
            BeginTime = begin,
            EasingFunction = Out
        });

        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = Ms(260),
            BeginTime = begin,
            EasingFunction = Hard
        });
    }

    public static void ToColor(SolidColorBrush brush, Color to, double ms = 220)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = to,
            Duration = Ms(ms),
            EasingFunction = Out
        });
    }

    public static void Fade(UIElement element, double to, double ms = 180)
    {
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            To = to,
            Duration = Ms(ms),
            EasingFunction = Out
        });
    }
}
