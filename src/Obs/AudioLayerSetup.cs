using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipWatch;

public static class AudioLayerSetup
{
    public const int MaxTracks = 6;
    public const int FirstLayerTrack = 2;

    private const string ProcessCaptureKind = "wasapi_process_output_capture";

    private const int MatchByExecutable = 2;

    public static int TrackMask(Config config)
    {
        var mask = 1;
        foreach (var layer in config.AudioLayers)
            if (layer.Track is >= FirstLayerTrack and <= MaxTracks)
                mask |= 1 << (layer.Track - 1);
        return mask;
    }

    public static int NextFreeTrack(Config config)
    {
        for (var track = FirstLayerTrack; track <= MaxTracks; track++)
            if (config.AudioLayers.All(l => l.Track != track))
                return track;
        return 0;
    }

    public static async Task<string> ApplyAsync(ObsClient obs, Config config)
    {
        if (!config.EnableAudioLayers) return "";
        if (config.AudioLayers.Count == 0) return "No audio layers configured.";

        if (!await SupportsProcessCaptureAsync(obs))
            return "This OBS build has no Application Audio Capture source, so per-app tracks aren't available.";

        var scene = await CurrentSceneAsync(obs);
        if (scene == null) return "Couldn't read the current OBS scene.";

        var existing = await InputNamesAsync(obs);
        var created = 0;
        var failed = new List<string>();

        foreach (var layer in config.AudioLayers)
        {
            if (string.IsNullOrWhiteSpace(layer.Process)) continue;
            if (layer.Track is < FirstLayerTrack or > MaxTracks) continue;

            var name = layer.SourceName;

            if (!existing.Contains(name))
            {
                if (await CreateCaptureAsync(obs, scene, name, layer.Process)) created++;
                else { failed.Add(layer.Process); continue; }
            }
            else
            {
                await UpdateCaptureAsync(obs, name, layer.Process);
            }

            if (!await RouteToTrackAsync(obs, name, layer.Track)) failed.Add(layer.Process);
        }

        foreach (var name in existing)
            if (IsDefaultAudioInput(name))
                await RouteToTrackAsync(obs, name, 1);

        if (failed.Count > 0)
            return $"Set up {config.AudioLayers.Count - failed.Count} layer(s); couldn't set up {string.Join(", ", failed)}.";

        return created > 0
            ? $"Created {created} audio capture source(s) in OBS."
            : $"{config.AudioLayers.Count} audio layer(s) ready.";
    }

    public static async Task RemoveAsync(ObsClient obs, IEnumerable<AudioLayer> layers)
    {
        var existing = await InputNamesAsync(obs);

        foreach (var layer in layers)
        {
            if (!existing.Contains(layer.SourceName)) continue;
            await obs.RequestAsync("RemoveInput", new JsonObject { ["inputName"] = layer.SourceName });
        }
    }

    private static async Task<bool> SupportsProcessCaptureAsync(ObsClient obs)
    {
        var response = await obs.RequestAsync("GetInputKindList");
        if (!response.Ok) return false;

        try
        {
            if (!response.Data.TryGetProperty("inputKinds", out var kinds)) return false;
            foreach (var kind in kinds.EnumerateArray())
                if (kind.GetString() == ProcessCaptureKind) return true;
        }
        catch { }

        return false;
    }

    private static async Task<string?> CurrentSceneAsync(ObsClient obs)
    {
        var response = await obs.RequestAsync("GetCurrentProgramScene");
        if (!response.Ok) return null;

        try
        {
            if (response.Data.TryGetProperty("sceneName", out var name)) return name.GetString();
            if (response.Data.TryGetProperty("currentProgramSceneName", out var legacy)) return legacy.GetString();
        }
        catch { }

        return null;
    }

    private static async Task<HashSet<string>> InputNamesAsync(ObsClient obs)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var response = await obs.RequestAsync("GetInputList");
        if (!response.Ok) return names;

        try
        {
            if (!response.Data.TryGetProperty("inputs", out var inputs)) return names;
            foreach (var input in inputs.EnumerateArray())
                if (input.TryGetProperty("inputName", out var name) && name.GetString() is { } text)
                    names.Add(text);
        }
        catch { }

        return names;
    }

    private static async Task<bool> CreateCaptureAsync(ObsClient obs, string scene, string name, string process)
    {
        var response = await obs.RequestAsync("CreateInput", new JsonObject
        {
            ["sceneName"] = scene,
            ["inputName"] = name,
            ["inputKind"] = ProcessCaptureKind,
            ["inputSettings"] = CaptureSettings(process),
            ["sceneItemEnabled"] = true
        });

        return response.Ok;
    }

    private static async Task<bool> UpdateCaptureAsync(ObsClient obs, string name, string process)
    {
        var response = await obs.RequestAsync("SetInputSettings", new JsonObject
        {
            ["inputName"] = name,
            ["inputSettings"] = CaptureSettings(process),
            ["overlay"] = true
        });

        return response.Ok;
    }

    private static JsonObject CaptureSettings(string process) => new()
    {
        ["window"] = "::" + ExeName(process),
        ["priority"] = MatchByExecutable
    };

    private static string ExeName(string process) =>
        process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process : process + ".exe";

    private static async Task<bool> RouteToTrackAsync(ObsClient obs, string inputName, int track)
    {
        var tracks = new JsonObject();
        for (var i = 1; i <= MaxTracks; i++) tracks[i.ToString()] = i == track;

        var response = await obs.RequestAsync("SetInputAudioTracks", new JsonObject
        {
            ["inputName"] = inputName,
            ["inputAudioTracks"] = tracks
        });

        return response.Ok;
    }

    private static bool IsDefaultAudioInput(string name) =>
        name.Contains("Desktop Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mic/Aux", StringComparison.OrdinalIgnoreCase);
}
