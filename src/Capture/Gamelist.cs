using System.IO;
using System.Text.Json;

namespace ClipWatch;

public sealed class GameList
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public static string FilePath => Path.Combine(Config.Directory, "games.json");

    public GameList() => Load();

    public bool Contains(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        lock (_gate) return _names.Contains(Normalize(processName));
    }

    public bool Toggle(string processName)
    {
        var name = Normalize(processName);
        bool added;

        lock (_gate)
        {
            added = _names.Add(name);
            if (!added) _names.Remove(name);
        }

        Save();
        return added;
    }

    public IReadOnlyCollection<string> All()
    {
        lock (_gate) return _names.ToArray();
    }

    private static string Normalize(string name) =>
        (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).Trim();

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var parsed = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
            if (parsed == null) return;

            lock (_gate)
            {
                foreach (var n in parsed)
                    if (!string.IsNullOrWhiteSpace(n))
                        _names.Add(Normalize(n));
            }
        }
        catch
        {
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.Directory);
            string[] snapshot;
            lock (_gate) snapshot = _names.OrderBy(n => n).ToArray();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, Options));
        }
        catch
        {
        }
    }
}
