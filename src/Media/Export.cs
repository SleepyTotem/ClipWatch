using System.Globalization;
using System.IO;
using System.Text;

namespace ClipWatch;

public enum ExportMode
{
    Fast,

    Precise
}

public enum ExportContainer
{
    KeepOriginal,
    Mp4,
    Mkv
}

public enum ExportQuality
{
    High,
    Balanced,
    Small
}

public enum ExportDestination
{
    NewFile,

    ReplaceOriginal,

    ChosenFolder
}

public enum ExportAfter
{
    Nothing,
    OpenFolder,
    CopyPath
}

public sealed record AudioMix(int Index, double Volume, bool Muted)
{
    public bool Audible => !Muted && Volume > 0.001;

    public bool Untouched => !Muted && Math.Abs(Volume - 1.0) < 0.001;
}

public sealed record ExportRequest
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }

    public ExportMode Mode { get; init; } = ExportMode.Fast;
    public ExportQuality Quality { get; init; } = ExportQuality.High;

    public int? TargetHeight { get; init; }

    public IReadOnlyList<AudioMix> Audio { get; init; } = Array.Empty<AudioMix>();

    public TimeSpan Duration => End - Start;
}

public sealed record ExportResult(bool Ok, string Message);

public static class Exporter
{
    private static int Crf(ExportQuality quality) => quality switch
    {
        ExportQuality.High => 18,
        ExportQuality.Balanced => 21,
        _ => 26
    };

    private static double BitrateFor(ExportQuality quality, int width, int height)
    {
        var bpp = quality switch
        {
            ExportQuality.High => 0.13,
            ExportQuality.Balanced => 0.07,
            _ => 0.035
        };

        var pixels = (double)Math.Max(1, width) * Math.Max(1, height);
        return pixels * 60 * bpp;
    }

    public static string ExtensionFor(ExportContainer container, string sourceExtension) => container switch
    {
        ExportContainer.Mp4 => ".mp4",
        ExportContainer.Mkv => ".mkv",
        _ => sourceExtension
    };

    public static long EstimateBytes(ExportRequest request, MediaInfo info)
    {
        var seconds = Math.Max(0, request.Duration.TotalSeconds);
        if (seconds <= 0) return 0;

        var scaling = request.TargetHeight is { } h && info.Height > 0 && h < info.Height;
        var reencodingVideo = request.Mode == ExportMode.Precise || scaling;

        double videoBits;
        if (reencodingVideo)
        {
            var height = request.TargetHeight ?? info.Height;
            var width = info.Height > 0 ? (int)Math.Round(info.Width * (double)height / info.Height) : info.Width;
            videoBits = BitrateFor(request.Quality, width, height) * seconds;
        }
        else
        {
            var bitrate = info.VideoBitrate > 0
                ? info.VideoBitrate
                : Math.Max(0, info.TotalBitrate - 192_000);
            videoBits = bitrate * seconds;
        }

        var audible = request.Audio.Count(a => a.Audible);
        var audioBits = (audible > 0 ? 192_000d : 0) * seconds;

        return (long)((videoBits + audioBits) / 8);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024L * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB"
    };

    public static async Task<ExportResult> RunAsync(ExportRequest request, CancellationToken token = default)
    {
        if (!Ffmpeg.Available) return new ExportResult(false, "ffmpeg isn't installed yet.");
        if (!File.Exists(request.Source)) return new ExportResult(false, "The source clip has been moved or deleted.");
        if (request.Duration <= TimeSpan.Zero) return new ExportResult(false, "Choose a valid in and out point.");

        var folder = Path.GetDirectoryName(Path.GetFullPath(request.Destination))!;
        Directory.CreateDirectory(folder);

        var args = BuildArguments(request);

        var (ok, _, stderr) = await Ffmpeg.RunFfmpegAsync(args, token, TimeSpan.FromMinutes(30));

        if (ok && File.Exists(request.Destination)) return new ExportResult(true, "");

        var tail = stderr.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "ffmpeg failed.";
        return new ExportResult(false, tail);
    }

    public static string BuildArguments(ExportRequest request)
    {
        var scaling = request.TargetHeight is { } target && target > 0;
        var reencodeVideo = request.Mode == ExportMode.Precise || scaling;

        var audible = request.Audio.Where(a => a.Audible).ToList();
        var everyTrackUntouched = request.Audio.Count > 0 && request.Audio.All(a => a.Untouched);

        var mixing = request.Audio.Count > 0 && !everyTrackUntouched;

        var sb = new StringBuilder();

        sb.Append("-n -ss ").Append(Ffmpeg.Fmt(request.Start));
        sb.Append(" -i \"").Append(request.Source).Append('"');
        sb.Append(" -t ").Append(Ffmpeg.Fmt(request.Duration));

        if (mixing && audible.Count > 0)
        {
            sb.Append(" -filter_complex \"").Append(BuildAudioFilter(audible)).Append('"');
            sb.Append(" -map 0:v:0 -map \"[aout]\"");
        }
        else if (mixing)
        {
            sb.Append(" -map 0:v:0 -an");
        }
        else
        {
            sb.Append(" -map 0:v:0 -map 0:a?");
        }

        if (reencodeVideo)
        {
            sb.Append(" -c:v libx264 -preset veryfast -crf ").Append(Crf(request.Quality));
            sb.Append(" -pix_fmt yuv420p");
            if (scaling)
                sb.Append(" -vf scale=-2:").Append(request.TargetHeight!.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(" -c:v copy");
        }

        if (mixing && audible.Count > 0) sb.Append(" -c:a aac -b:a 192k");
        else if (!mixing) sb.Append(" -c:a copy");

        sb.Append(" -avoid_negative_ts make_zero");

        if (new[] { ".mp4", ".m4v", ".mov" }.Contains(Path.GetExtension(request.Destination).ToLowerInvariant()))
            sb.Append(" -movflags +faststart");

        sb.Append(" \"").Append(request.Destination).Append('"');
        return sb.ToString();
    }

    private static string BuildAudioFilter(IReadOnlyList<AudioMix> audible)
    {
        var sb = new StringBuilder();
        var labels = new List<string>(audible.Count);

        foreach (var track in audible)
        {
            var label = "a" + track.Index;
            labels.Add(label);

            sb.Append("[0:a:").Append(track.Index).Append(']');
            sb.Append("volume=").Append(track.Volume.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append('[').Append(label).Append("];");
        }

        if (labels.Count == 1)
        {
            return sb.ToString()[..^1].Replace($"[{labels[0]}]", "[aout]");
        }

        foreach (var label in labels) sb.Append('[').Append(label).Append(']');
        sb.Append("amix=inputs=").Append(labels.Count).Append(":normalize=0[aout]");

        return sb.ToString();
    }
}
