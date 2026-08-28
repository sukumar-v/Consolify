using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchLauncher.Models;
using Microsoft.Win32;

namespace CouchLauncher.Services;

/// <summary>
/// Scans Steam (appmanifest ACF files), Epic (launcher .item manifests) and GOG (registry)
/// for installed games. Manual entries are managed separately by LibraryStore.
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
        return games;
    }

    // ---------- Steam ----------

    public List<Game> ScanSteam()
    {
        var games = new List<Game>();
        var steamPath = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)
                        ?.Replace('/', '\\');
        if (steamPath is null || !Directory.Exists(steamPath)) return games;

        // All library folders (libraryfolders.vdf lists additional drives)
        var libraryRoots = new List<string> { Path.Combine(steamPath, "steamapps") };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                var p = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps");
                if (Directory.Exists(p) && !libraryRoots.Contains(p, StringComparer.OrdinalIgnoreCase))
                    libraryRoots.Add(p);
            }
        }

        foreach (var root in libraryRoots)
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

                var (cover, banner) = ImportSteamArt(steamPath, appId);
                games.Add(new Game
                {
                    Id = $"steam:{appId}",
                    Title = name,
                    Platform = "Steam",
                    LaunchUri = $"steam://rungameid/{appId}",
                    InstallDir = installDir,
                    SizeBytes = size,
                    LastPlayed = lastPlayed,
                    Installed = Directory.Exists(installDir),
                    CoverFile = cover,
                    BannerFile = banner
                });
            }
        }
        return games;
    }

    /// <summary>
    /// Copy Steam's cached art into our covers dir: portrait for grid tiles, landscape for the
    /// continue row / detail hero. Two cache layouts exist: legacy flat files
    /// ("<appid>_library_600x900.jpg") and the newer per-app folder whose hashed subfolders hold
    /// named art (library_capsule.jpg, library_header.jpg, ...).
    /// </summary>
    private static (string? cover, string? banner) ImportSteamArt(string steamPath, string appId)
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
                var dest = Path.Combine(Paths.CoversDir, $"steam_{appId}{suffix}.jpg");
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
            new[] { "library_hero.jpg", "library_header.jpg", "header.jpg" }), "_wide");
        return (cover ?? banner, banner);
    }

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
