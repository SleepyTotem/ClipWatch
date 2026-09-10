using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;

namespace ClipWatch;

internal sealed class ModalShell : Window
{
    private readonly StackPanel _body;
    private readonly StackPanel _buttons;

    internal ModalShell(string title, double width = 440)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontSize = 13;

        _body = new StackPanel();
        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        };

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(heading);
        stack.Children.Add(_body);
        stack.Children.Add(_buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(22),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 30,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = 0.55,
                Color = Colors.Black
            }
        };

        Content = card;

        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        Loaded += (_, _) =>
        {
            SetResourceReference(FontFamilyProperty, "UiFont");
            SetResourceReference(ForegroundProperty, "Text");
            card.SetResourceReference(BackgroundProperty, "Ink1");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            Motion.RiseIn(card, from: 14);
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    internal void Add(UIElement element) => _body.Children.Add(element);

    internal Button AddButton(string text, bool primary, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Height = 36,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Margin = new Thickness(_buttons.Children.Count == 0 ? 0 : 10, 0, 0, 0),

            Focusable = true,
            IsTabStop = true
        };

        if (primary) button.SetResourceReference(StyleProperty, "AccentButton");
        _buttons.Children.Add(button);
        return button;
    }

    internal bool? Run(Window? owner)
    {
        if (owner != null) Owner = owner;

        foreach (var d in Application.Current.Resources.MergedDictionaries)
            Resources.MergedDictionaries.Add(d);

        return ShowDialog();
    }
}

public static class Dialogs
{
    public static bool Confirm(Window? owner, string title, string message,
                               string confirmText = "OK", bool danger = false)
    {
        var dialog = new ModalShell(title);
        dialog.Add(Body(message));

        var cancel = dialog.AddButton("Cancel", primary: false, isCancel: true);
        var ok = dialog.AddButton(confirmText, primary: !danger, isDefault: true);
        if (danger) ok.SetResourceReference(FrameworkElement.StyleProperty, "DangerButton");

        cancel.Click += (_, _) => dialog.DialogResult = false;
        ok.Click += (_, _) => dialog.DialogResult = true;

        return dialog.Run(owner) == true;
    }

    public static void Notify(Window? owner, string title, string message)
    {
        var dialog = new ModalShell(title);
        dialog.Add(Body(message));

        var ok = dialog.AddButton("Close", primary: true, isDefault: true, isCancel: true);
        ok.Click += (_, _) => dialog.DialogResult = true;

        dialog.Run(owner);
    }

    private static TextBlock Body(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 19
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return text;
    }
}

public sealed class PromptDialog
{
    public static string? Ask(Window? owner, string title, string initial)
    {
        var dialog = new ModalShell(title);

        var input = new TextBox { Text = initial, FontSize = 14, Padding = new Thickness(11, 9, 11, 9) };
        dialog.Add(input);

        var cancel = dialog.AddButton("Cancel", primary: false, isCancel: true);
        var ok = dialog.AddButton("Save", primary: true, isDefault: true);

        cancel.Click += (_, _) => dialog.DialogResult = false;
        ok.Click += (_, _) => dialog.DialogResult = true;

        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

        return dialog.Run(owner) == true ? input.Text : null;
    }
}
