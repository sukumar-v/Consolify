using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Consolify.Models;

namespace Consolify.Services;

public static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Consolify");

    public static string CoversDir { get; } = Path.Combine(DataDir, "covers");
    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
    public static string LibraryFile { get; } = Path.Combine(DataDir, "library.json");
    public static string LogFile { get; } = Path.Combine(DataDir, "consolify.log");

    public static void EnsureCreated()
    {
        MigrateFromCouchLauncher();
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CoversDir);
    }

    /// <summary>
    /// The app used to be called Couch Launcher and kept everything in %APPDATA%\CouchLauncher.
    /// Renaming without this would orphan an existing library, its cover art and every setting.
    ///
    /// Copies rather than moves, and leaves the old folder alone. Moving the whole directory is
    /// all-or-nothing and races anything still holding a handle in there -- an old build left
    /// running will keep writing to it, and a half-finished move can leave the library in one
    /// folder and the settings in another. The WebView2 profile is skipped deliberately: it is a
    /// rebuildable cache, it is the part that holds locks, and it is most of the bytes.
    ///
    /// A marker file makes this run exactly once, so a rescan that happened before the migration
    /// completed is still replaced by the real library, and later starts never touch it again.
    /// </summary>
    private static void MigrateFromCouchLauncher()
    {
        var old = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchLauncher");
        var marker = Path.Combine(DataDir, ".migrated-from-couchlauncher");
        if (!Directory.Exists(old) || File.Exists(marker)) return;

        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(CoversDir);

            foreach (var name in new[] { "settings.json", "library.json" })
            {
                var src = Path.Combine(old, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(DataDir, name), overwrite: true);
            }

            int covers = 0;
            var oldCovers = Path.Combine(old, "covers");
            if (Directory.Exists(oldCovers))
                foreach (var f in Directory.GetFiles(oldCovers))
                {
                    var dst = Path.Combine(CoversDir, Path.GetFileName(f));
                    if (File.Exists(dst)) continue;
                    File.Copy(f, dst);
                    covers++;
                }

            // Carry the history over, but only into an empty log, so a re-run cannot duplicate it.
            var oldLog = Path.Combine(old, "couchlauncher.log");
            if (File.Exists(oldLog) && !File.Exists(LogFile)) File.Copy(oldLog, LogFile);

            File.WriteAllText(marker, DateTime.Now.ToString("O"));
            Log.Info($"Migrated settings, library and {covers} cover(s) from {old} (left in place)");
        }
        catch (Exception ex)
        {
            // Never block startup on this. Without the marker it simply tries again next time.
            try { Log.Info($"Could not migrate from {old}: {ex.Message}"); } catch { }
        }
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

    /// <summary>"#RRGGBB", and nothing else.</summary>
    public static bool IsHexColor(string? value) =>
        value is not null && value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    public void Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();

            // The accent reaches the UI as a CSS value, so a hand-edited file must not be able to
            // put anything but a hex colour there. Checked on the way in as well as on save,
            // because a file edited by hand never passes through the save path at all.
            if (!IsHexColor(Settings.AccentColor)) Settings.AccentColor = new AppSettings().AccentColor;
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
