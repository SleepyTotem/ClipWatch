using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using UserControl = System.Windows.Controls.UserControl;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using Brush = System.Windows.Media.Brush;

namespace ClipWatch;

public partial class SettingsView : UserControl
{
    private readonly Controller _controller;
    private readonly Config _config;
    private readonly DispatcherTimer _foregroundTimer;

    private bool _suspend;

    public SettingsView(Controller controller, Config config)
    {
        _controller = controller;
        _config = config;

        InitializeComponent();

        Bind(ToastsCheck,         v => _config.ShowToasts = v);
        Bind(SoundsCheck,         v => _config.PlaySounds = v, Beeper.Reset);
        Bind(AutoInstallCheck,    v => _config.AutoInstallFfmpeg = v);
        Bind(AutoConfigureCheck,  v => _config.AutoConfigureObs = v);
        Bind(AutoLaunchCheck,     v => _config.AutoLaunchObs = v);
        Bind(LearnHotkeyCheck,    v => _config.EnableLearnHotkey = v, NoteRestart);
        Bind(PlaybackHotkeyCheck, v => _config.EnablePlaybackHotkey = v, NoteRestart);
        Bind(OwnSaveHotkeyCheck,  v => _config.UseOwnSaveHotkey = v, NoteRestart);
        Bind(AlwaysClipCheck, v => _config.AlwaysClip = v, () =>
        {
            _ = _controller.RefreshSyncAsync();
            RefreshForeground();
        });

        Bind(AudioLayersCheck, v => _config.EnableAudioLayers = v, OnAudioLayersToggled);

        VolumeSlider.ValueChanged += (_, _) =>
        {
            VolumeLabel.Text = VolumeSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
            if (_suspend) return;
            _config.SoundVolume = VolumeSlider.Value;
            _config.Save();
        };

        _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _foregroundTimer.Tick += (_, _) => RefreshForeground();
        Loaded += (_, _) => _foregroundTimer.Start();
        Unloaded += (_, _) => _foregroundTimer.Stop();

        _controller.StatusChanged += RefreshStatus;
        _controller.FfmpegProgress += line => Dispatcher.InvokeAsync(() =>
        {
            FfmpegProgressText.Text = line;
            InstallFfmpegButton.IsEnabled = !_controller.FfmpegInstalling;
        });
    }

    private void Bind(CheckBox box, Action<bool> apply, Action? after = null)
    {
        void Handler(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            apply(box.IsChecked == true);
            _config.Save();
            after?.Invoke();
            SaveNotice.Text = "Saved.";
        }

        box.Checked += Handler;
        box.Unchecked += Handler;
    }

    private void NoteRestart() => SaveNotice.Text = "Saved - restart ClipWatch to apply.";

    public void Reload()
    {
        _suspend = true;
        try
        {
        HostBox.Text = _config.ObsHost;
        PortBox.Text = _config.ObsPort.ToString();
        PasswordBox.Text = _config.ObsPassword;

        PollBox.Text = _config.PollIntervalMs.ToString();
        StopDelayBox.Text = _config.StopDelaySeconds.ToString();
        ClipsFolderBox.Text = _config.ClipsFolder ?? "";
        FfmpegBox.Text = _config.FfmpegFolder ?? "";
        AutoInstallCheck.IsChecked = _config.AutoInstallFfmpeg;

        AudioLayersCheck.IsChecked = _config.EnableAudioLayers;
        RefreshAudioLayers();
        FfmpegProgressText.Text = Ffmpeg.Available
            ? $"Using {Ffmpeg.FfmpegPath}"
            : "Not installed.";
        InstallFfmpegButton.IsEnabled = !_controller.FfmpegInstalling;

        ToastsCheck.IsChecked = _config.ShowToasts;
        ToastDurationBox.Text = _config.ToastDurationMs.ToString();
        SoundsCheck.IsChecked = _config.PlaySounds;
        VolumeSlider.Value = _config.SoundVolume;
        VolumeLabel.Text = _config.SoundVolume.ToString("0.00", CultureInfo.InvariantCulture);

        LearnHotkeyCheck.IsChecked = _config.EnableLearnHotkey;
        LearnKeyBox.Text = "0x" + _config.LearnHotkeyVirtualKey.ToString("X2");
        LearnModBox.Text = _config.LearnHotkeyModifiers.ToString();
        OwnSaveHotkeyCheck.IsChecked = _config.UseOwnSaveHotkey;
        SaveKeyBox.Text = "0x" + _config.SaveHotkeyVirtualKey.ToString("X2");

        PlaybackHotkeyCheck.IsChecked = _config.EnablePlaybackHotkey;
        PlaybackKeyBox.Text = "0x" + _config.PlaybackHotkeyVirtualKey.ToString("X2");

        AutoConfigureCheck.IsChecked = _config.AutoConfigureObs;
        AutoLaunchCheck.IsChecked = _config.AutoLaunchObs;
        ObsSetupText.Text = _controller.ObsSetup.Detail ?? "";

        AlwaysClipCheck.IsChecked = _config.AlwaysClip;
        }
        finally
        {
            _suspend = false;
        }

        RefreshGames();
        RefreshStatus();
        RefreshForeground();
    }

    private void RefreshForeground()
    {
        var process = _controller.ForegroundProcess;

        if (string.IsNullOrWhiteSpace(process))
        {
            ForegroundLabel.Text = "—";
            ForegroundVerdict.Text = "";
            return;
        }

        ForegroundLabel.Text = process;

        var listed = _controller.Games.Contains(process);
        ForegroundVerdict.Text = _config.AlwaysClip
            ? "Always clipping is on — the buffer runs regardless."
            : listed
                ? "In your games list — this one triggers clipping."
                : "Not listed — ignored.";

        ForegroundVerdict.Foreground = listed || _config.AlwaysClip
            ? (Brush)FindResource("Live")
            : (Brush)FindResource("Faint");
    }

    private void RefreshGames()
    {
        GamesList.ItemsSource = _controller.Games.All().OrderBy(n => n).ToList();
    }

    private void RefreshStatus()
    {
        Dispatcher.InvokeAsync(() =>
        {
            StatusLeft.Children.Clear();
            StatusRight.Children.Clear();

            AddStatus(StatusLeft, "OBS", _controller.IsObsConnected ? "Connected" : "Not connected",
                      _controller.IsObsConnected);
            AddStatus(StatusLeft, "Replay buffer", _controller.BufferActive ? "Running" : "Stopped",
                      _controller.BufferActive);
            AddStatus(StatusLeft, "Locked game", _controller.ActiveGame ?? "None",
                      _controller.ActiveGame != null);

            AddStatus(StatusRight, "ffmpeg", Ffmpeg.Available ? "Found" : "Not found", Ffmpeg.Available);
            AddStatus(StatusRight, "Clips folder",
                      string.IsNullOrWhiteSpace(_controller.Library.Folder) ? "Unknown" : _controller.Library.Folder!,
                      !string.IsNullOrWhiteSpace(_controller.Library.Folder));
            AddStatus(StatusRight, "Clips found", _controller.Library.Clips.Count.ToString(), true);
        });
    }

    private void AddStatus(Panel panel, string label, string value, bool good)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 20, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)FindResource("Faint")
        };
        Grid.SetColumn(name, 0);

        var val = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = good ? (Brush)FindResource("Live") : (Brush)FindResource("Muted")
        };
        Grid.SetColumn(val, 1);

        row.Children.Add(name);
        row.Children.Add(val);
        panel.Children.Add(row);
    }

    private void RefreshAudioLayers()
    {
        AudioLayersPanel.IsEnabled = _config.EnableAudioLayers;
        AudioLayersPanel.Opacity = _config.EnableAudioLayers ? 1 : 0.45;

        AudioLayersList.ItemsSource = _config.AudioLayers
            .OrderBy(l => l.Track)
            .Select(l => $"Track {l.Track}  ·  {l.Label}  ({l.Process}.exe)")
            .ToList();

        if (!_config.EnableAudioLayers)
            AudioLayersStatus.Text = "";
        else if (_config.AudioLayers.Count == 0)
            AudioLayersStatus.Text = "Add the apps you want on their own track. Track 1 stays the combined mix.";
    }

    private void OnAudioLayersToggled()
    {
        RefreshAudioLayers();

        if (_config.EnableAudioLayers)
        {
            NoteObsSetup();
            _ = _controller.ApplyAudioLayersAsync();
        }
        else
        {
            var restored = ObsBootstrap.RestoreOutputMode(_config);
            AudioLayersStatus.Text = restored
                ? "Audio layers off. OBS's output mode has been restored."
                : ObsBootstrap.ObsRunning
                    ? "Audio layers off. Close OBS and restart ClipWatch to restore its output mode."
                    : "Audio layers off.";
        }
    }

    private void NoteObsSetup()
    {
        var result = ObsBootstrap.Ensure(_config);

        AudioLayersStatus.Text = result.State switch
        {
            ObsSetupState.Ready => "OBS is set up for multi-track recording.",
            ObsSetupState.NeedsRestart => result.Detail + " Restart OBS to apply.",
            ObsSetupState.ObsRunningLocked => "Close OBS so ClipWatch can switch it to multi-track recording.",
            _ => result.Detail
        };
    }

    private void AddAudioLayer_Click(object sender, RoutedEventArgs e)
    {
        var track = AudioLayerSetup.NextFreeTrack(_config);
        if (track == 0)
        {
            AudioLayersStatus.Text = $"OBS only has {AudioLayerSetup.MaxTracks} audio tracks, and they're all in use.";
            return;
        }

        var process = PromptDialog.Ask(Window.GetWindow(this),
            "Process name (without .exe)", "");
        if (string.IsNullOrWhiteSpace(process)) return;

        process = process.Trim();
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process[..^4];

        if (_config.AudioLayers.Any(l => string.Equals(l.Process, process, StringComparison.OrdinalIgnoreCase)))
        {
            AudioLayersStatus.Text = $"{process} already has its own track.";
            return;
        }

        var label = PromptDialog.Ask(Window.GetWindow(this),
            "Name for this track", process);
        if (label == null) return;

        _config.AudioLayers.Add(new AudioLayer
        {
            Process = process,
            Label = string.IsNullOrWhiteSpace(label) ? process : label.Trim(),
            Track = track
        });
        _config.Save();

        RefreshAudioLayers();
        NoteObsSetup();
        _ = _controller.ApplyAudioLayersAsync();
    }

    private void RemoveAudioLayer_Click(object sender, RoutedEventArgs e)
    {
        if (AudioLayersList.SelectedIndex < 0) return;

        var ordered = _config.AudioLayers.OrderBy(l => l.Track).ToList();
        if (AudioLayersList.SelectedIndex >= ordered.Count) return;

        var layer = ordered[AudioLayersList.SelectedIndex];

        if (!Dialogs.Confirm(Window.GetWindow(this), "Remove audio layer",
                $"Stop recording {layer.Label} on its own track? The OBS source ClipWatch created for it is removed too.",
                "Remove", danger: true))
            return;

        _config.AudioLayers.Remove(layer);
        _config.Save();

        _ = AudioLayerSetup.RemoveAsync(_controller.Obs, new[] { layer });

        RefreshAudioLayers();
        AudioLayersStatus.Text = $"Removed {layer.Label}.";
    }

    private async void ReapplyAudioLayers_Click(object sender, RoutedEventArgs e)
    {
        if (!_config.EnableAudioLayers) return;

        NoteObsSetup();

        if (!_controller.IsObsConnected)
        {
            AudioLayersStatus.Text = "OBS isn't connected yet.";
            return;
        }

        var detail = await AudioLayerSetup.ApplyAsync(_controller.Obs, _config);
        if (!string.IsNullOrEmpty(detail)) AudioLayersStatus.Text = detail;
    }

    private void ResetDetection_Click(object sender, RoutedEventArgs e) => _controller.ResetDetection();

    private async void ToggleBuffer_Click(object sender, RoutedEventArgs e) =>
        await _controller.ToggleManuallyAsync();

    private async void SaveNow_Click(object sender, RoutedEventArgs e) =>
        await _controller.SaveReplayAsync();

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(Window.GetWindow(this),
            "Process name (without .exe)", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        _controller.Games.Toggle(name.Trim());
        RefreshGames();
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (GamesList.SelectedItem is not string name) return;

        _controller.Games.Toggle(name);
        if (string.Equals(_controller.ActiveGame, name, StringComparison.OrdinalIgnoreCase))
            _controller.ResetDetection();

        RefreshGames();
    }

    private void ClearGames_Click(object sender, RoutedEventArgs e)
    {
        var names = _controller.Games.All().ToList();
        if (names.Count == 0) return;

        var confirm = Dialogs.Confirm(Window.GetWindow(this), "Clear list",
            $"Remove all {names.Count} entries from the games list?", "Clear", danger: true);
        if (!confirm) return;

        foreach (var n in names) _controller.Games.Toggle(n);
        _controller.ResetDetection();
        RefreshGames();
        RefreshForeground();
    }

    private void OpenGamesFile_Click(object sender, RoutedEventArgs e) => TryStart(GameList.FilePath);

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e) => TryStart(Config.Directory);

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        InstallFfmpegButton.IsEnabled = false;
        FfmpegProgressText.Text = "Starting...";

        await _controller.InstallFfmpegAsync();

        InstallFfmpegButton.IsEnabled = true;
        FfmpegBox.Text = _config.FfmpegFolder ?? "";
        RefreshStatus();
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        _config.SoundVolume = VolumeSlider.Value;
        _config.PlaySounds = SoundsCheck.IsChecked == true;
        Beeper.Reset();
        Beeper.Play(ToastSound.Save, _config);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _config.ObsHost = HostBox.Text.Trim();
        if (int.TryParse(PortBox.Text, out var port)) _config.ObsPort = port;
        _config.ObsPassword = PasswordBox.Text;

        if (int.TryParse(PollBox.Text, out var poll)) _config.PollIntervalMs = poll;
        if (int.TryParse(StopDelayBox.Text, out var stop)) _config.StopDelaySeconds = stop;

        _config.ClipsFolder = Blank(ClipsFolderBox.Text);
        _config.FfmpegFolder = Blank(FfmpegBox.Text);
        _config.AutoInstallFfmpeg = AutoInstallCheck.IsChecked == true;

        _config.ShowToasts = ToastsCheck.IsChecked == true;
        if (int.TryParse(ToastDurationBox.Text, out var toast)) _config.ToastDurationMs = toast;
        _config.PlaySounds = SoundsCheck.IsChecked == true;
        _config.SoundVolume = VolumeSlider.Value;

        _config.EnableLearnHotkey = LearnHotkeyCheck.IsChecked == true;
        if (TryParseKey(LearnKeyBox.Text, out var learnKey)) _config.LearnHotkeyVirtualKey = learnKey;
        if (uint.TryParse(LearnModBox.Text, out var mods)) _config.LearnHotkeyModifiers = mods;
        _config.UseOwnSaveHotkey = OwnSaveHotkeyCheck.IsChecked == true;
        if (TryParseKey(SaveKeyBox.Text, out var saveKey)) _config.SaveHotkeyVirtualKey = saveKey;

        _config.AlwaysClip = AlwaysClipCheck.IsChecked == true;
        _config.AutoConfigureObs = AutoConfigureCheck.IsChecked == true;
        _config.AutoLaunchObs = AutoLaunchCheck.IsChecked == true;
        _config.EnablePlaybackHotkey = PlaybackHotkeyCheck.IsChecked == true;
        if (TryParseKey(PlaybackKeyBox.Text, out var playbackKey))
            _config.PlaybackHotkeyVirtualKey = playbackKey;

        _config.Save();

        Beeper.Reset();
        Ffmpeg.Locate(_config);
        _controller.ApplyClipsFolder();

        SaveNotice.Text = "Saved.";
        RefreshStatus();
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool TryParseKey(string text, out int value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(text, out value);
    }

    private static void TryStart(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }
}
