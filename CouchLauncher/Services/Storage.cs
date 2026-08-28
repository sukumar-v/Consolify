using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CouchLauncher.Models;

namespace CouchLauncher.Services;

public static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchLauncher");

    public static string CoversDir { get; } = Path.Combine(DataDir, "covers");
    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
    public static string LibraryFile { get; } = Path.Combine(DataDir, "library.json");
    public static string LogFile { get; } = Path.Combine(DataDir, "couchlauncher.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CoversDir);
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
    }
}

public class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Info($"Settings load failed, using defaults: {ex.Message}");
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        Paths.EnsureCreated();
        File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(Settings, JsonOpts));
    }
}

public class CollectionDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> GameIds { get; set; } = new();
}

public class LibraryFileData
{
    public List<Game> Games { get; set; } = new();
    public List<CollectionDef> Collections { get; set; } = new();
}

public class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _gate = new();

    public List<Game> Games { get; private set; } = new();
    public List<CollectionDef> Collections { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(Paths.LibraryFile))
            {
                var text = File.ReadAllText(Paths.LibraryFile);
                if (text.TrimStart().StartsWith('['))
                {
                    // pre-collections format: a bare game array
                    Games = JsonSerializer.Deserialize<List<Game>>(text) ?? new List<Game>();
                }
                else
                {
                    var data = JsonSerializer.Deserialize<LibraryFileData>(text) ?? new LibraryFileData();
                    Games = data.Games;
                    Collections = data.Collections;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Library load failed, starting empty: {ex.Message}");
            Games = new List<Game>();
            Collections = new List<CollectionDef>();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            Paths.EnsureCreated();
            var data = new LibraryFileData { Games = Games, Collections = Collections };
            File.WriteAllText(Paths.LibraryFile, JsonSerializer.Serialize(data, JsonOpts));
        }
    }

    /// <summary>Merge freshly scanned games, preserving locally tracked playtime/session data.</summary>
    public void MergeScanned(IEnumerable<Game> scanned)
    {
        lock (_gate)
        {
            var manual = Games.Where(g => g.Manual).ToList();
            var byId = Games.ToDictionary(g => g.Id);
            var merged = new List<Game>();

            foreach (var s in scanned)
            {
                if (byId.TryGetValue(s.Id, out var old))
                {
                    s.PlaytimeMinutes = old.PlaytimeMinutes;
                    s.Sessions = old.Sessions;
                    s.Favorite = old.Favorite;
                    s.Hidden = old.Hidden;
                    if (old.LastPlayed is not null && (s.LastPlayed is null || old.LastPlayed > s.LastPlayed))
                        s.LastPlayed = old.LastPlayed;
                    if (s.CoverFile is null) s.CoverFile = old.CoverFile;
                    if (s.BannerFile is null) s.BannerFile = old.BannerFile;
                    // user overrides survive rescans
                    if (!string.IsNullOrWhiteSpace(old.Args)) s.Args = old.Args;
                    if (old.PreferDirectLaunch) { s.PreferDirectLaunch = true; s.ExePath = old.ExePath; }
                }
                merged.Add(s);
            }

            merged.AddRange(manual);
            Games = merged;
        }
        Save();
    }

    public Game? Find(string id)
    {
        lock (_gate) return Games.FirstOrDefault(g => g.Id == id);
    }

    public void AddManual(Game game)
    {
        lock (_gate) Games.Add(game);
        Save();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            Games.RemoveAll(g => g.Id == id);
            foreach (var c in Collections) c.GameIds.Remove(id);
        }
        Save();
    }

    public string CreateCollection(string name)
    {
        var col = new CollectionDef { Id = Guid.NewGuid().ToString("N"), Name = name };
        lock (_gate) Collections.Add(col);
        Save();
        return col.Id;
    }

    public void DeleteCollection(string id)
    {
        lock (_gate) Collections.RemoveAll(c => c.Id == id);
        Save();
    }

    /// <summary>Returns true if the game is now in the collection.</summary>
    public bool ToggleInCollection(string collectionId, string gameId)
    {
        bool added = false;
        lock (_gate)
        {
            var col = Collections.FirstOrDefault(c => c.Id == collectionId);
            if (col is null) return false;
            if (!col.GameIds.Remove(gameId)) { col.GameIds.Add(gameId); added = true; }
        }
        Save();
        return added;
    }
}
