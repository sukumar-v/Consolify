using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Consolify.Models;
using Microsoft.Win32;

namespace Consolify.Services;

/// <summary>
/// Scans Steam (appmanifest ACF files), Epic (launcher .item manifests) and GOG (registry)
/// for installed games. Manual entries are managed separately by LibraryStore, and the games a
/// Steam account owns without having installed come from SteamAccountService and are turned into
/// entries by <see cref="OwnedSteamGames"/>.
/// </summary>
public class LibraryScanner
{
    private static readonly string[] SteamJunkNames =
    {
        "Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "SteamVR"
    };

    public List<Game> ScanAll()
    {
        var games = new List<Game>();
        try { games.AddRange(ScanSteam()); } catch (Exception ex) { Log.Info($"Steam scan failed: {ex.Message}"); }
        try { games.AddRange(ScanEpic()); } catch (Exception ex) { Log.Info($"Epic scan failed: {ex.Message}"); }
        try { games.AddRange(ScanGog()); } catch (Exception ex) { Log.Info($"GOG scan failed: {ex.Message}"); }
        try { games.AddRange(ScanXbox()); } catch (Exception ex) { Log.Info($"Xbox scan failed: {ex.Message}"); }
        return games;
    }

    // ---------- Emulated games ----------

    /// <summary>
    /// One entry per ROM file in each configured folder. Nothing is guessed about the system: the
    /// folder was set up for one, and every file in it with one of that system's extensions is a
    /// game for it. The id is a hash of the file's full path, so it survives a rescan -- playtime,
    /// favourites and hand-picked art all key on it -- and moves with nothing but the file.
    /// </summary>
    public List<Game> ScanEmulated(IReadOnlyList<RomFolderDef> folders)
    {
        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            try
            {
                foreach (var g in ScanRomFolder(folder))
                    // The same file reached through two overlapping folders is one game. A
                    // duplicate id would break the merge's dictionary on the next scan.
                    if (seen.Add(g.Id)) games.Add(g);
            }
            catch (Exception ex) { Log.Info($"ROM folder {folder.Path} skipped: {ex.Message}"); }
        }
        return games;
    }

    private static List<Game> ScanRomFolder(RomFolderDef folder)
    {
        var games = new List<Game>();
        var platform = EmulatedPlatforms.Find(folder.PlatformId);
        if (platform is null) { Log.Info($"ROM folder {folder.Path}: unknown platform '{folder.PlatformId}'"); return games; }
        if (folder.Playlist) return ScanPlaylist(folder, platform);
        if (!Directory.Exists(folder.Path)) { Log.Info($"ROM folder {folder.Path} is not there"); return games; }

        IEnumerable<string> extList = folder.Extensions is { Count: > 0 } own ? own : platform.Extensions;
        var exts = extList
            .Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = folder.Recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = Directory.EnumerateFiles(folder.Path, "*", options).ToList();

        // A multi-disc game is one game. Its .m3u lists the discs, and a .cue names its tracks,
        // so anything either of them refers to is a part of a game already in the list, not a
        // game of its own.
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase)) AddPlaylistParts(file, parts);
            else if (ext.Equals(".cue", StringComparison.OrdinalIgnoreCase)) AddCueParts(file, parts);
        }

        foreach (var file in files)
        {
            if (!exts.Contains(Path.GetExtension(file))) continue;
            if (parts.Contains(file)) continue;
            long size = 0;
            try { size = new FileInfo(file).Length; } catch { /* cosmetic */ }
            games.Add(new Game
            {
                Id = "rom:" + PathHash(file),
                Title = RomTitles.FromFileName(file),
                Platform = platform.Name,
                PlatformId = platform.Id,
                Emulated = true,
                RomPath = file,
                RomFolderId = folder.Id,
                InstallDir = Path.GetDirectoryName(file),
                SizeBytes = size,
                Installed = true,
            });
        }
        return games;
    }

    /// <summary>
    /// A RetroArch playlist's entries as games. The database label is the title where there is
    /// one -- it is the No-Intro name, cleaned the same way a file name is -- and an entry whose
    /// file has gone is left out rather than shown as a tile that cannot start.
    /// </summary>
    private static List<Game> ScanPlaylist(RomFolderDef folder, EmulatedPlatforms.Def platform)
    {
        var games = new List<Game>();
        var playlist = RetroArchPlaylists.Read(folder.Path);
        if (playlist is null) return games;
        foreach (var entry in playlist.Entries)
        {
            if (!File.Exists(entry.RomPath)) continue;
            long size = 0;
            try { size = new FileInfo(entry.RomPath).Length; } catch { /* cosmetic */ }
            games.Add(new Game
            {
                Id = "rom:" + PathHash(entry.RomPath),
                Title = entry.Label.Length > 0 ? RomTitles.FromLabel(entry.Label) : RomTitles.FromFileName(entry.RomPath),
                Platform = platform.Name,
                PlatformId = platform.Id,
                Emulated = true,
                RomPath = entry.RomPath,
                RomFolderId = folder.Id,
                InstallDir = Path.GetDirectoryName(entry.RomPath),
                SizeBytes = size,
                Installed = true,
            });
        }
        return games;
    }

    private static void AddPlaylistParts(string m3u, HashSet<string> parts)
    {
        try
        {
            var dir = Path.GetDirectoryName(m3u) ?? "";
            foreach (var raw in File.ReadLines(m3u))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                parts.Add(Path.GetFullPath(Path.Combine(dir, line)));
            }
        }
        catch { /* an unreadable playlist just means its discs are listed separately */ }
    }

    private static void AddCueParts(string cue, HashSet<string> parts)
    {
        try
        {
            var dir = Path.GetDirectoryName(cue) ?? "";
            foreach (Match m in Regex.Matches(File.ReadAllText(cue), "^\\s*FILE\\s+\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                parts.Add(Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value)));
        }
        catch { /* same as above */ }
    }

    /// <summary>Sixteen hex characters of the path's SHA-1, lower-cased first so a drive letter
    /// typed either way is the same game. Plenty for a library; a collision would need two ROM
    /// files whose paths hash alike in 64 bits.</summary>
    private static string PathHash(string path)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    // ---------- Xbox / Microsoft Store ----------

    /// <summary>
    /// Xbox app titles install to &lt;drive&gt;\XboxGames\&lt;Title&gt;\Content with a MicrosoftGame.config
    /// manifest. That folder is readable (unlike WindowsApps), so we scan it directly and then look
    /// the package up in the AppModel repository to build a "shell:AppsFolder\PFN!AppId" launch URI.
    /// </summary>
    public List<Game> ScanXbox()
    {
        var games = new List<Game>();
        var packages = ReadAppModelPackages();

        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                if (!drive.IsReady) continue;
                root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(root)) continue;
            }
            catch { continue; }

            foreach (var dir in SafeEnumerateDirectories(root))
            {
                try
                {
                    var content = Path.Combine(dir, "Content");
                    var config = Path.Combine(content, "MicrosoftGame.config");
                    if (!File.Exists(config)) continue;

                    var doc = System.Xml.Linq.XDocument.Load(config);
                    var game = doc.Root;
                    if (game is null) continue;

                    var identity = game.Element("Identity")?.Attribute("Name")?.Value;
                    var visuals = game.Element("ShellVisuals");
                    var title = visuals?.Attribute("DefaultDisplayName")?.Value
                                ?? Path.GetFileName(dir);
                    var exeEl = game.Element("ExecutableList")?.Elements("Executable").FirstOrDefault();
                    var exeName = exeEl?.Attribute("Name")?.Value;
                    var appId = exeEl?.Attribute("Id")?.Value ?? "Game";
                    if (identity is null) continue;

                    // PackageFullName -> PackageFamilyName is "<name>_<publisherId>"
                    string? launchUri = null;
                    string? pfn = null;
                    if (packages.TryGetValue(identity, out var fullName))
                    {
                        var parts = fullName.Split('_');
                        if (parts.Length >= 2)
                        {
                            pfn = $"{parts[0]}_{parts[^1]}";
                            launchUri = $"shell:AppsFolder\\{pfn}!{appId}";
                        }
                    }

                    var exePath = exeName is not null ? Path.Combine(content, exeName) : null;
                    long size = 0;
                    try { size = new DirectoryInfo(content).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
                    catch { /* size is cosmetic */ }

                    games.Add(new Game
                    {
                        Id = $"xbox:{identity}",
                        Title = title,
                        Platform = "Xbox",
                        LaunchUri = launchUri,
                        ExePath = exePath,
                        InstallDir = content,
                        SizeBytes = size,
                        Installed = true,
                        PackageFamilyName = pfn,
                        CoverFile = ImportXboxArt(content, visuals, identity)
                    });
                }
                catch (Exception ex) { Log.Info($"Xbox entry {dir} skipped: {ex.Message}"); }
            }
        }
        return games;
    }

    /// <summary>Maps package Identity name -> PackageFullName from the AppModel repository.</summary>
    private static Dictionary<string, string> ReadAppModelPackages()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (key is null) return map;
            foreach (var fullName in key.GetSubKeyNames())
            {
                var name = fullName.Split('_').FirstOrDefault();
                if (string.IsNullOrEmpty(name)) continue;
                // Prefer the first/highest entry; duplicates are per-version leftovers.
                map.TryAdd(name, fullName);
            }
        }
        catch (Exception ex) { Log.Info($"AppModel package enumeration failed: {ex.Message}"); }
        return map;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static string? ImportXboxArt(string content, System.Xml.Linq.XElement? visuals, string identity)
    {
        if (visuals is null) return null;
        foreach (var attr in new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo", "Square44x44Logo" })
        {
            var rel = visuals.Attribute(attr)?.Value;
            if (string.IsNullOrWhiteSpace(rel)) continue;
            try
            {
                var src = Path.Combine(content, rel.Replace('/', '\\'));
                if (!File.Exists(src)) continue;
                var safe = identity.Replace('.', '_');
                var dest = Path.Combine(Paths.CoversDir, $"xbox_{safe}{Path.GetExtension(src)}");
                if (!File.Exists(dest) || new FileInfo(src).LastWriteTimeUtc > new FileInfo(dest).LastWriteTimeUtc)
                    File.Copy(src, dest, overwrite: true);
                return Path.GetFileName(dest);
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    // ---------- Steam ----------

    /// <summary>
    /// Every steamapps folder Steam knows about: the install's own, plus each extra library
    /// folder listed in libraryfolders.vdf. Shared with the manifest watcher in UiBridge, so the
    /// folders it watches are exactly the folders the scan reads.
    /// </summary>
    public static List<string> SteamLibraryRoots()
    {
        var roots = new List<string>();
        var steamPath = SteamAccountService.SteamPath();
        if (steamPath is null) return roots;

        var main = Path.Combine(steamPath, "steamapps");
        if (Directory.Exists(main)) roots.Add(main);
        var vdf = Path.Combine(main, "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                var p = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps");
                if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                    roots.Add(p);
            }
        }
        return roots;
    }

    public List<Game> ScanSteam()
    {
        var games = new List<Game>();
        var steamPath = SteamAccountService.SteamPath();
        if (steamPath is null) return games;

        foreach (var root in SteamLibraryRoots())
        {
            foreach (var acf in Directory.EnumerateFiles(root, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                string? Get(string key) => Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"").Groups[1].Value is { Length: > 0 } v ? v : null;

                var appId = Get("appid");
                var name = Get("name");
                var installDirName = Get("installdir");
                if (appId is null || name is null || installDirName is null) continue;
                if (SteamJunkNames.Any(j => name.Contains(j, StringComparison.OrdinalIgnoreCase))) continue;

                var installDir = Path.Combine(root, "common", installDirName);
                long.TryParse(Get("SizeOnDisk"), out var size);
                DateTime? lastPlayed = null;
                if (long.TryParse(Get("LastPlayed"), out var lp) && lp > 0)
                    lastPlayed = DateTimeOffset.FromUnixTimeSeconds(lp).LocalDateTime;

                // StateFlags bit 4 is "fully installed". A download Steam has only just started
                // already has a manifest and a folder, so the folder alone showed it as installed
                // for the whole download -- a tile you could press A on and watch nothing happen.
                // A manifest with no flags at all is taken at its folder's word.
                int.TryParse(Get("StateFlags"), out var flags);
                var installed = Directory.Exists(installDir) && (flags == 0 || (flags & 4) != 0);

                var art = ImportSteamArt(steamPath, appId);
                games.Add(new Game
                {
                    Id = $"steam:{appId}",
                    Title = name,
                    Platform = "Steam",
                    LaunchUri = $"steam://rungameid/{appId}",
                    InstallDir = installDir,
                    SizeBytes = size,
                    LastPlayed = lastPlayed,
                    Installed = installed,
                    CoverFile = art.Cover,
                    BannerFile = art.Banner,
                    HeroFile = art.Hero,
                    LogoFile = art.Logo
                });
            }
        }
        return games;
    }

    /// <summary>
    /// Library entries for the games an account owns but has not installed. Anything the manifest
    /// scan already found is skipped -- that entry knows the install folder and the size, and this
    /// one knows neither -- so the two lists merge by plain concatenation. Art is whatever the
    /// Steam client has cached locally; MetadataService fetches the rest by app id exactly as it
    /// does for an installed game, so an uninstalled tile ends up looking like any other.
    /// </summary>
    public List<Game> OwnedSteamGames(IEnumerable<OwnedGame> owned, IEnumerable<Game> alreadyFound)
    {
        var games = new List<Game>();
        var have = alreadyFound.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var steamPath = SteamAccountService.SteamPath();
        foreach (var o in owned)
        {
            var id = $"steam:{o.AppId}";
            if (!have.Add(id)) continue;
            if (SteamJunkNames.Any(j => o.Name.Contains(j, StringComparison.OrdinalIgnoreCase))) continue;

            var art = steamPath is null ? default : ImportSteamArt(steamPath, o.AppId);
            games.Add(new Game
            {
                Id = id,
                Title = o.Name,
                Platform = "Steam",
                LaunchUri = $"steam://rungameid/{o.AppId}",
                InstallUri = $"steam://install/{o.AppId}",
                LastPlayed = o.LastPlayed,
                Installed = false,
                CoverFile = art.Cover,
                BannerFile = art.Banner,
                HeroFile = art.Hero,
                LogoFile = art.Logo
            });
        }
        return games;
    }

    /// <summary>
    /// The owned entries that are not already in the library as something installed. Three things
    /// make two entries the same game: the id (Epic's app name and GOG's product id are the same
    /// on disk and in Galaxy), the package family name (an installed Xbox game against a
    /// catalogue entry), and failing both, an exact title on the same platform -- Galaxy's Xbox
    /// entries carry neither of the first two. The installed entry always wins: it is the one
    /// that can be launched.
    ///
    /// Owned entries are de-duplicated against each other by id and by package family name --
    /// the Xbox title history and the Game Pass catalogue list the same game under different ids
    /// -- but never by title. Two owned games with the same title are a real thing (Steam sells
    /// two named exactly "DOOM"), and dropping one by title would lose a game the account paid for.
    /// </summary>
    public static List<Game> NotAlreadyFound(IEnumerable<Game> found, IEnumerable<Game> owned)
    {
        var have = found.ToList();
        var ids = have.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var pfns = have.Where(g => g.PackageFamilyName is not null)
            .Select(g => g.PackageFamilyName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titles = have.Select(g => (g.Platform, TitleMatch.Normalise(g.Title))).ToHashSet();

        var fresh = new List<Game>();
        foreach (var o in owned)
        {
            if (!ids.Add(o.Id)) continue;
            if (o.PackageFamilyName is not null && !pfns.Add(o.PackageFamilyName)) continue;
            if (titles.Contains((o.Platform, TitleMatch.Normalise(o.Title)))) continue;
            fresh.Add(o);
        }
        return fresh;
    }

    /// <summary>
    /// Copy Steam's cached art into our covers dir. Four different shapes, because they are not
    /// interchangeable:
    ///
    ///   library_600x900 / library_capsule  portrait, for a portrait grid tile
    ///   header / library_header (460x215)  ~2:1 with the logo burnt in -- the closest thing
    ///                                      Steam caches to a landscape tile
    ///   library_hero (1920x620)            a ~3:1 backdrop; the subject sits off-centre with
    ///                                      empty space either side
    ///   logo (transparent wordmark)        for a theme that draws the title as art
    ///
    /// The hero used to be taken as the banner, which is why landscape tiles looked like they had
    /// the wrong game's art: cropping 3.1:1 down to 16:9 throws away 43% of the width, and what
    /// survives is whichever piece of background happened to be in the middle.
    ///
    /// Two cache layouts exist: legacy flat files ("&lt;appid&gt;_library_600x900.jpg") and the
    /// newer per-app folder holding named art. Both hold half-size copies -- the cached capsule is
    /// 300x450, not 600x900 -- so MetadataService replaces these with full-resolution art from
    /// Steam's CDN when it can. These are the offline fallback and the first paint.
    /// </summary>
    private static SteamArt ImportSteamArt(string steamPath, string appId)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        var perApp = Path.Combine(cache, appId);

        string? FindArt(string[] flatNames, string[] nestedNames)
        {
            var flat = flatNames.Select(f => Path.Combine(cache, f)).FirstOrDefault(File.Exists);
            if (flat is not null) return flat;
            if (!Directory.Exists(perApp)) return null;
            foreach (var name in nestedNames)
            {
                var hit = Directory.EnumerateFiles(perApp, name, SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
            return null;
        }

        string? Import(string? src, string suffix)
        {
            if (src is null) return null;
            try
            {
                var dest = Path.Combine(Paths.CoversDir, $"steam_{appId}{suffix}{Path.GetExtension(src)}");
                if (!File.Exists(dest) || new FileInfo(src).LastWriteTimeUtc > new FileInfo(dest).LastWriteTimeUtc)
                    File.Copy(src, dest, overwrite: true);
                return Path.GetFileName(dest);
            }
            catch { return null; }
        }

        var cover = Import(FindArt(
            new[] { $"{appId}_library_600x900.jpg" },
            new[] { "library_600x900.jpg", "library_capsule.jpg" }), "");
        var banner = Import(FindArt(
            new[] { $"{appId}_header.jpg" },
            new[] { "library_header.jpg", "header.jpg" }), "_wide");
        var hero = Import(FindArt(
            Array.Empty<string>(),
            new[] { "library_hero.jpg" }), "_hero");
        var logo = Import(FindArt(
            new[] { $"{appId}_logo.png" },
            new[] { "logo.png" }), "_logo");

        // A portrait tile can live with the header letterboxed; a landscape tile cannot live with
        // the portrait cover, so the banner never falls back the other way.
        return new SteamArt(cover ?? banner, banner, hero, logo);
    }

    private readonly record struct SteamArt(string? Cover, string? Banner, string? Hero, string? Logo);

    // ---------- Epic ----------

    public List<Game> ScanEpic()
    {
        var games = new List<Game>();
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) return games;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var name = S("DisplayName");
                var appName = S("AppName");
                var installLocation = S("InstallLocation");
                var launchExe = S("LaunchExecutable");
                if (name is null || appName is null || installLocation is null) continue;
                // Skip DLC / non-game entries that have no executable
                if (string.IsNullOrWhiteSpace(launchExe)) continue;
                if (!IsEpicGame(r)) continue;

                long size = 0;
                if (r.TryGetProperty("InstallSize", out var sz) && sz.ValueKind == JsonValueKind.Number)
                    size = sz.GetInt64();

                games.Add(new Game
                {
                    Id = $"epic:{appName}",
                    Title = name,
                    Platform = "Epic",
                    LaunchUri = $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true",
                    ExePath = Path.Combine(installLocation, launchExe),
                    InstallDir = installLocation,
                    SizeBytes = size,
                    Installed = Directory.Exists(installLocation)
                });
            }
            catch (Exception ex) { Log.Info($"Epic manifest {file} skipped: {ex.Message}"); }
        }
        return games;
    }

    /// <summary>
    /// Epic's manifest folder holds more than games: Unreal Engine installs, Quixel Bridge, Fab
    /// plugins. The engine ones even have a launch executable (UnrealEditor.exe), so the "has an
    /// exe" test alone lets four copies of "Unreal Engine" into the library.
    ///
    /// Epic labels them itself, in AppCategories -- a game carries "games", an engine carries
    /// "engines" -- so this reads that rather than pattern-matching the display name, which would
    /// throw away a game legitimately called something with "engine" in it.
    ///
    /// An entry with no categories at all is kept: it has already passed the executable test, and
    /// missing metadata is a weaker signal than a label that actively says "engine".
    /// </summary>
    private static bool IsEpicGame(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("AppCategories", out var cats) || cats.ValueKind != JsonValueKind.Array)
            return true;

        var categories = cats.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString()!)
            .ToList();
        if (categories.Count == 0) return true;

        return categories.Any(c => c.Equals("games", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- GOG ----------

    public List<Game> ScanGog()
    {
        var games = new List<Game>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                       ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (root is null) return games;

        foreach (var idKey in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(idKey);
            if (k is null) continue;

            var name = k.GetValue("gameName") as string;
            var exe = k.GetValue("exe") as string;
            var path = k.GetValue("path") as string;
            if (name is null || exe is null) continue;
            if (k.GetValue("dependsOn") is string dep && dep.Length > 0) continue; // DLC

            long size = 0;
            try
            {
                if (path is not null && Directory.Exists(path))
                    size = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            catch { /* size is cosmetic */ }

            games.Add(new Game
            {
                Id = $"gog:{idKey}",
                Title = name,
                Platform = "GOG",
                ExePath = exe,
                Args = k.GetValue("launchParam") as string,
                InstallDir = path,
                SizeBytes = size,
                Installed = File.Exists(exe)
            });
        }
        return games;
    }
}
