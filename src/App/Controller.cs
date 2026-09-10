using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ClipWatch;

public sealed class Controller : IDisposable
{
    private readonly Config _config;
    private readonly ObsClient _obs;
    private readonly GameWatcher _watcher;
    private readonly GameList _games;
    private readonly ClipLibrary _library;
    private readonly ToastWindow _toast;
    private readonly HotkeyManager _hotkeys;
    private readonly Dispatcher _dispatcher;

    private bool _bufferActive;
    private bool _syncing;
    private bool _announcedDisconnect;
    private string? _obsRecordDirectory;
    private bool _awaitingPlayback;
    private DateTime _playbackRequestedAt;

    public event Action<string>? TempReplayReady;

    public ObsSetupResult ObsSetup { get; private set; }

    public string StatusLine { get; private set; } = "Starting...";
    public event Action? StatusChanged;

    public bool IsObsConnected => _obs.IsConnected;

    public Config Config => _config;

    public ObsClient Obs => _obs;
    public bool BufferActive => _bufferActive;
    public string? ActiveGame => _watcher.GameActive ? _watcher.ActiveProcessName : null;
    public GameList Games => _games;
    public string? ForegroundProcess => _watcher.LastForegroundProcess;
    public ClipLibrary Library => _library;

    public bool FfmpegInstalling { get; private set; }

    public event Action<string>? FfmpegProgress;

    public Controller(Config config)
    {
        _config = config;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _toast = new ToastWindow(config);
        _obs = new ObsClient(config);
        _games = new GameList();
        _library = new ClipLibrary();
        _watcher = new GameWatcher(config, _games);

        _obs.ConnectionChanged += OnConnectionChanged;
        _obs.ObsEvent += OnObsEvent;

        _obs.Log += line => _dispatcher.InvokeAsync(() => SetStatus(line));

        _watcher.StateChanged += OnGameStateChanged;

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkey;
    }

    public void Start()
    {
        ClearTempFolder();
        RunObsBootstrap();

        _toast.Show("ClipWatch running", "Waiting for a game...", ToastKind.Info);

        if (_config.UseOwnSaveHotkey &&
            !_hotkeys.Register(HotkeyManager.SaveHotkeyId, _config.SaveHotkeyVirtualKey))
        {
            _toast.Show("Save hotkey unavailable",
                "Another app owns that key. Bind it in OBS instead.", ToastKind.Warning);
        }

        if (_config.EnableLearnHotkey &&
            !_hotkeys.Register(HotkeyManager.LearnHotkeyId,
                               _config.LearnHotkeyVirtualKey,
                               _config.LearnHotkeyModifiers))
        {
            _toast.Show("Learn hotkey unavailable",
                "Another app owns Shift+F7. Change it in config.json.", ToastKind.Warning);
        }

        if (_config.EnablePlaybackHotkey &&
            !_hotkeys.Register(HotkeyManager.PlaybackHotkeyId,
                               _config.PlaybackHotkeyVirtualKey,
                               _config.PlaybackHotkeyModifiers))
        {
            _toast.Show("Playback hotkey unavailable",
                "Another app owns Shift+F9. Change it in Settings.", ToastKind.Warning);
        }

        Ffmpeg.Locate(_config);
        ApplyClipsFolder();

        if (!Ffmpeg.Available && _config.AutoInstallFfmpeg)
            _ = InstallFfmpegAsync(announce: true);

        _obs.Start();
        _watcher.Start();
        SetStatus("Waiting for OBS...");
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyManager.SaveHotkeyId:
                _ = SaveReplayAsync();
                break;
            case HotkeyManager.LearnHotkeyId:
                LearnForegroundWindow();
                break;
            case HotkeyManager.PlaybackHotkeyId:
                _ = RequestPlaybackAsync();
                break;
        }
    }

    private void LearnForegroundWindow()
    {
        var process = GameWatcher.GetForegroundProcessName();

        if (string.IsNullOrWhiteSpace(process))
        {
            _toast.Show("Nothing to add", "Couldn't identify the focused window.", ToastKind.Error, ToastSound.Error);
            return;
        }

        var added = _games.Toggle(process);

        if (added)
        {
            _toast.Show("Added to game list", process, ToastKind.Success, ToastSound.Start);

            _watcher.PollNow();
        }
        else
        {
            _toast.Show("Removed from game list", process, ToastKind.Warning);

            if (string.Equals(_watcher.ActiveProcessName, process, StringComparison.OrdinalIgnoreCase))
                ResetDetection();
        }

        SetStatus($"{(added ? "Added" : "Removed")}: {process}");
    }

    private void OnConnectionChanged(bool connected)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            if (connected)
            {
                _announcedDisconnect = false;
                SetStatus("Connected to OBS.");

                var status = await _obs.RequestAsync("GetReplayBufferStatus");
                if (status.TryGetBool("outputActive", out var active))
                    _bufferActive = active;

                await DiscoverClipsFolderAsync();
                await ApplyAudioLayersAsync();

                _watcher.PollNow();
                await SyncAsync();
            }
            else
            {
                _bufferActive = false;
                SetStatus("OBS not connected. Retrying...");

                if (!_announcedDisconnect)
                {
                    _announcedDisconnect = true;
                    _toast.Show("OBS disconnected", "Clipping is paused until OBS is back.", ToastKind.Warning);
                }
            }
        });
    }

    private void OnGameStateChanged(bool active, string? processName)
    {
        _dispatcher.InvokeAsync(async () =>
        {
            SetStatus(active ? $"Game detected: {processName}" : "No game running.");
            if (!active && !_config.AlwaysClip)
                _toast.Show("Clipping disabled", processName == null ? null : $"{processName} closed", ToastKind.Info);

            await SyncAsync();
        });
    }

    private async Task SyncAsync()
    {
        if (_syncing) return;
        _syncing = true;

        try
        {
            if (!_obs.IsConnected) return;

            var wantRunning = _config.AlwaysClip || _watcher.GameActive;

            var status = await _obs.RequestAsync("GetReplayBufferStatus");
            if (status.TryGetBool("outputActive", out var actual))
                _bufferActive = actual;

            if (wantRunning && !_bufferActive)
            {
                var result = await _obs.RequestAsync("StartReplayBuffer");
                if (!result.Ok)
                {
                    _toast.Show(
                        "Couldn't start clipping",
                        DescribeStartFailure(result),
                        ToastKind.Error, ToastSound.Error);
                    SetStatus($"StartReplayBuffer failed: {result.Comment}");
                }
            }
            else if (!wantRunning && _bufferActive)
            {
                await _obs.RequestAsync("StopReplayBuffer");
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private static string DescribeStartFailure(ObsResponse result)
    {
        if (result.Comment is { Length: > 0 } comment) return comment;
        return "Enable the replay buffer in OBS -> Settings -> Output.";
    }

    private void OnObsEvent(string eventType, JsonElement data)
    {
        _dispatcher.InvokeAsync(() =>
        {
            switch (eventType)
            {
                case "ReplayBufferStateChanged":
                {
                    var state = data.ValueKind == JsonValueKind.Object &&
                                data.TryGetProperty("outputState", out var s)
                        ? s.GetString()
                        : null;

                    var active = data.ValueKind == JsonValueKind.Object &&
                                 data.TryGetProperty("outputActive", out var a) &&
                                 a.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                 a.GetBoolean();

                    if (state is "OBS_WEBSOCKET_OUTPUT_STARTED")
                    {
                        _bufferActive = true;
                        SetStatus("Replay buffer running.");
                        _toast.Show("Clipping enabled",
                            _watcher.ActiveProcessName is { } p ? $"Buffering {p}" : "Replay buffer running",
                            ToastKind.Success, ToastSound.Start);
                    }
                    else if (state is "OBS_WEBSOCKET_OUTPUT_STOPPED")
                    {
                        _bufferActive = false;
                        SetStatus("Replay buffer stopped.");
                    }
                    else
                    {
                        _bufferActive = active;
                    }
                    break;
                }

                case "ReplayBufferSaved":
                {
                    var path = data.ValueKind == JsonValueKind.Object &&
                               data.TryGetProperty("savedReplayPath", out var p)
                        ? p.GetString()
                        : null;

                    var forPlayback = _awaitingPlayback &&
                                      DateTime.UtcNow - _playbackRequestedAt < TimeSpan.FromSeconds(10);
                    _awaitingPlayback = false;

                    if (forPlayback && !string.IsNullOrEmpty(path))
                    {
                        _ = HandOffToPlaybackAsync(path!);
                        break;
                    }

                    var name = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
                    _toast.Show("Replay saved", name, ToastKind.Success, ToastSound.Save);
                    SetStatus($"Saved: {path}");

                    _ = _library.RefreshAsync();
                    break;
                }

                case "ExitStarted":
                    _bufferActive = false;
                    SetStatus("OBS is shutting down.");
                    break;
            }
        });
    }

    public async Task SaveReplayAsync()
    {
        if (!_obs.IsConnected)
        {
            _toast.Show("Not saved", "OBS isn't connected.", ToastKind.Error, ToastSound.Error);
            return;
        }

        if (!_bufferActive)
        {
            _toast.Show("Not saved", "The replay buffer isn't running.", ToastKind.Warning, ToastSound.Error);
            return;
        }

        var result = await _obs.RequestAsync("SaveReplayBuffer");
        if (!result.Ok)
            _toast.Show("Not saved", result.Comment ?? "OBS rejected the request.", ToastKind.Error, ToastSound.Error);
    }

    public async Task RequestPlaybackAsync()
    {
        if (!_obs.IsConnected)
        {
            _toast.Show("No replay", "OBS isn't connected.", ToastKind.Error, ToastSound.Error);
            return;
        }

        if (!_bufferActive)
        {
            _toast.Show("No replay", "The replay buffer isn't running.", ToastKind.Warning, ToastSound.Error);
            return;
        }

        _awaitingPlayback = true;
        _playbackRequestedAt = DateTime.UtcNow;

        var result = await _obs.RequestAsync("SaveReplayBuffer");
        if (!result.Ok)
        {
            _awaitingPlayback = false;
            _toast.Show("No replay", result.Comment ?? "OBS rejected the request.",
                ToastKind.Error, ToastSound.Error);
        }
    }

    private async Task HandOffToPlaybackAsync(string source)
    {
        System.IO.Directory.CreateDirectory(Config.TempFolder);

        var target = Path.Combine(Config.TempFolder,
            $"replay-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(source)}");

        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                File.Move(source, target);
                TempReplayReady?.Invoke(target);
                SetStatus("Instant replay ready.");
                return;
            }
            catch (IOException)
            {
                await Task.Delay(250);
            }
            catch (Exception ex)
            {
                SetStatus($"Playback failed: {ex.Message}");
                return;
            }
        }

        TempReplayReady?.Invoke(source);
        _ = _library.RefreshAsync();
    }

    public Task RefreshSyncAsync() => SyncAsync();

    private static void ClearTempFolder()
    {
        try
        {
            if (!System.IO.Directory.Exists(Config.TempFolder)) return;
            foreach (var stale in System.IO.Directory.GetFiles(Config.TempFolder))
                File.Delete(stale);
        }
        catch { }
    }

    private void RunObsBootstrap()
    {
        if (!_config.AutoConfigureObs) return;

        ObsSetup = ObsBootstrap.Ensure(_config);

        switch (ObsSetup.State)
        {
            case ObsSetupState.NeedsRestart when _config.AutoLaunchObs:
                if (ObsBootstrap.LaunchObs())
                    _toast.Show("Setting up OBS", ObsSetup.Detail, ToastKind.Info);
                break;

            case ObsSetupState.Ready when !ObsBootstrap.ObsRunning && _config.AutoLaunchObs:
                ObsBootstrap.LaunchObs();
                break;

            case ObsSetupState.ObsRunningLocked:
                _toast.Show("OBS needs a restart", ObsSetup.Detail, ToastKind.Warning);
                break;

            case ObsSetupState.ObsNotFound:
                _toast.Show("OBS not found", ObsSetup.Detail, ToastKind.Warning);
                break;
        }
    }

    public async Task<string?> InstallFfmpegAsync(bool announce = false)
    {
        if (FfmpegInstalling) return "An install is already running.";

        FfmpegInstalling = true;
        StatusChanged?.Invoke();

        if (announce)
            _toast.Show("Setting up ffmpeg", "Downloading it in the background.", ToastKind.Info);

        var progress = new Progress<string>(line =>
        {
            SetStatus(line);
            FfmpegProgress?.Invoke(line);
        });

        var error = await FfmpegInstaller.InstallAsync(_config, progress);

        FfmpegInstalling = false;

        if (error != null)
        {
            SetStatus($"ffmpeg install failed: {error}");
            FfmpegProgress?.Invoke($"Failed: {error}");
            if (announce)
                _toast.Show("ffmpeg setup failed", error, ToastKind.Warning);
            return error;
        }

        SetStatus("ffmpeg ready.");
        FfmpegProgress?.Invoke("ffmpeg installed.");

        await _library.ReEnrichAllAsync();
        StatusChanged?.Invoke();

        if (announce)
            _toast.Show("ffmpeg ready", "Thumbnails and trimming are available.", ToastKind.Success);

        return null;
    }

    public void ApplyClipsFolder()
    {
        var folder = !string.IsNullOrWhiteSpace(_config.ClipsFolder)
            ? _config.ClipsFolder
            : _obsRecordDirectory;

        _library.SetFolder(folder);
        StatusChanged?.Invoke();
    }

    private async Task DiscoverClipsFolderAsync()
    {
        var response = await _obs.RequestAsync("GetRecordDirectory");
        if (response is { Ok: true, Data.ValueKind: JsonValueKind.Object } &&
            response.Data.TryGetProperty("recordDirectory", out var dir) &&
            dir.ValueKind == JsonValueKind.String)
        {
            _obsRecordDirectory = dir.GetString();
            ApplyClipsFolder();
        }
    }

    public void ResetDetection()
    {
        _watcher.ResetLock();
        _watcher.PollNow();
    }

    public async Task ApplyAudioLayersAsync()
    {
        if (!_config.EnableAudioLayers || !_obs.IsConnected) return;

        var detail = await AudioLayerSetup.ApplyAsync(_obs, _config);
        if (!string.IsNullOrEmpty(detail)) SetStatus(detail);
    }

    public async Task ToggleManuallyAsync()
    {
        if (!_obs.IsConnected) return;
        await _obs.RequestAsync(_bufferActive ? "StopReplayBuffer" : "StartReplayBuffer");
    }

    private void SetStatus(string line)
    {
        StatusLine = line;
        StatusChanged?.Invoke();
    }

    public async Task ShutdownAsync()
    {
        _watcher.Stop();
        _hotkeys.Dispose();

        if (_obs.IsConnected && _bufferActive)
            await _obs.RequestAsync("StopReplayBuffer", timeoutMs: 2000);

        await _obs.StopAsync();
    }

    public void Dispose()
    {
        _obs.Dispose();
        _hotkeys.Dispose();
        _library.Dispose();
    }
}
