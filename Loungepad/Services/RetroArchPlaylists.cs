using System.IO;
using System.Text.Json;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// RetroArch's playlists, which are the closest thing an emulator keeps to a game library.
///
/// One .lpl per system under RetroArch's playlists folder, named after the libretro database it
/// was matched against ("Nintendo - Game Boy Advance.lpl"), holding one entry per ROM: its path,
/// the database's own name for it, and the core that runs it -- or "DETECT", which means
/// whichever core is set for the system. Since 1.7.5 the file is JSON; before that it was six
/// plain lines per entry, and a folder that has been upgraded through several versions can still
/// hold one of those.
/// </summary>
public static class RetroArchPlaylists
{
    public sealed record Entry(string RomPath, string Label, string? CorePath);

    public sealed record Playlist(string Path, string? PlatformId, List<Entry> Entries)
    {
        /// <summary>The core most of the entries name, when any of them names one that exists.
        /// "DETECT" is not a core, and a core that has since been deleted is not one either.</summary>
        public string? CommonCore =>
            Entries.Select(e => e.CorePath)
                .Where(c => c is not null && File.Exists(c))
                .GroupBy(c => c!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
    }

    /// <summary>
    /// Every system playlist this RetroArch has: in its own playlists folder (a portable
    /// install), in %APPDATA%\RetroArch (an installed one), and wherever retroarch.cfg points
    /// if it points somewhere else. The content_* files are history and favourites, which mix
    /// systems and are not libraries.
    /// </summary>
    public static IEnumerable<string> Find(EmulatorDef retroarch)
    {
        var emuDir = Path.GetDirectoryName(retroarch.ExePath);
        if (emuDir is null) yield break;

        var dirs = new List<string>
        {
            Path.Combine(emuDir, "playlists"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch", "playlists"),
        };
        if (ConfiguredDir(emuDir) is { } configured) dirs.Add(configured);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.lpl").ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                if (Path.GetFileName(f).StartsWith("content_", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(Path.GetFullPath(f))) yield return f;
            }
        }
    }

    /// <summary>playlist_directory out of retroarch.cfg. ":\playlists" means beside the exe,
    /// which the list above already covers; an absolute path is the interesting case.</summary>
    private static string? ConfiguredDir(string emuDir)
    {
        try
        {
            var cfg = Path.Combine(emuDir, "retroarch.cfg");
            if (!File.Exists(cfg)) return null;
            foreach (var line in File.ReadLines(cfg))
            {
                if (!line.StartsWith("playlist_directory", StringComparison.Ordinal)) continue;
                var q1 = line.IndexOf('"');
                var q2 = line.LastIndexOf('"');
                if (q1 < 0 || q2 <= q1) return null;
                var value = line[(q1 + 1)..q2];
                if (value.StartsWith(":\\") || value.StartsWith(":/")) return Path.Combine(emuDir, value[2..]);
                return Path.IsPathRooted(value) ? value : null;
            }
        }
        catch { /* a config we cannot read is not a reason to skip the default folders */ }
        return null;
    }

    public static Playlist? Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { Log.Info($"Playlist {path} could not be read: {ex.Message}"); return null; }

        var entries = text.TrimStart().StartsWith('{') ? ReadJson(text) : ReadLegacy(text);
        if (entries is null) return null;
        var platform = PlatformFor(Path.GetFileNameWithoutExtension(path));
        return new Playlist(path, platform, entries);
    }

    private static List<Entry>? ReadJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return new List<Entry>();
            var list = new List<Entry>();
            foreach (var item in items.EnumerateArray())
            {
                var rom = Str(item, "path");
                if (string.IsNullOrWhiteSpace(rom)) continue;
                list.Add(new Entry(RomFile(rom), Str(item, "label") ?? "", Core(Str(item, "core_path"))));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Info($"Playlist is not valid JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>Six lines per entry: path, label, core path, core name, crc, db name.</summary>
    private static List<Entry> ReadLegacy(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var list = new List<Entry>();
        for (var i = 0; i + 5 < lines.Count; i += 6)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            list.Add(new Entry(RomFile(lines[i]), lines[i + 1], Core(lines[i + 2])));
        }
        return list;
    }

    /// <summary>A ROM inside an archive is written "game.zip#game.gba". The archive is the file
    /// that exists, and the file every core here can be handed directly.</summary>
    private static string RomFile(string path)
    {
        var hash = path.IndexOf('#');
        return hash > 0 ? path[..hash] : path;
    }

    private static string? Core(string? corePath) =>
        string.IsNullOrWhiteSpace(corePath) || corePath.Equals("DETECT", StringComparison.OrdinalIgnoreCase)
            ? null : corePath;

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Which of our systems a playlist is for, from its database name. The names are stable and
    /// mostly self-describing -- "Sega - Mega Drive - Genesis" -- so the folder-name guesser does
    /// most of the work; the few it cannot read are listed. Null for the playlists that are not a
    /// system at all: Doom, Quake, ScummVM, TIC-80 and the other engine cores.
    /// </summary>
    public static string? PlatformFor(string dbName)
    {
        foreach (var (name, id) in Special)
            if (dbName.Equals(name, StringComparison.OrdinalIgnoreCase)) return id;
        return EmulatedPlatforms.Guess(dbName);
    }

    private static readonly (string Name, string Id)[] Special =
    {
        ("FBNeo - Arcade Games", "arcade"),
        ("MAME", "arcade"),
        ("MAME 2000", "arcade"), ("MAME 2003", "arcade"), ("MAME 2003-Plus", "arcade"), ("MAME 2010", "arcade"), ("MAME 2015", "arcade"),
        ("Nintendo - Family Computer Disk System", "nes"),
        ("Nintendo - Satellaview", "snes"),
        ("Nintendo - Sufami Turbo", "snes"),
        ("Sega - SG-1000", "sms"),
        ("SNK - Neo Geo CD", "neogeo"),
        ("Sony - PlayStation Portable (PSN)", "psp"),
    };
}
