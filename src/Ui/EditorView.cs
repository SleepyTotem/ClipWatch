using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Clipboard = System.Windows.Clipboard;

namespace ClipWatch;

public partial class EditorView : UserControl
{
    private readonly Controller _controller;
    private readonly DispatcherTimer _tick;

    private Clip? _clip;
    private TimeSpan _duration;
    private bool _playing;
    private bool _busy;
    private bool _resumeAfterScrub;
    private bool _mediaReady;

    private bool _loading;

    private MediaInfo _info = MediaInfo.Empty;
    private readonly List<TrackRow> _trackRows = new();
    private bool _previewMuted;

    public Action? OnClosed { get; set; }

    private Config Config => _controller?.Config ?? (_fallbackConfig ??= new Config());
    private Config? _fallbackConfig;

    public EditorView(Controller controller)
    {
        _controller = controller;
        InitializeComponent();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _tick.Tick += (_, _) => SyncPlayhead();

        Timeline.Scrubbed += OnScrubbed;
        Timeline.ScrubStarted += () =>
        {
            _resumeAfterScrub = _playing;
            if (_mediaReady) PausePlayback();
            _tick.Stop();
        };
        Timeline.ScrubEnded += seconds =>
        {
            if (!_mediaReady) return;
            Player.Position = TimeSpan.FromSeconds(seconds);
            UpdateTimeLabel(TimeSpan.FromSeconds(seconds));
            if (_resumeAfterScrub) StartPlayback();
            _resumeAfterScrub = false;
            _tick.Start();
        };
        Timeline.TrimChanged += () =>
        {
            UpdateTrimLabels();
            UpdateEstimate();
        };

        Loaded += (_, _) =>
        {
            var top = Math.Max(0, SeekBar.StripCentre - PlayButton.Height / 2);
            PlayButton.Margin = new Thickness(0, top, 14, 0);
        };

        ApplyConfigToControls();
    }

    public void Load(Clip clip)
    {
        Stop();
        Timeline.SetFilmstrip(Array.Empty<ImageSource>());

        _clip = clip;
        _busy = false;
        _info = MediaInfo.Empty;

        HeaderName.Text = clip.DisplayName;
        NameBox.Text = clip.DisplayName;
        StatusText.Text = "";
        SaveButton.IsEnabled = true;
        ConvertButton.Visibility = clip.IsPlayable ? Visibility.Collapsed : Visibility.Visible;

        BuildTrackRows(MediaInfo.Empty);
        ApplyPreviewVolume();
        ApplyDuration(clip.Duration ?? TimeSpan.FromSeconds(1));

        if (!File.Exists(clip.Path))
        {
            Player.Visibility = Visibility.Collapsed;
            ClickCatcher.Visibility = Visibility.Collapsed;
            PlayerNotice.Visibility = Visibility.Visible;
            PlayerNotice.Text = "This clip has been moved or deleted. Return to Clips to refresh the library.";
            SaveButton.IsEnabled = false;
            ConvertButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (clip.IsPlayable)
        {
            PlayerNotice.Visibility = Visibility.Collapsed;
            Player.Visibility = Visibility.Visible;
            ClickCatcher.Visibility = Visibility.Visible;
            Player.Source = new Uri(clip.Path);
            Player.Position = TimeSpan.Zero;
            Player.Play();
            Player.Pause();
            _tick.Start();
        }
        else
        {
            Player.Visibility = Visibility.Collapsed;
            ClickCatcher.Visibility = Visibility.Collapsed;
            PlayerNotice.Visibility = Visibility.Visible;
            PlayerNotice.Text =
                $"Preview isn't available for {clip.Extension} files — Windows has no built-in " +
                "decoder for this container. Trimming and renaming still work, or convert it to MP4.";
        }

        if (!Ffmpeg.Available)
            StatusText.Text = "Install ffmpeg to trim. Renaming is available.";
        else if (!clip.IsPlayable) _ = Timeline.LoadFilmstripAsync(clip.Path);

        _ = LoadMediaInfoAsync(clip);
    }

    private async Task LoadMediaInfoAsync(Clip clip)
    {
        var info = await MediaProbe.ReadAsync(clip.Path);
        if (!ReferenceEquals(_clip, clip)) return;

        _info = info;
        BuildTrackRows(info);
        UpdateEstimate();
    }

    private void ApplyDuration(TimeSpan duration)
    {
        _duration = duration;

        Timeline.Duration = duration.TotalSeconds;

        OriginalLengthLabel.Text = SeekBar.FormatSpan(duration.TotalSeconds);
        UpdateTrimLabels();
        UpdateTimeLabel(TimeSpan.Zero);
        UpdateEstimate();
    }

    private sealed class TrackRow
    {
        public required AudioTrack Track { get; init; }
        public required Slider Volume { get; init; }
        public required Button Mute { get; init; }
        public required TextBlock Readout { get; init; }
        public bool Muted { get; set; }
    }

    private void BuildTrackRows(MediaInfo info)
    {
        _trackRows.Clear();
        AudioTracks.Items.Clear();

        if (info.AudioTracks.Count == 0)
        {
            AudioTrackCount.Text = "";
            AudioNotice.Text = Ffmpeg.Available
                ? "No audio tracks found in this clip."
                : "Install ffmpeg to read this clip's audio tracks.";
            AudioNotice.Visibility = Visibility.Visible;
            return;
        }

        AudioTrackCount.Text = info.AudioTracks.Count == 1
            ? "1 TRACK"
            : $"{info.AudioTracks.Count} TRACKS";

        AudioNotice.Visibility = info.HasLayers ? Visibility.Collapsed : Visibility.Visible;
        AudioNotice.Text = info.HasLayers
            ? ""
            : "Only one track. Turn on audio layers in Settings to record apps separately.";

        foreach (var track in info.AudioTracks)
            AudioTracks.Items.Add(BuildTrackRow(track));
    }

    private UIElement BuildTrackRow(AudioTrack track)
    {
        var name = new TextBlock
        {
            Text = track.DisplayName(Config),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var readout = new TextBlock
        {
            Width = 34,
            TextAlignment = TextAlignment.Right,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        readout.SetResourceReference(TextBlock.ForegroundProperty, "Faint");

        var mute = new Button
        {
            Content = "",
            Width = 26,
            Height = 26,
            FontSize = 13,
            ToolTip = "Mute this track",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        mute.SetResourceReference(StyleProperty, "IconControl");

        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 1,
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new TrackRow { Track = track, Volume = volume, Mute = mute, Readout = readout };
        _trackRows.Add(row);

        mute.Click += (_, _) =>
        {
            row.Muted = !row.Muted;
            RefreshTrackRow(row);
            UpdateEstimate();
        };

        volume.ValueChanged += (_, _) =>
        {
            RefreshTrackRow(row);
            UpdateEstimate();
        };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(name);
        Grid.SetColumn(readout, 1);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(readout);

        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(mute, 0);
        Grid.SetColumn(volume, 1);
        controls.Children.Add(mute);
        controls.Children.Add(volume);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(header);
        stack.Children.Add(controls);

        RefreshTrackRow(row);
        return stack;
    }

    private void RefreshTrackRow(TrackRow row)
    {
        var percent = (int)Math.Round(row.Volume.Value * 100);

        row.Readout.Text = row.Muted ? "muted" : percent + "%";
        row.Readout.SetResourceReference(TextBlock.ForegroundProperty, row.Muted ? "Danger" : "Faint");

        row.Mute.Content = row.Muted ? "" : "";
        row.Mute.ToolTip = row.Muted ? "Unmute this track" : "Mute this track";
        row.Volume.Opacity = row.Muted ? 0.4 : 1;
    }

    private IReadOnlyList<AudioMix> CurrentMix() =>
        _trackRows.Select(r => new AudioMix(r.Track.Index, r.Volume.Value, r.Muted)).ToList();

    private void PreviewVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        if (_previewMuted && e.NewValue > 0) _previewMuted = false;

        ApplyPreviewVolume();

        Config.PreviewVolume = PreviewVolume.Value;
        Config.Save();
    }

    private void PreviewMute_Click(object sender, RoutedEventArgs e)
    {
        _previewMuted = !_previewMuted;
        ApplyPreviewVolume();
    }

    private void ApplyPreviewVolume()
    {
        var volume = _previewMuted ? 0 : PreviewVolume.Value;

        Player.Volume = volume;
        Player.IsMuted = _previewMuted;

        PreviewMuteButton.Content = _previewMuted ? "" : "";
        PreviewMuteButton.ToolTip = _previewMuted ? "Unmute the preview" : "Mute the preview";
        PreviewVolumeLabel.Text = _previewMuted ? "muted" : (int)Math.Round(PreviewVolume.Value * 100) + "%";
    }

    private void ApplyConfigToControls()
    {
        _loading = true;
        var config = Config;

        PreviewVolume.Value = Math.Clamp(config.PreviewVolume, 0, 1);

        ModeFast.IsChecked = config.ExportMode == ExportMode.Fast;
        ModePrecise.IsChecked = config.ExportMode == ExportMode.Precise;

        FormatKeep.IsChecked = config.ExportContainer == ExportContainer.KeepOriginal;
        FormatMp4.IsChecked = config.ExportContainer == ExportContainer.Mp4;
        FormatMkv.IsChecked = config.ExportContainer == ExportContainer.Mkv;

        ResOriginal.IsChecked = config.ExportHeight is null;
        Res1440.IsChecked = config.ExportHeight == 1440;
        Res1080.IsChecked = config.ExportHeight == 1080;
        Res720.IsChecked = config.ExportHeight == 720;

        QualityHigh.IsChecked = config.ExportQuality == ExportQuality.High;
        QualityBalanced.IsChecked = config.ExportQuality == ExportQuality.Balanced;
        QualitySmall.IsChecked = config.ExportQuality == ExportQuality.Small;

        DestNew.IsChecked = config.ExportDestination == ExportDestination.NewFile;
        DestReplace.IsChecked = config.ExportDestination == ExportDestination.ReplaceOriginal;
        DestFolder.IsChecked = config.ExportDestination == ExportDestination.ChosenFolder;

        AfterNothing.IsChecked = config.ExportAfter == ExportAfter.Nothing;
        AfterOpen.IsChecked = config.ExportAfter == ExportAfter.OpenFolder;
        AfterCopy.IsChecked = config.ExportAfter == ExportAfter.CopyPath;

        _loading = false;

        ApplyPreviewVolume();
        RefreshExportChrome();
    }

    private void ExportOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;

        var config = Config;
        config.ExportMode = ModePrecise.IsChecked == true ? ExportMode.Precise : ExportMode.Fast;

        config.ExportContainer =
            FormatMp4.IsChecked == true ? ExportContainer.Mp4 :
            FormatMkv.IsChecked == true ? ExportContainer.Mkv :
            ExportContainer.KeepOriginal;

        config.ExportHeight =
            Res1440.IsChecked == true ? 1440 :
            Res1080.IsChecked == true ? 1080 :
            Res720.IsChecked == true ? 720 :
            null;

        config.ExportQuality =
            QualityBalanced.IsChecked == true ? ExportQuality.Balanced :
            QualitySmall.IsChecked == true ? ExportQuality.Small :
            ExportQuality.High;

        config.ExportAfter =
            AfterOpen.IsChecked == true ? ExportAfter.OpenFolder :
            AfterCopy.IsChecked == true ? ExportAfter.CopyPath :
            ExportAfter.Nothing;

        if (DestFolder.IsChecked == true && sender == DestFolder && !ChooseExportFolder())
        {
            DestNew.IsChecked = true;
            return;
        }

        config.ExportDestination =
            DestReplace.IsChecked == true ? ExportDestination.ReplaceOriginal :
            DestFolder.IsChecked == true ? ExportDestination.ChosenFolder :
            ExportDestination.NewFile;

        config.Save();
        RefreshExportChrome();
    }

    private bool ChooseExportFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Where should exported clips go?",
            UseDescriptionForTitle = true,
            SelectedPath = Config.ExportFolder ?? _controller?.Library.Folder ?? ""
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

        Config.ExportFolder = dialog.SelectedPath;
        return true;
    }

    private void RefreshExportChrome()
    {
        var scaling = Config.ExportHeight is not null;
        var reencoding = Config.ExportMode == ExportMode.Precise || scaling;

        QualityBar.IsEnabled = reencoding;
        QualityLabel.Opacity = reencoding ? 1 : 0.5;
        QualityHint.Text = reencoding
            ? "Re-encoding this export."
            : "Fast mode copies the streams, so quality and resolution are unchanged.";

        DestFolderLabel.Visibility = Config.ExportDestination == ExportDestination.ChosenFolder
            ? Visibility.Visible
            : Visibility.Collapsed;
        DestFolderLabel.Text = Config.ExportFolder ?? "";

        SaveButton.Content = Config.ExportDestination == ExportDestination.ReplaceOriginal
            ? "Export, replacing original"
            : "Export clip";

        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        if (!IsInitialized || _clip is null) return;

        var request = BuildRequest(_clip, "estimate" + TargetExtension());
        var bytes = Exporter.EstimateBytes(request, _info);

        SizeEstimate.Text = bytes > 0 ? "~" + Exporter.FormatSize(bytes) : "—";
    }

    private string TargetExtension() =>
        Exporter.ExtensionFor(Config.ExportContainer, _clip?.Extension ?? ".mp4");

    private ExportRequest BuildRequest(Clip clip, string destination) => new()
    {
        Source = clip.Path,
        Destination = destination,
        Start = TimeSpan.FromSeconds(Timeline.InPoint),
        End = TimeSpan.FromSeconds(Timeline.OutPoint),
        Mode = Config.ExportMode,
        Quality = Config.ExportQuality,
        TargetHeight = EffectiveHeight(),
        Audio = CurrentMix()
    };

    private int? EffectiveHeight()
    {
        var wanted = Config.ExportHeight;
        if (wanted is null) return null;
        if (_info.Height > 0 && wanted >= _info.Height) return null;
        return wanted;
    }

    private void OnScrubbed(double seconds)
    {
        if (_clip is null || !_clip.IsPlayable) return;

        UpdateTimeLabel(TimeSpan.FromSeconds(seconds));
    }

    private void UpdateTrimLabels()
    {
        InLabel.Text = SeekBar.FormatSpan(Timeline.InPoint);
        OutLabel.Text = SeekBar.FormatSpan(Timeline.OutPoint);
        TrimmedLengthLabel.Text = SeekBar.FormatSpan(Timeline.SelectionLength);
    }

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        Timeline.InPoint = 0;
        Timeline.OutPoint = _duration.TotalSeconds;
        UpdateTrimLabels();
        UpdateEstimate();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        ApplyPreviewVolume();

        if (!Player.NaturalDuration.HasTimeSpan) return;

        var actual = Player.NaturalDuration.TimeSpan;
        if (_clip is { Duration: null }) _clip.Duration = actual;

        if (Math.Abs(actual.TotalSeconds - _duration.TotalSeconds) > 0.25)
            ApplyDuration(actual);
        if (_clip != null) _ = Timeline.LoadFilmstripAsync(_clip.Path);
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => PausePlayback();

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Stop();
        Player.Visibility = Visibility.Collapsed;
        ClickCatcher.Visibility = Visibility.Collapsed;
        PlayerNotice.Visibility = Visibility.Visible;
        PlayerNotice.Text = "Windows couldn't decode this file for preview. " +
                            "Trimming and renaming still work.";
    }

    private void Video_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        PlayPause_Click(this, new RoutedEventArgs());

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady || Timeline.IsScrubbing) return;
        if (_playing) PausePlayback(); else StartPlayback();
    }

    public void TogglePlayback() => PlayPause_Click(this, new RoutedEventArgs());

    private void StartPlayback()
    {
        var pos = Player.Position.TotalSeconds;
        if (pos < Timeline.InPoint || pos >= Timeline.OutPoint - 0.05)
            Player.Position = TimeSpan.FromSeconds(Timeline.InPoint);

        Player.Play();
        _playing = true;
        PlayButton.Content = "";
        _tick.Start();
    }

    private void PausePlayback()
    {
        Player.Pause();
        _playing = false;
        PlayButton.Content = "";
    }

    public void Stop()
    {
        Timeline.CancelFilmstrip();
        _tick.Stop();
        _playing = false;
        _mediaReady = false;
        _resumeAfterScrub = false;

        try
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
        }
        catch { }

        if (IsLoaded) PlayButton.Content = "";
    }

    private void SyncPlayhead()
    {
        if (!_mediaReady || Timeline.IsScrubbing) return;

        var pos = Player.Position;

        if (_playing && pos.TotalSeconds >= Timeline.OutPoint)
        {
            PausePlayback();
            Player.Position = TimeSpan.FromSeconds(Timeline.OutPoint);
            pos = Player.Position;
        }

        Timeline.Position = pos.TotalSeconds;
        UpdateTimeLabel(pos);
    }

    private void UpdateTimeLabel(TimeSpan pos) =>
        TimeLabel.Text = $"{SeekBar.FormatSpan(pos.TotalSeconds)} / " +
                         $"{SeekBar.FormatSpan(_duration.TotalSeconds)}";

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || _busy) return;

        var clip = _clip;
        var config = Config;
        var replaceOriginal = config.ExportDestination == ExportDestination.ReplaceOriginal;

        var start = TimeSpan.FromSeconds(Timeline.InPoint);
        var end = TimeSpan.FromSeconds(Timeline.OutPoint);

        var trimmed = start > TimeSpan.FromSeconds(0.05) ||
                      end < _duration - TimeSpan.FromSeconds(0.05);

        var newName = ClipOps.Sanitize(NameBox.Text);
        var renamed = newName != clip.DisplayName;

        var extension = TargetExtension();
        var reformatted = !string.Equals(extension, clip.Extension, StringComparison.OrdinalIgnoreCase);
        var remixed = _trackRows.Any(r => r.Muted || Math.Abs(r.Volume.Value - 1) > 0.001);
        var rescaled = EffectiveHeight() is not null;
        var relocated = config.ExportDestination == ExportDestination.ChosenFolder;

        var needsEncode = trimmed || reformatted || remixed || rescaled;

        if (!needsEncode && !renamed && !relocated)
        {
            StatusText.Text = "Nothing to export — no trim, mix, format or name change.";
            return;
        }

        if (!needsEncode && !relocated)
        {
            Stop();
            var renameError = ClipOps.Rename(clip, newName);
            if (renameError != null) { Load(clip); StatusText.Text = renameError; return; }

            await RefreshLibraryAsync();
            OnClosed?.Invoke();
            return;
        }

        if (!Ffmpeg.Available)
        {
            StatusText.Text = "ffmpeg isn't installed yet.";
            return;
        }

        SetBusy(true, config.ExportMode == ExportMode.Precise || rescaled
            ? "Re-encoding — this takes a moment..."
            : "Exporting...");

        var sourcePath = clip.Path;
        Stop();

        var outputFolder = config.ExportDestination == ExportDestination.ChosenFolder &&
                           !string.IsNullOrWhiteSpace(config.ExportFolder) &&
                           Directory.Exists(config.ExportFolder)
            ? config.ExportFolder!
            : Path.GetDirectoryName(sourcePath)!;

        var temp = Path.Combine(outputFolder, $".clipwatch-{Guid.NewGuid():N}{extension}");

        var result = await Exporter.RunAsync(BuildRequest(clip, temp));

        if (!result.Ok)
        {
            TryDelete(temp);
            if (ReferenceEquals(_clip, clip))
            {
                Load(clip);
                SetBusy(false, result.Message);
            }
            return;
        }

        string finalPath;
        try
        {
            if (replaceOriginal)
            {
                var target = Path.Combine(outputFolder, newName + extension);
                ClipLibrary.InvalidateThumbnail(sourcePath);
                MediaProbe.Invalidate(sourcePath);

                if (string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase))
                {
                    File.Replace(temp, sourcePath, null);
                    finalPath = sourcePath;
                }
                else
                {
                    File.Move(temp, target);
                    File.Delete(sourcePath);
                    finalPath = target;
                }
            }
            else
            {
                finalPath = ClipOps.UniquePath(outputFolder, newName, extension);
                File.Move(temp, finalPath);
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_clip, clip))
                SetBusy(false, $"Export succeeded but writing the file failed: {ex.Message}");
            TryDelete(temp);
            return;
        }

        await RefreshLibraryAsync();
        RunAfterExport(finalPath);

        if (ReferenceEquals(_clip, clip))
        {
            SetBusy(false, "Exported.");
            if (IsVisible) OnClosed?.Invoke();
        }
    }

    private void RunAfterExport(string path)
    {
        try
        {
            switch (Config.ExportAfter)
            {
                case ExportAfter.OpenFolder:
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                    break;

                case ExportAfter.CopyPath:
                    Clipboard.SetText(path);
                    break;
            }
        }
        catch { }
    }

    private Task RefreshLibraryAsync() =>
        _controller?.Library.RefreshAsync() ?? Task.CompletedTask;

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || _busy || !Ffmpeg.Available) return;

        SetBusy(true, "Converting to MP4...");

        var source = _clip.Path;
        var target = ClipOps.UniquePath(
            Path.GetDirectoryName(source)!,
            Path.GetFileNameWithoutExtension(source),
            ".mp4");

        var (ok, message) = await Ffmpeg.RemuxToMp4Async(source, target);
        if (!ok) { SetBusy(false, message); return; }

        await RefreshLibraryAsync();
        SetBusy(false, "Converted. The MP4 is in your clips list.");
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy && _clip != null && File.Exists(_clip.Path);
        ConvertButton.IsEnabled = !busy;
        StatusText.Text = message;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Stop();
        OnClosed?.Invoke();
    }
}
