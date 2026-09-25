using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Loungepad.Models;

namespace Loungepad.Services;

public static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loungepad");

    /// <summary>Caches that should not roam: the WebView2 profiles and downloaded updates.</summary>
    public static string LocalDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Loungepad");

    public static string CoversDir { get; } = Path.Combine(DataDir, "covers");
    /// <summary>One folder per theme. Reachable from the page as https://loungepad.data/themes/…
    /// because DataDir is mapped as a virtual host, so a theme's own files need no further
    /// plumbing to load.</summary>
    public static string ThemesDir { get; } = Path.Combine(DataDir, "themes");

    /// <summary>
    /// WebView2's profile. Deliberately NOT under DataDir: WebView2 refuses to serve a virtual
    /// host mapping for a folder that contains its own user data folder, and DataDir is mapped
    /// as loungepad.data. With the profile inside it, every request to that host failed -- cover
    /// art included -- which looked like missing art rather than a broken mapping.
    ///
    /// It is a rebuildable cache, so living somewhere else costs nothing; Local is where a cache
    /// belongs anyway, and it keeps it out of a roaming profile.
    /// </summary>
    public static string WebViewDir { get; } = Path.Combine(LocalDir, "webview2");
    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
    public static string LibraryFile { get; } = Path.Combine(DataDir, "library.json");
    /// <summary>The last list of games the Steam account owned, so a start with no network keeps
    /// the uninstalled half of the library rather than losing it until the next fetch.</summary>
    public static string SteamOwnedFile { get; } = Path.Combine(DataDir, "steam-owned.json");
    /// <summary>The PC Game Pass catalogue as last fetched; it is a few dozen requests to
    /// rebuild and changes a handful of times a month.</summary>
    public static string GamePassFile { get; } = Path.Combine(DataDir, "gamepass.json");
    public static string LogFile { get; } = Path.Combine(DataDir, "loungepad.log");

    public static void EnsureCreated()
    {
        MigrateFromConsolify();
        MigrateFromCouchLauncher();
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CoversDir);
        Directory.CreateDirectory(ThemesDir);
    }

    private static bool _consolifyChecked;

    /// <summary>
    /// Up to 1.4 the app was Consolify, in %APPDATA%\Consolify and %LOCALAPPDATA%\Consolify.
    /// Once per process: EnsureCreated also runs on every save, from the scan thread among others.
    /// Must run before anything creates either new folder, or the move finds its destination
    /// already there and falls back to the slower copy. The Couch Launcher migration runs after
    /// this one, because its marker file is in the folder that this one moves.
    /// </summary>
    private static void MigrateFromConsolify()
    {
        if (_consolifyChecked) return;
        _consolifyChecked = true;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var moved = ConsolifyMigration.MoveFolder(Path.Combine(roaming, ConsolifyMigration.OldName), DataDir, copyFallback: true);
        // Local is caches and the stores' sign-in profiles. Worth moving, so nobody has to sign in
        // to GOG again, but never worth copying a quarter of a gigabyte of browser cache for.
        var movedLocal = ConsolifyMigration.MoveFolder(Path.Combine(local, ConsolifyMigration.OldName), LocalDir, copyFallback: false);
        ConsolifyMigration.RenameLog(DataDir, LogFile);

        if (moved is not null) Log.Info(moved);
        if (movedLocal is not null) Log.Info(movedLocal);
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

    /// <summary>
    /// Roll over at a megabyte, keeping one previous file.
    ///
    /// This only ever appended, so on a launcher that starts with Windows and runs all day the
    /// file grew without limit -- slowly, which is the kind that goes unnoticed until it is
    /// hundreds of megabytes. One megabyte is several thousand lines, far more than anything
    /// worth reading back, and keeping the previous file means a problem from before the roll is
    /// still there to look at.
    /// </summary>
    private const long MaxBytes = 1024 * 1024;

    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                Roll();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    private static void Roll()
    {
        try
        {
            var info = new FileInfo(Paths.LogFile);
            if (!info.Exists || info.Length < MaxBytes) return;
            File.Move(Paths.LogFile, Paths.LogFile + ".old", overwrite: true);
        }
        catch { /* a locked or missing file is not worth losing the line over */ }
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

            // A bundled theme that has been renamed keeps the user on it. Without this the id in
            // settings.json matches nothing, and someone who chose a theme is silently moved back
            // to the built-in look for no reason they can see.
            if (Settings.Theme is { Length: > 0 } theme
                && ThemeService.Renamed.TryGetValue(theme, out var renamed))
            {
                Settings.Theme = renamed;
                Log.Info($"Theme '{theme}' is now '{renamed}'");
            }
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
    /// <summary>Emulators and ROM folders live here rather than in settings.json: they describe
    /// the library, like collections do, and "Restore default settings" promises to leave the
    /// library alone.</summary>
    public List<EmulatorDef> Emulators { get; set; } = new();
    public List<RomFolderDef> RomFolders { get; set; } = new();
    /// <summary>What was removed by hand, so detection does not put it straight back. Paths,
    /// because that is what detection finds things by.</summary>
    public List<string> IgnoredEmulatorPaths { get; set; } = new();
    public List<string> IgnoredRomFolderPaths { get; set; } = new();
}

public class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _gate = new();

    public List<Game> Games { get; private set; } = new();
    public List<CollectionDef> Collections { get; private set; } = new();
    public List<EmulatorDef> Emulators { get; private set; } = new();
    public List<RomFolderDef> RomFolders { get; private set; } = new();
    public List<string> IgnoredEmulatorPaths { get; private set; } = new();
    public List<string> IgnoredRomFolderPaths { get; private set; } = new();

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
                    Emulators = data.Emulators ?? new();
                    RomFolders = data.RomFolders ?? new();
                    IgnoredEmulatorPaths = data.IgnoredEmulatorPaths ?? new();
                    IgnoredRomFolderPaths = data.IgnoredRomFolderPaths ?? new();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Library load failed, starting empty: {ex.Message}");
            Games = new List<Game>();
            Collections = new List<CollectionDef>();
            Emulators = new List<EmulatorDef>();
            RomFolders = new List<RomFolderDef>();
            IgnoredEmulatorPaths = new List<string>();
            IgnoredRomFolderPaths = new List<string>();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            Paths.EnsureCreated();
            var data = new LibraryFileData
            {
                Games = Games, Collections = Collections, Emulators = Emulators, RomFolders = RomFolders,
                IgnoredEmulatorPaths = IgnoredEmulatorPaths, IgnoredRomFolderPaths = IgnoredRomFolderPaths,
            };
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
                    s.PreferredEdition = old.PreferredEdition;
                    if (old.LastPlayed is not null && (s.LastPlayed is null || old.LastPlayed > s.LastPlayed))
                        s.LastPlayed = old.LastPlayed;
                    // Art: the scan only ever finds Steam's local half-size cache, so anything
                    // MetadataService already upgraded has to survive a rescan or every scan
                    // would undo it and the next enrich would download it all again.
                    s.CoverFile = KeepBest(s.CoverFile, old.CoverFile);
                    s.BannerFile = KeepBest(s.BannerFile, old.BannerFile);
                    s.HeroFile = KeepBest(s.HeroFile, old.HeroFile);
                    s.LogoFile = KeepBest(s.LogoFile, old.LogoFile);
                    // No KeepBest: a scan never finds a backdrop, so there is nothing to weigh it
                    // against. It was missing from this list entirely, which meant every scan
                    // dropped it and the next enrich had to fetch it again.
                    s.BackdropFile = old.BackdropFile;

                    // Fetched metadata is not discoverable from disk at all. EVERY fetched field
                    // has to be listed here -- the same trap as CopySettings, and it fails the
                    // same silent way: PegiRating was missing, so a rating would have been wiped
                    // by the next scan and the feature would have looked broken with nothing in
                    // the code to point at.
                    s.Description = old.Description;
                    s.Developer = old.Developer;
                    s.Publisher = old.Publisher;
                    s.Genres = old.Genres;
                    s.ReleaseDate = old.ReleaseDate;
                    s.CriticScore = old.CriticScore;
                    s.CriticSource = old.CriticSource;
                    s.PegiRating = old.PegiRating;
                    s.EsrbRating = old.EsrbRating;
                    s.ContentDescriptors = old.ContentDescriptors;
                    s.ControllerSupport = old.ControllerSupport;
                    s.MetadataSource = old.MetadataSource;
                    s.MetadataFetched = old.MetadataFetched;
                    // Without this the stamp resets to 0 on every scan, every game looks like it
                    // was filled in by an older build, and the whole library is re-fetched on
                    // every single start.
                    s.MetadataVersion = old.MetadataVersion;
                    // Just installed. The lite pass an uninstalled Steam game gets (see
                    // MetadataService) stops at the cover and the tile; clearing the stamp is
                    // what fetches the hero, the backdrop and the wordmark now that there is a
                    // detail page worth dressing. Placed after the copy above, or the copy would
                    // put the old stamp straight back.
                    if (s.Installed && !old.Installed) s.MetadataFetched = null;
                    // user overrides survive rescans
                    if (!string.IsNullOrWhiteSpace(old.Args)) s.Args = old.Args;
                    if (old.PreferDirectLaunch) { s.PreferDirectLaunch = true; s.ExePath = old.ExePath; }
                    // A ROM's title is guessed from its file name and a rescan guesses the same
                    // thing again, so one typed in by hand has to be carried across or "Rename"
                    // would undo itself on the next start. The per-game emulator is an override
                    // in the same sense as Args.
                    if (old.TitleEdited) { s.Title = old.Title; s.TitleEdited = true; }
                    if (old.EmulatorId is not null) s.EmulatorId = old.EmulatorId;
                }
                merged.Add(s);
            }

            merged.AddRange(manual);
            Games = merged;
        }
        Save();
    }

    // ---- Emulators and ROM folders ----

    public EmulatorDef? FindEmulator(string? id) =>
        id is null ? null : Emulators.FirstOrDefault(e => e.Id == id);

    public RomFolderDef? FindRomFolder(string? id) =>
        id is null ? null : RomFolders.FirstOrDefault(f => f.Id == id);

    /// <summary>The emulator this ROM starts with: its own choice if it has one, else its folder's.</summary>
    public EmulatorDef? EmulatorFor(Game game) =>
        FindEmulator(game.EmulatorId) ?? FindEmulator(FindRomFolder(game.RomFolderId)?.EmulatorId);

    // Adding by hand takes the path off the ignore list, and removing puts it on: "I removed it"
    // means "do not find it again", however it got there, and "I added it back" means the reverse.

    public void AddEmulator(EmulatorDef emulator)
    {
        lock (_gate)
        {
            Emulators.Add(emulator);
            IgnoredEmulatorPaths.RemoveAll(p => p.Equals(emulator.ExePath, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    /// <summary>Removes the emulator and un-assigns it from every folder and game that named it,
    /// so nothing is left pointing at an id that no longer exists.</summary>
    public void RemoveEmulator(string id)
    {
        lock (_gate)
        {
            foreach (var e in Emulators.Where(e => e.Id == id))
                if (!IgnoredEmulatorPaths.Contains(e.ExePath, StringComparer.OrdinalIgnoreCase))
                    IgnoredEmulatorPaths.Add(e.ExePath);
            Emulators.RemoveAll(e => e.Id == id);
            foreach (var f in RomFolders) if (f.EmulatorId == id) f.EmulatorId = null;
            foreach (var g in Games) if (g.EmulatorId == id) g.EmulatorId = null;
        }
        Save();
    }

    public void AddRomFolder(RomFolderDef folder)
    {
        lock (_gate)
        {
            RomFolders.Add(folder);
            IgnoredRomFolderPaths.RemoveAll(p => p.Equals(folder.Path, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    /// <summary>The folder's games go with it on the next scan, which only keeps what was scanned.</summary>
    public void RemoveRomFolder(string id)
    {
        lock (_gate)
        {
            foreach (var f in RomFolders.Where(f => f.Id == id))
                if (!IgnoredRomFolderPaths.Contains(f.Path, StringComparer.OrdinalIgnoreCase))
                    IgnoredRomFolderPaths.Add(f.Path);
            RomFolders.RemoveAll(f => f.Id == id);
        }
        Save();
    }

    /// <summary>
    /// Picks between the art a fresh scan found and the art already on record. Downloaded art wins
    /// over anything the scan found locally -- it is the full-resolution version of the same
    /// picture, or a shape the local cache does not hold at all -- and a user's hand-picked cover
    /// wins over both, which is what the "custom_" prefix marks.
    /// </summary>
    /// <summary>
    /// Art a scan may replace, and art it may not.
    ///
    /// The scan only ever finds Steam's local cache, which is half size and whatever shape the
    /// client happened to store -- so anything MetadataService downloaded or the user chose has
    /// to outlast a rescan, or every start would throw the good art away and the library would
    /// visibly change between sessions.
    ///
    /// The markers below ARE the naming scheme, so they have to move with it. When the fetched
    /// names changed from "_hd*" to "_st_*"/"_sv_*" this test went on matching the old one, which
    /// meant it stopped recognising fetched art at all and every scan quietly reverted the whole
    /// library to the local cache. "_hd" stays in the list for installs that have not re-fetched
    /// yet; it costs nothing and its absence would cost them their art for one pass.
    /// </summary>
    private static readonly string[] FetchedMarkers = { "_st_", "_sv_", "_pf_", "_hd" };

    private static string? KeepBest(string? scanned, string? existing)
    {
        if (existing is null) return scanned;
        if (scanned is null) return existing;
        var kept = existing.StartsWith("custom_", StringComparison.Ordinal)
                   || FetchedMarkers.Any(m => existing.Contains(m, StringComparison.Ordinal));
        return kept ? existing : scanned;
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
