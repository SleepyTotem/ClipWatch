using System.IO;

namespace ClipWatch;

public static class ClipOps
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static string Sanitize(string name)
    {
        var cleaned = new string(name.Where(c => !Invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        var stem = cleaned.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            cleaned = "_" + cleaned;
        return cleaned.Length == 0 ? "clip" : cleaned;
    }

    public static string? Rename(Clip clip, string newName)
    {
        try
        {
            var target = Path.Combine(clip.Folder, Sanitize(newName) + clip.Extension);
            if (string.Equals(target, clip.Path, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(target)) return "A clip with that name already exists.";

            ClipLibrary.InvalidateThumbnail(clip.Path);
            using (Ffmpeg.ReleaseFile(clip.Path))
                MoveWithRetry(clip.Path, target);
            clip.Path = target;
            clip.DisplayName = Path.GetFileNameWithoutExtension(target);
            return null;
        }
        catch (IOException)
        {
            return "The file is in use. Close anything playing it and try again.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string? Delete(Clip clip)
    {
        try
        {
            ClipLibrary.InvalidateThumbnail(clip.Path);
            using var hold = Ffmpeg.ReleaseFile(clip.Path);

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    clip.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(clip.Path);
            }

            return null;
        }
        catch (IOException)
        {
            return "The file is in use. Close anything playing it and try again.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // The video player lets go of a file a moment after it's stopped, so a move straight
    // after closing playback can briefly fail with "in use".
    public static void MoveWithRetry(string source, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, target);
                return;
            }
            catch (IOException) when (attempt < 10 && File.Exists(source) && !File.Exists(target))
            {
                Thread.Sleep(100);
            }
        }
    }

    public static string UniquePath(string folder, string baseName, string extension)
    {
        var candidate = Path.Combine(folder, baseName + extension);
        var n = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{baseName} ({n++}){extension}");
        return candidate;
    }
}
