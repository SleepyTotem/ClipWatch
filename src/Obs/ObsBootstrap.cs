using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ClipWatch;

public enum ObsSetupState
{
    Ready,

    NeedsRestart,

    ObsNotFound,

    ObsRunningLocked
}

public readonly record struct ObsSetupResult(ObsSetupState State, string Detail);

public static class ObsBootstrap
{
    private static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    private static string WebsocketConfig =>
        Path.Combine(Root, "plugin_config", "obs-websocket", "config.json");

    private static string GlobalIni => Path.Combine(Root, "global.ini");

    public const int RequiredBufferSeconds = 60;

    public static bool ObsRunning =>
        Process.GetProcessesByName("obs64").Length > 0 ||
        Process.GetProcessesByName("obs32").Length > 0;

    public static bool TryReadWebsocket(out int port, out string password, out bool enabled)
    {
        port = 4455;
        password = "";
        enabled = false;

        try
        {
            if (!File.Exists(WebsocketConfig)) return false;

            var node = JsonNode.Parse(File.ReadAllText(WebsocketConfig)) as JsonObject;
            if (node == null) return false;

            if (node["server_port"] is { } p && int.TryParse(p.ToString(), out var parsed))
                port = parsed;

            enabled = node["server_enabled"]?.GetValue<bool>() ?? false;

            var authRequired = node["auth_required"]?.GetValue<bool>() ?? false;
            password = authRequired ? node["server_password"]?.GetValue<string>() ?? "" : "";

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindObsExe()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OBS Studio");
            if (key?.GetValue(null) is string dir)
            {
                var exe = Path.Combine(dir, "bin", "64bit", "obs64.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            var exe = Path.Combine(root, "obs-studio", "bin", "64bit", "obs64.exe");
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    public static bool LaunchObs(bool minimised = true)
    {
        var exe = FindObsExe();
        if (exe == null) return false;

        try
        {
            var info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false
            };
            info.ArgumentList.Add("--startreplaybuffer");
            if (minimised)
            {
                info.ArgumentList.Add("--minimize-to-tray");
                info.ArgumentList.Add("--disable-shutdown-check");
            }

            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static ObsSetupResult Ensure(Config config)
    {
        if (!Directory.Exists(Root))
            return new ObsSetupResult(ObsSetupState.ObsNotFound,
                "OBS's settings folder doesn't exist. Install and run OBS once.");

        var websocketOk = TryReadWebsocket(out var port, out var password, out var enabled) && enabled;
        var bufferOk = ReplayBufferConfigured();
        var tracksOk = !config.EnableAudioLayers || MultiTrackConfigured(config);

        if (websocketOk)
        {
            config.ObsPort = port;
            config.ObsPassword = password;
        }

        if (websocketOk && bufferOk && tracksOk)
        {
            config.Save();
            return new ObsSetupResult(ObsSetupState.Ready, $"OBS websocket on port {port}.");
        }

        if (ObsRunning)
            return new ObsSetupResult(ObsSetupState.ObsRunningLocked,
                "OBS needs a one-time settings change. Close OBS and ClipWatch will do it.");

        var changes = new List<string>();

        if (!websocketOk && EnableWebsocket(out port, out password))
        {
            config.ObsPort = port;
            config.ObsPassword = password;
            changes.Add("websocket server");
        }

        if (!bufferOk && EnableReplayBuffer())
            changes.Add($"{RequiredBufferSeconds}s replay buffer");

        if (!tracksOk && EnableMultiTrackRecording(config))
            changes.Add($"{config.AudioLayers.Count + 1} audio tracks");

        config.Save();

        return changes.Count == 0
            ? new ObsSetupResult(ObsSetupState.ObsNotFound, "Couldn't write OBS's settings files.")
            : new ObsSetupResult(ObsSetupState.NeedsRestart, "Enabled " + string.Join(" and ", changes) + ".");
    }

    private static bool EnableWebsocket(out int port, out string password)
    {
        port = 4455;
        password = "";

        try
        {
            var dir = Path.GetDirectoryName(WebsocketConfig)!;
            Directory.CreateDirectory(dir);

            var node = File.Exists(WebsocketConfig)
                ? JsonNode.Parse(File.ReadAllText(WebsocketConfig)) as JsonObject ?? new JsonObject()
                : new JsonObject();

            if (node["server_port"] is { } p && int.TryParse(p.ToString(), out var existing))
                port = existing;

            node["server_enabled"] = true;
            node["server_port"] = port;
            node["alerts_enabled"] = false;
            node["first_load"] = false;

            node["auth_required"] = false;
            node["server_password"] ??= "";

            File.WriteAllText(WebsocketConfig,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ActiveProfileIni()
    {
        try
        {
            var profiles = Path.Combine(Root, "basic", "profiles");
            if (!Directory.Exists(profiles)) return null;

            string? dirName = null;

            if (File.Exists(GlobalIni))
                foreach (var line in File.ReadLines(GlobalIni))
                    if (line.StartsWith("ProfileDir=", StringComparison.OrdinalIgnoreCase))
                    {
                        dirName = line[11..].Trim();
                        break;
                    }

            var dir = dirName != null
                ? Path.Combine(profiles, dirName)
                : Directory.GetDirectories(profiles).FirstOrDefault();

            if (dir == null || !Directory.Exists(dir)) return null;

            var ini = Path.Combine(dir, "basic.ini");
            return File.Exists(ini) ? ini : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ReplayBufferConfigured()
    {
        var ini = ActiveProfileIni();
        if (ini == null) return false;

        try
        {
            var text = File.ReadAllText(ini);
            var mode = ReadKey(text, "Mode") ?? "Simple";

            if (mode.Equals("Advanced", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(ReadKey(text, "RecRB"), "true", StringComparison.OrdinalIgnoreCase);
            }

            var on = string.Equals(ReadKey(text, "RecRB"), "true", StringComparison.OrdinalIgnoreCase);
            var time = int.TryParse(ReadKey(text, "RecRBTime"), out var t) ? t : 0;
            return on && time >= RequiredBufferSeconds;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnableReplayBuffer()
    {
        var ini = ActiveProfileIni();
        if (ini == null) return false;

        try
        {
            var lines = File.ReadAllLines(ini).ToList();
            var section = FindSection(lines, "SimpleOutput");

            if (section < 0)
            {
                lines.Add("");
                lines.Add("[SimpleOutput]");
                section = lines.Count - 1;
            }

            SetKey(lines, section, "RecRB", "true");
            SetKey(lines, section, "RecRBTime", RequiredBufferSeconds.ToString());

            SetKey(lines, section, "RecRBSize", "1024");

            File.WriteAllLines(ini, lines);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool MultiTrackConfigured(Config config)
    {
        var ini = ActiveProfileIni();
        if (ini == null) return false;

        try
        {
            var text = File.ReadAllText(ini);

            if (!string.Equals(ReadKey(text, "Mode"), "Advanced", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(ReadKey(text, "RecRB"), "true", StringComparison.OrdinalIgnoreCase))
                return false;

            var wanted = AudioLayerSetup.TrackMask(config);
            var actual = int.TryParse(ReadKey(text, "RecTracks"), out var mask) ? mask : 0;
            return (actual & wanted) == wanted;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnableMultiTrackRecording(Config config)
    {
        var ini = ActiveProfileIni();
        if (ini == null) return false;

        try
        {
            var lines = File.ReadAllLines(ini).ToList();
            var text = string.Join("\n", lines);

            var previous = ReadKey(text, "Mode") ?? "Simple";
            if (!string.Equals(previous, "Advanced", StringComparison.OrdinalIgnoreCase))
                config.ObsOutputModeBeforeLayers ??= previous;

            var output = FindSection(lines, "Output");
            if (output < 0)
            {
                lines.Add("");
                lines.Add("[Output]");
                output = lines.Count - 1;
            }
            SetKey(lines, output, "Mode", "Advanced");

            var adv = FindSection(lines, "AdvOut");
            if (adv < 0)
            {
                lines.Add("");
                lines.Add("[AdvOut]");
                adv = lines.Count - 1;
            }

            SetKey(lines, adv, "RecType", "Standard");
            SetKey(lines, adv, "RecTracks", AudioLayerSetup.TrackMask(config).ToString());

            SetKey(lines, adv, "RecFormat2", "mp4");

            SetKey(lines, adv, "RecRB", "true");
            SetKey(lines, adv, "RecRBTime", RequiredBufferSeconds.ToString());
            SetKey(lines, adv, "RecRBSize", "1024");

            if (ReadKey(text, "RecEncoder") == null)
                SetKey(lines, adv, "RecEncoder", "obs_x264");

            File.WriteAllLines(ini, lines);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool RestoreOutputMode(Config config)
    {
        if (string.IsNullOrWhiteSpace(config.ObsOutputModeBeforeLayers)) return false;

        var ini = ActiveProfileIni();
        if (ini == null || ObsRunning) return false;

        try
        {
            var lines = File.ReadAllLines(ini).ToList();
            var output = FindSection(lines, "Output");
            if (output < 0) return false;

            SetKey(lines, output, "Mode", config.ObsOutputModeBeforeLayers!);
            File.WriteAllLines(ini, lines);

            config.ObsOutputModeBeforeLayers = null;
            config.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadKey(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim();
        }
        return null;
    }

    private static int FindSection(List<string> lines, string name)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Trim().Equals($"[{name}]", StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static void SetKey(List<string> lines, int sectionIndex, string key, string value)
    {
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[')) break;

            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }

        lines.Insert(sectionIndex + 1, $"{key}={value}");
    }
}
