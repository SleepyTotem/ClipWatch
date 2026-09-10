using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace ClipWatch;

public static class FfmpegInstaller
{
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public static string TargetFolder => Path.Combine(Config.Directory, "ffmpeg");

    public static bool IsInstalledLocally =>
        File.Exists(Path.Combine(TargetFolder, "ffmpeg.exe")) &&
        File.Exists(Path.Combine(TargetFolder, "ffprobe.exe"));

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string?> InstallAsync(
        Config config,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        if (!await Gate.WaitAsync(0, token))
            return "An install is already running.";

        var zipPath = Path.Combine(Path.GetTempPath(), $"clipwatch_ffmpeg_{Guid.NewGuid():N}.zip");

        try
        {
            progress?.Report("Contacting github.com...");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClipWatch");

            using var response = await http.GetAsync(
                DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);

            if (!response.IsSuccessStatusCode)
                return $"Download failed ({(int)response.StatusCode}). Check your connection.";

            var total = response.Content.Headers.ContentLength ?? 0;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = File.Create(zipPath))
            {
                var buffer = new byte[128 * 1024];
                long read = 0;
                var lastReport = -1;
                int n;

                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;

                    if (total > 0)
                    {
                        var pct = (int)(read * 100 / total);
                        if (pct != lastReport && pct % 2 == 0)
                        {
                            lastReport = pct;
                            progress?.Report($"Downloading ffmpeg... {pct}%");
                        }
                    }
                    else
                    {
                        progress?.Report($"Downloading ffmpeg... {read / 1024 / 1024} MB");
                    }
                }
            }

            progress?.Report("Extracting...");
            Directory.CreateDirectory(TargetFolder);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var wanted = new[] { "ffmpeg.exe", "ffprobe.exe" };
                var found = 0;

                foreach (var entry in zip.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                    entry.ExtractToFile(Path.Combine(TargetFolder, name), overwrite: true);
                    found++;
                }

                if (found < 2)
                    return "The archive didn't contain ffmpeg.exe and ffprobe.exe.";
            }

            config.FfmpegFolder = TargetFolder;
            config.Save();
            Ffmpeg.Locate(config);

            if (!Ffmpeg.Available)
                return "Extracted, but the executables still couldn't be found.";

            progress?.Report("ffmpeg installed.");
            return null;
        }
        catch (OperationCanceledException)
        {
            return "Cancelled.";
        }
        catch (HttpRequestException ex)
        {
            return $"Download failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException)
        {
            return "Couldn't write to the ClipWatch folder. Check permissions.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            Gate.Release();
        }
    }
}
