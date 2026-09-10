using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipWatch;

public sealed class AudioLayer
{
    public string Process { get; set; } = "";

    public string Label { get; set; } = "";

    public int Track { get; set; } = 2;

    public string SourceName => "ClipWatch: " + (string.IsNullOrWhiteSpace(Label) ? Process : Label);
}

public sealed class Config
{
    public const int CurrentVersion = 4;

    public int ConfigVersion { get; set; } = CurrentVersion;

    public string ObsHost { get; set; } = "127.0.0.1";

    public int ObsPort { get; set; } = 4455;

    public string ObsPassword { get; set; } = "";

    public int PollIntervalMs { get; set; } = 1000;

    public int StopDelaySeconds { get; set; } = 10;

    public List<string> GameProcessWhitelist { get; set; } = new();

    public List<string> IgnoredProcesses { get; set; } = new()
    {
        "explorer", "dwm", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "ApplicationFrameHost", "TextInputHost", "LockApp", "SystemSettings", "Taskmgr",
        "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera", "opera_gx",
        "vivaldi", "zen", "librewolf", "waterfox", "iexplore",
        "Discord", "DiscordPTB", "DiscordCanary", "Spotify", "Slack", "Teams", "ms-teams",
        "obs64", "obs32", "steam", "steamwebhelper", "EpicGamesLauncher", "Battle.net",
        "RiotClientUX", "RiotClientServices", "GalaxyClient", "Origin", "EADesktop",
        "UbisoftConnect", "upc", "itch", "Playnite.DesktopApp",
        "Code", "devenv", "rider64", "idea64", "sublime_text", "notepad", "notepad++",
        "WindowsTerminal", "powershell", "pwsh", "cmd", "explorer",
        "mpv", "vlc", "mpc-hc64", "PotPlayerMini64", "Photos", "mstsc", "ClipWatch"
    };

    public bool UseFullscreenHeuristic { get; set; } = false;

    public bool UseOwnSaveHotkey { get; set; } = false;

    public int SaveHotkeyVirtualKey { get; set; } = 0x77;

    public bool EnableLearnHotkey { get; set; } = true;

    public int LearnHotkeyVirtualKey { get; set; } = 0x76;

    public uint LearnHotkeyModifiers { get; set; } = 4;

    public bool EnablePlaybackHotkey { get; set; } = true;

    public int PlaybackHotkeyVirtualKey { get; set; } = 0x78;

    public uint PlaybackHotkeyModifiers { get; set; } = 4;

    public bool AutoConfigureObs { get; set; } = true;

    public bool AutoLaunchObs { get; set; } = true;

    public bool AlwaysClip { get; set; } = false;

    public string? ClipsFolder { get; set; }

    public string? FfmpegFolder { get; set; }

    public bool AutoInstallFfmpeg { get; set; } = true;

    public bool StartMinimised { get; set; }

    public bool ShowToasts { get; set; } = true;

    public bool PlaySounds { get; set; } = true;

    public double SoundVolume { get; set; } = 0.35;

    public int ToastDurationMs { get; set; } = 2600;

    public bool EnableAudioLayers { get; set; } = false;

    public List<AudioLayer> AudioLayers { get; set; } = new();

    public string? ObsOutputModeBeforeLayers { get; set; }

    public double PreviewVolume { get; set; } = 0.8;

    public ExportMode ExportMode { get; set; } = ExportMode.Fast;
    public ExportContainer ExportContainer { get; set; } = ExportContainer.KeepOriginal;
    public ExportQuality ExportQuality { get; set; } = ExportQuality.High;

    public int? ExportHeight { get; set; }

    public ExportDestination ExportDestination { get; set; } = ExportDestination.NewFile;
    public ExportAfter ExportAfter { get; set; } = ExportAfter.Nothing;

    public string? ExportFolder { get; set; }

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipWatch");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "config.json");

    [JsonIgnore]
    public static string TempFolder => Path.Combine(Directory, "temp");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,

        Converters = { new JsonStringEnumConverter() }
    };

    public static Config Load()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<Config>(json, Options);
                if (cfg != null)
                {
                    if (cfg.Migrate()) cfg.Save();
                    return cfg;
                }
            }
        }
        catch
        {
        }

        var fresh = new Config();
        fresh.Save();
        return fresh;
    }

    private bool Migrate()
    {
        if (ConfigVersion >= CurrentVersion) return false;

        if (ConfigVersion < 2)
            UseFullscreenHeuristic = false;

        if (ConfigVersion < 3)
        {
            UseFullscreenHeuristic = false;
            AutoConfigureObs = true;
        }

        ConfigVersion = CurrentVersion;
        return true;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
        }
    }
}
