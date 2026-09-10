using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace ClipWatch;

public sealed class Clip : INotifyPropertyChanged
{
    private string _path = "";
    private string _displayName = "";
    private TimeSpan? _duration;
    private string? _thumbnailPath;
    private long _sizeBytes;

    public string Path
    {
        get => _path;
        set { _path = value; Raise(); Raise(nameof(Folder)); }
    }

    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; Raise(); }
    }

    public DateTime Created { get; set; }

    public long SizeBytes
    {
        get => _sizeBytes;
        set { _sizeBytes = value; Raise(); Raise(nameof(SizeText)); }
    }

    public TimeSpan? Duration
    {
        get => _duration;
        set { _duration = value; Raise(); Raise(nameof(DurationText)); }
    }

    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set { _thumbnailPath = value; Raise(); Raise(nameof(HasThumbnail)); }
    }

    public bool HasThumbnail => !string.IsNullOrEmpty(_thumbnailPath) && File.Exists(_thumbnailPath);
    public string Folder => System.IO.Path.GetDirectoryName(_path) ?? "";
    public string Extension => System.IO.Path.GetExtension(_path).ToLowerInvariant();
    public bool IsPlayable => Ffmpeg.IsPlayable(_path);

    public string DurationText => _duration is { } d
        ? (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))
        : "--:--";

    public string SizeText => _sizeBytes <= 0
        ? ""
        : _sizeBytes >= 1024L * 1024 * 1024
            ? $"{_sizeBytes / 1024.0 / 1024 / 1024:0.0} GB"
            : $"{_sizeBytes / 1024.0 / 1024:0} MB";

    public string CreatedText => Created.ToString("MMM d, h:mm tt");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

public sealed class ClipLibrary : IDisposable
{
    private static readonly string[] Extensions =
        { ".mp4", ".mkv", ".mov", ".flv", ".m4v", ".ts", ".avi", ".wmv" };

    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _work = new(2, 2);
    private FileSystemWatcher? _watcher;
    private bool _disposed;
    private int _scanVersion;

    public ObservableCollection<Clip> Clips { get; } = new();

    public string? Folder { get; private set; }

    public event Action? Changed;

    public static string ThumbnailFolder =>
        System.IO.Path.Combine(Config.Directory, "thumbnails");

    public ClipLibrary() => _dispatcher = Dispatcher.CurrentDispatcher;

    public void SetFolder(string? folder)
    {
        if (string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase)) return;

        Folder = folder;
        HookWatcher();
        _ = RefreshAsync();
    }

    private void HookWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;

        if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder)) return;

        _watcher = new FileSystemWatcher(Folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += (_, _) => DebouncedRefresh();
        _watcher.Deleted += (_, _) => DebouncedRefresh();
        _watcher.Renamed += (_, _) => DebouncedRefresh();
        _watcher.Changed += (_, _) => DebouncedRefresh();
        _watcher.Error += (_, _) => DebouncedRefresh();
    }

    private DispatcherTimer? _debounce;

    private void DebouncedRefresh()
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _debounce.Stop();
            _debounce.Tick -= OnDebounceTick;
            _debounce.Tick += OnDebounceTick;
            _debounce.Start();
        });
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var version = ++_scanVersion;
        var folder = Folder;
        if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder))
        {
            await _dispatcher.InvokeAsync(() => { Clips.Clear(); Changed?.Invoke(); });
            return;
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder!)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.StartsWith(".clipwatch-", StringComparison.OrdinalIgnoreCase))
                .Where(f => Extensions.Contains(f.Extension.ToLowerInvariant()))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        catch
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || version != _scanVersion || folder != Folder) return;
            var byPath = Clips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                seen.Add(f.FullName);

                if (byPath.TryGetValue(f.FullName, out var existing))
                {
                    var changed = existing.SizeBytes != f.Length || existing.Created != f.LastWriteTime;
                    existing.SizeBytes = f.Length;
                    if (changed)
                    {
                        existing.Created = f.LastWriteTime;
                        existing.Duration = null;
                        existing.ThumbnailPath = null;
                        InvalidateThumbnail(existing.Path);
                        _ = EnrichAsync(existing);
                    }
                    var currentIndex = Clips.IndexOf(existing);
                    if (currentIndex != i && i < Clips.Count) Clips.Move(currentIndex, i);
                    continue;
                }

                var clip = new Clip
                {
                    Path = f.FullName,
                    DisplayName = System.IO.Path.GetFileNameWithoutExtension(f.Name),
                    Created = f.LastWriteTime,
                    SizeBytes = f.Length
                };

                if (i <= Clips.Count) Clips.Insert(i, clip); else Clips.Add(clip);
                _ = EnrichAsync(clip);
            }

            foreach (var stale in Clips.Where(c => !seen.Contains(c.Path)).ToList())
                Clips.Remove(stale);

            Changed?.Invoke();
        });
    }

    public async Task ReEnrichAllAsync()
    {
        var snapshot = await _dispatcher.InvokeAsync(() => Clips.ToList());
        foreach (var clip in snapshot) _ = EnrichAsync(clip);
        await Task.CompletedTask;
    }

    public async Task EnrichAsync(Clip clip)
    {
        if (_disposed || !Ffmpeg.Available || !File.Exists(clip.Path)) return;

        await _work.WaitAsync();
        try
        {
            if (_disposed || !File.Exists(clip.Path)) return;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var duration = await Ffmpeg.ProbeDurationAsync(clip.Path);
                if (duration is { TotalSeconds: > 0.2 })
                {
                    await _dispatcher.InvokeAsync(() => clip.Duration = duration);
                    break;
                }
                await Task.Delay(1500);
            }

            var thumb = ThumbnailPathFor(clip.Path);
            if (!File.Exists(thumb))
            {
                var at = clip.Duration is { } d
                    ? TimeSpan.FromSeconds(Math.Min(d.TotalSeconds * 0.35, Math.Max(1, d.TotalSeconds - 0.5)))
                    : TimeSpan.FromSeconds(1);

                await Ffmpeg.ThumbnailAsync(clip.Path, thumb, at);
            }

            if (File.Exists(thumb))
                await _dispatcher.InvokeAsync(() => clip.ThumbnailPath = thumb);

            if (clip.Duration is { TotalSeconds: > 0 } known)
                Filmstrip.Preload(clip.Path, known.TotalSeconds);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            _work.Release();
        }
    }

    public static string ThumbnailPathFor(string videoPath)
    {
        var hash = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(videoPath.ToLowerInvariant())))[..16];
        return System.IO.Path.Combine(ThumbnailFolder, hash + ".jpg");
    }

    public static void InvalidateThumbnail(string videoPath)
    {
        try
        {
            var p = ThumbnailPathFor(videoPath);
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }

        Filmstrip.Invalidate(videoPath);
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce?.Stop();
        _watcher?.Dispose();
    }
}
