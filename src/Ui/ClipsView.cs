using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ClipWatch;

public sealed class ThumbnailConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public partial class ClipsView : UserControl
{
    private readonly Controller _controller;
    private ICollectionView? _view;

    public Action<Clip>? OnEditRequested { get; set; }

    public ClipsView(Controller controller)
    {
        _controller = controller;
        InitializeComponent();

        ClipItems.ItemsSource = _controller.Library.Clips;
        _view = CollectionViewSource.GetDefaultView(_controller.Library.Clips);
        _view.Filter = FilterClip;

        _controller.Library.Changed += UpdateChrome;
        _controller.FfmpegProgress += OnFfmpegProgress;
        UpdateChrome();
    }

    public async Task RefreshAsync()
    {
        UpdateChrome();
        await _controller.Library.RefreshAsync();
    }

    private bool FilterClip(object obj)
    {
        if (obj is not Clip clip) return false;
        var q = SearchBox?.Text?.Trim();
        return string.IsNullOrEmpty(q) ||
               clip.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateChrome()
    {
        Dispatcher.InvokeAsync(() =>
        {
            FfmpegWarning.Visibility = Ffmpeg.Available ? Visibility.Collapsed : Visibility.Visible;
            InstallFfmpegButton.IsEnabled = !_controller.FfmpegInstalling;

            var folder = _controller.Library.Folder;
            FolderLabel.Text = string.IsNullOrWhiteSpace(folder)
                ? "Waiting for OBS to report its recording folder..."
                : folder;

            var count = _controller.Library.Clips.Count;
            ClipCount.Text = count.ToString();
            CountChip.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

            EmptyLabel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private static Clip? ClipFrom(object sender) => sender switch
    {
        Button b => b.Tag as Clip,
        MenuItem m => m.DataContext as Clip,
        _ => null
    };

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is { } clip) OnEditRequested?.Invoke(clip);
    }

    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is { } clip) OnEditRequested?.Invoke(clip);
    }

    private void MenuPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is not { } clip) return;
        TryStart(clip.Path);
    }

    private void MenuReveal_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is not { } clip) return;
        try { Process.Start("explorer.exe", $"/select,\"{clip.Path}\""); }
        catch { }
    }

    private async void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is not { } clip) return;

        var name = PromptDialog.Ask(Window.GetWindow(this), "Rename clip", clip.DisplayName);
        if (string.IsNullOrWhiteSpace(name) || name == clip.DisplayName) return;

        var error = ClipOps.Rename(clip, name);
        if (error != null)
        {
            Dialogs.Notify(Window.GetWindow(this), "Rename failed", error);
            return;
        }

        await _controller.Library.RefreshAsync();
    }

    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ClipFrom(sender) is not { } clip) return;

        var confirm = Dialogs.Confirm(Window.GetWindow(this), "Delete clip",
            $"Move \"{clip.DisplayName}\" to the Recycle Bin?", "Delete", danger: true);
        if (!confirm) return;

        var error = ClipOps.Delete(clip);
        if (error != null)
        {
            Dialogs.Notify(Window.GetWindow(this), "Delete failed", error);
            return;
        }

        await _controller.Library.RefreshAsync();
    }

    private void OnFfmpegProgress(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            FfmpegProgressText.Visibility = Visibility.Visible;
            FfmpegProgressText.Text = line;
            InstallFfmpegButton.IsEnabled = !_controller.FfmpegInstalling;

            if (Ffmpeg.Available) UpdateChrome();
        });
    }

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        InstallFfmpegButton.IsEnabled = false;
        FfmpegProgressText.Visibility = Visibility.Visible;
        FfmpegProgressText.Text = "Starting...";

        await _controller.InstallFfmpegAsync();

        InstallFfmpegButton.IsEnabled = true;
        UpdateChrome();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _controller.Library.Folder;
        if (!string.IsNullOrWhiteSpace(folder)) TryStart(folder);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _view?.Refresh();
    }

    private static void TryStart(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }
}
