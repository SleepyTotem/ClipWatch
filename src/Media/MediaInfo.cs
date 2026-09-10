using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClipWatch;

public sealed record AudioTrack(int Index, string? Title, string? Language, int Channels)
{
    public string DisplayName(Config config)
    {
        if (!string.IsNullOrWhiteSpace(Title)) return Title!;

        var configured = config.AudioLayers
            .FirstOrDefault(l => l.Track == Index + 1 && !string.IsNullOrWhiteSpace(l.Label));
        if (configured != null) return configured.Label;

        return Index == 0 ? "Main mix" : $"Track {Index + 1}";
    }
}

public sealed record MediaInfo(
    int Width,
    int Height,
    double DurationSeconds,
    long TotalBitrate,
    long VideoBitrate,
    IReadOnlyList<AudioTrack> AudioTracks)
{
    public static MediaInfo Empty { get; } =
        new(0, 0, 0, 0, 0, Array.Empty<AudioTrack>());

    public bool HasVideo => Width > 0 && Height > 0;

    public bool HasLayers => AudioTracks.Count > 1;
}

public static class MediaProbe
{
    private static readonly Dictionary<string, MediaInfo> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<MediaInfo> ReadAsync(string file, CancellationToken token = default)
    {
        if (!Ffmpeg.Available || !File.Exists(file)) return MediaInfo.Empty;

        var key = CacheKey(file);

        await Gate.WaitAsync(token);
        try
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }
        finally { Gate.Release(); }

        var info = await ProbeAsync(file, token);

        await Gate.WaitAsync(token);
        try { Cache[key] = info; }
        finally { Gate.Release(); }

        return info;
    }

    public static void Invalidate(string file)
    {
        Gate.Wait();
        try { Cache.Remove(CacheKey(file)); }
        finally { Gate.Release(); }
    }

    private static string CacheKey(string file)
    {
        try { return file.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(file).Ticks; }
        catch { return file.ToLowerInvariant(); }
    }

    private static async Task<MediaInfo> ProbeAsync(string file, CancellationToken token)
    {
        var args = "-v error -show_entries " +
                   "stream=index,codec_type,width,height,channels,bit_rate:" +
                   "stream_tags=title,language:" +
                   "format=duration,bit_rate " +
                   $"-of json \"{file}\"";

        var (ok, stdout, _) = await Ffmpeg.RunProbeAsync(args, token, TimeSpan.FromSeconds(30));
        if (!ok || string.IsNullOrWhiteSpace(stdout)) return MediaInfo.Empty;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var width = 0;
            var height = 0;
            long videoBitrate = 0;
            var audio = new List<AudioTrack>();

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = Text(stream, "codec_type");

                    if (type == "video" && width == 0)
                    {
                        width = Int(stream, "width");
                        height = Int(stream, "height");
                        videoBitrate = Long(stream, "bit_rate");
                    }
                    else if (type == "audio")
                    {
                        string? title = null;
                        string? language = null;
                        if (stream.TryGetProperty("tags", out var tags))
                        {
                            title = Text(tags, "title");
                            language = Text(tags, "language");
                        }

                        audio.Add(new AudioTrack(audio.Count, title, language, Int(stream, "channels")));
                    }
                }
            }

            double duration = 0;
            long totalBitrate = 0;
            if (root.TryGetProperty("format", out var format))
            {
                duration = Double(format, "duration");
                totalBitrate = Long(format, "bit_rate");
            }

            return new MediaInfo(width, height, duration, totalBitrate, videoBitrate, audio);
        }
        catch
        {
            return MediaInfo.Empty;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out var n) ? n : 0;
        return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
    }

    private static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out var n) ? n : 0;
        return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
    }

    private static double Double(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
    }
}
