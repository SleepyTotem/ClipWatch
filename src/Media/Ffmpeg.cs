using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ClipWatch;

public static class Ffmpeg
{
    private static readonly SemaphoreSlim ThumbnailWork = new(1, 1);

    // Background jobs (probe, thumbnail, filmstrip) and the file each one is reading, so they
    // can be stopped before that file is moved or deleted — otherwise Windows refuses with
    // "the file is open in ffmpeg".
    private static readonly ConcurrentDictionary<Process, string> Readers = new();
    private static readonly ConcurrentDictionary<string, byte> Held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kills any background ffmpeg job reading <paramref name="path"/> and stops new ones
    /// starting on it until the returned handle is disposed.
    /// </summary>
    public static IDisposable ReleaseFile(string path)
    {
        var target = Normalise(path);
        if (target == null) return new FileHold(null);

        Held[target] = 0;

        foreach (var (proc, file) in Readers)
        {
            if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            catch { }
        }

        return new FileHold(target);
    }

    private sealed class FileHold(string? path) : IDisposable
    {
        public void Dispose()
        {
            if (path != null) Held.TryRemove(path, out _);
        }
    }

    private static string? Normalise(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }
    public static string? FfmpegPath { get; private set; }
    public static string? FfprobePath { get; private set; }
    public static bool Available => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    private static readonly string[] Playable = { ".mp4", ".m4v", ".mov", ".wmv", ".avi" };

    public static bool IsPlayable(string path) =>
        Playable.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static void Locate(Config config)
    {
        FfmpegPath = Find("ffmpeg.exe", config.FfmpegFolder);
        FfprobePath = Find("ffprobe.exe", config.FfmpegFolder);
    }

    private static string? Find(string exe, string? configuredFolder)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredFolder))
        {
            candidates.Add(Path.Combine(configuredFolder, exe));
            candidates.Add(Path.Combine(configuredFolder, "bin", exe));
        }

        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        if (appDir.Length > 0) candidates.Add(Path.Combine(appDir, exe));
        candidates.Add(Path.Combine(FfmpegInstaller.TargetFolder, exe));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (!string.IsNullOrWhiteSpace(dir))
                candidates.Add(Path.Combine(dir.Trim(), exe));

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(pf, "ffmpeg", "bin", exe));
        candidates.Add(Path.Combine(@"C:\ffmpeg\bin", exe));

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; }
            catch { }
        }

        return null;
    }

    public static async Task<TimeSpan?> ProbeDurationAsync(string file)
    {
        if (FfprobePath == null || !File.Exists(file)) return null;

        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{file}\"";
        var (ok, stdout, _) = await RunAsync(FfprobePath, args, timeout: TimeSpan.FromSeconds(20), input: file);
        if (!ok) return null;

        return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public static async Task<bool> ThumbnailAsync(string file, string outJpg, TimeSpan at, CancellationToken token = default)
    {
        if (FfmpegPath == null || !File.Exists(file)) return false;
        var acquired = false;
        try
        {
            await ThumbnailWork.WaitAsync(token);
            acquired = true;
            Directory.CreateDirectory(Path.GetDirectoryName(outJpg)!);
            var args = $"-y -threads 1 -ss {Fmt(at)} -i \"{file}\" -map 0:v:0 -an -frames:v 1 -vf scale=320:-1 -filter_threads 1 -threads 1 -q:v 5 \"{outJpg}\"";
            var (ok, _, _) = await RunAsync(FfmpegPath, args, token, TimeSpan.FromSeconds(20), file);
            if (ok && File.Exists(outJpg)) return true;
            try { File.Delete(outJpg); } catch { }
            return false;
        }
        catch { return false; }
        finally { if (acquired) ThumbnailWork.Release(); }
    }

    public static async Task<bool> SpriteSheetAsync(
        string file, string outJpg, int count, double durationSeconds,
        int cellHeight = 84, CancellationToken token = default)
    {
        if (FfmpegPath == null || !File.Exists(file) || count < 1) return false;
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return false;

        var acquired = false;
        try
        {
            await ThumbnailWork.WaitAsync(token);
            acquired = true;
            Directory.CreateDirectory(Path.GetDirectoryName(outJpg)!);

            var fps = Inv(count / durationSeconds);
            var filter = $"fps={fps},scale=-2:{cellHeight},tile={count}x1";
            var args = $"-y -threads 1 -i \"{file}\" -map 0:v:0 -an -vf \"{filter}\" " +
                       $"-frames:v 1 -filter_threads 1 -q:v 4 \"{outJpg}\"";

            var (ok, _, _) = await RunAsync(FfmpegPath, args, token, TimeSpan.FromMinutes(3), file);
            if (ok && File.Exists(outJpg)) return true;
            try { File.Delete(outJpg); } catch { }
            return false;
        }
        catch { return false; }
        finally { if (acquired) ThumbnailWork.Release(); }
    }

    private static string Inv(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    public static async Task<(bool Ok, string Error)> TrimAsync(
        string src, string dst, TimeSpan start, TimeSpan end, bool reencode)
    {
        if (FfmpegPath == null) return (false, "ffmpeg not found.");
        if (!File.Exists(src)) return (false, "The source clip has been moved or deleted.");
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            return (false, "The output must be different from the source.");

        var duration = end - start;
        if (start < TimeSpan.Zero || duration <= TimeSpan.Zero) return (false, "Choose a valid in and out point.");

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        var codec = reencode
            ? "-c:v libx264 -preset veryfast -crf 18 -c:a aac -b:a 192k"
            : "-c copy";

        var fastStart = new[] { ".mp4", ".mov", ".m4v" }.Contains(Path.GetExtension(dst).ToLowerInvariant())
            ? "-movflags +faststart" : "";

        var args = $"-n -ss {Fmt(start)} -i \"{src}\" -t {Fmt(duration)} -map 0:v:0 -map 0:a? {codec} -avoid_negative_ts make_zero {fastStart} \"{dst}\"";

        var (ok, _, stderr) = await RunAsync(FfmpegPath, args);
        if (ok && File.Exists(dst)) return (true, "");

        var tail = stderr.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "ffmpeg failed.";
        return (false, tail);
    }

    public static async Task<(bool Ok, string Error)> RemuxToMp4Async(string src, string dst)
    {
        if (FfmpegPath == null) return (false, "ffmpeg not found.");

        if (!File.Exists(src)) return (false, "The source clip has been moved or deleted.");
        if (File.Exists(dst)) return (false, "The output file already exists.");
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dst))!, $".clipwatch-{Guid.NewGuid():N}.mp4");
        try
        {
            var args = $"-n -i \"{src}\" -map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero -movflags +faststart \"{temp}\"";
            var (ok, _, stderr) = await RunAsync(FfmpegPath, args);
            if (ok && File.Exists(temp))
            {
                File.Move(temp, dst);
                return (true, "");
            }
            return (false, stderr.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "ffmpeg failed.");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { File.Delete(temp); } catch { } }
    }

    internal static string Fmt(TimeSpan t) =>
        t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    internal static Task<(bool Ok, string StdOut, string StdErr)> RunFfmpegAsync(
        string args, CancellationToken token = default, TimeSpan? timeout = null) =>
        FfmpegPath == null
            ? Task.FromResult((false, "", "ffmpeg not found."))
            : RunAsync(FfmpegPath, args, token, timeout);

    internal static Task<(bool Ok, string StdOut, string StdErr)> RunProbeAsync(
        string args, CancellationToken token = default, TimeSpan? timeout = null) =>
        FfprobePath == null
            ? Task.FromResult((false, "", "ffprobe not found."))
            : RunAsync(FfprobePath, args, token, timeout);

    private static async Task<(bool Ok, string StdOut, string StdErr)> RunAsync(
        string exe, string args, CancellationToken token = default, TimeSpan? timeout = null,
        string? input = null)
    {
        var reading = input == null ? null : Normalise(input);
        if (reading != null && Held.ContainsKey(reading))
            return (false, "", "The file is being moved or deleted.");

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, "-hide_banner -loglevel error " +
                    (string.Equals(exe, FfmpegPath, StringComparison.OrdinalIgnoreCase) ? "-nostdin " : "") + args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            proc.Start();
            if (reading != null) Readers[proc] = reading;

            try
            {
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
                try { await proc.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    await proc.WaitForExitAsync();
                    await Task.WhenAll(stdout, stderr);
                    return (false, "", token.IsCancellationRequested ? "Cancelled." : "The media operation timed out.");
                }

                return (proc.ExitCode == 0, await stdout, await stderr);
            }
            finally
            {
                if (reading != null) Readers.TryRemove(proc, out _);
            }
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}
