using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipWatch;

public static class Filmstrip
{
    public const int Count = 14;

    private const int CellHeight = 84;

    private static readonly ConcurrentDictionary<string, ImageSource[]> Memory = new();

    private static readonly ConcurrentDictionary<string, Task<ImageSource[]>> InFlight = new();

    public static string Folder =>
        Path.Combine(Path.GetTempPath(), "ClipWatch", "strips");

    public static string PathFor(string videoPath)
    {
        var stamp = "";
        try { stamp = File.GetLastWriteTimeUtc(videoPath).Ticks.ToString(); }
        catch { }

        var key = videoPath.ToLowerInvariant() + "|" + stamp;
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(Folder, hash + ".jpg");
    }

    public static ImageSource[] Cached(string videoPath) =>
        Memory.TryGetValue(PathFor(videoPath), out var frames) ? frames : Array.Empty<ImageSource>();

    public static Task<ImageSource[]> GetAsync(
        string videoPath, double durationSeconds, CancellationToken token = default)
    {
        if (!Ffmpeg.Available || !File.Exists(videoPath))
            return Task.FromResult(Array.Empty<ImageSource>());

        var sheet = PathFor(videoPath);
        if (Memory.TryGetValue(sheet, out var done)) return Task.FromResult(done);

        return InFlight.GetOrAdd(sheet, _ => Build(videoPath, sheet, durationSeconds, token));
    }

    public static void Preload(string videoPath, double durationSeconds)
    {
        if (!Ffmpeg.Available || durationSeconds <= 0) return;
        _ = GetAsync(videoPath, durationSeconds);
    }

    private static async Task<ImageSource[]> Build(
        string videoPath, string sheetPath, double durationSeconds, CancellationToken token)
    {
        try
        {
            if (!File.Exists(sheetPath))
            {
                var ok = await Ffmpeg.SpriteSheetAsync(
                    videoPath, sheetPath, Count, durationSeconds, CellHeight, token);
                if (!ok) return Array.Empty<ImageSource>();
            }

            var frames = Slice(sheetPath);
            if (frames.Length > 0) Memory[sheetPath] = frames;
            return frames;
        }
        catch
        {
            return Array.Empty<ImageSource>();
        }
        finally
        {
            InFlight.TryRemove(sheetPath, out _);
        }
    }

    private static ImageSource[] Slice(string sheetPath)
    {
        var sheet = new BitmapImage();
        sheet.BeginInit();

        sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        sheet.UriSource = new Uri(sheetPath);
        sheet.EndInit();
        sheet.Freeze();

        var cell = sheet.PixelWidth / Count;
        if (cell <= 0) return Array.Empty<ImageSource>();

        var frames = new List<ImageSource>(Count);
        for (var i = 0; i < Count; i++)
        {
            var x = i * cell;
            if (x + cell > sheet.PixelWidth) break;

            var crop = new CroppedBitmap(sheet, new System.Windows.Int32Rect(x, 0, cell, sheet.PixelHeight));
            crop.Freeze();
            frames.Add(crop);
        }

        return frames.ToArray();
    }

    public static void Invalidate(string videoPath)
    {
        var sheet = PathFor(videoPath);
        Memory.TryRemove(sheet, out _);
        try { if (File.Exists(sheet)) File.Delete(sheet); }
        catch { }
    }
}
