using System.IO;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>What one detection pass added.</summary>
public sealed record DetectionSummary(List<EmulatorDef> Emulators, List<RomFolderDef> Folders);

/// <summary>
/// Finds emulators and ROMs the way the store scanners find games: from what is already on the
/// machine, with no setup. Runs at the start of every scan.
///
/// Emulators are found by their exe, wherever it is: a portable folder at a drive root
/// (C:\RetroArch-Win64, which is how most people run RetroArch), Program Files, %LOCALAPPDATA%
/// (yuzu, Citra), a Downloads or Desktop folder, an "Emulators" collection, Steam's copy of
/// RetroArch, the registry's uninstall entries, and Start Menu shortcuts. Only exes the presets
/// name are ever taken, so a scan cannot mistake a video player for an emulator.
///
/// ROMs come from three places, best first. RetroArch's playlists are a curated list per system
/// with the core to use, and the only source that works when several systems' ROMs share a
/// folder. Then the game-list folders PCSX2 and DuckStation keep in their ini files. Then folders
/// named after a system -- an EmuDeck-style Emulation\roms\snes, a D:\ROMs\PS1, RetroArch's own
/// downloads\GBA -- as long as they actually hold a file of that system's kind.
///
/// Anything removed by hand goes on an ignore list and is not found again.
/// </summary>
public static class EmulatorDetection
{
    public static DetectionSummary Run(LibraryStore library)
    {
        var summary = new DetectionSummary(new(), new());
        try
        {
            foreach (var e in FindEmulators(library.Emulators, library.IgnoredEmulatorPaths))
            {
                library.AddEmulator(e);
                summary.Emulators.Add(e);
            }
            foreach (var f in FindRomFolders(library.Emulators, library.RomFolders, library.IgnoredRomFolderPaths))
            {
                library.AddRomFolder(f);
                summary.Folders.Add(f);
            }
        }
        catch (Exception ex) { Log.Info($"Emulator detection failed: {ex.Message}"); }
        return summary;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    // ---------- Emulators ----------

    public static List<EmulatorDef> FindEmulators(IReadOnlyList<EmulatorDef> existing, IReadOnlyCollection<string> ignored)
    {
        var known = new HashSet<string>(existing.Select(e => e.ExePath).Concat(ignored), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var found = new List<EmulatorDef>();

        void Consider(string exe, string source)
        {
            string full;
            try { if (!File.Exists(exe)) return; full = Path.GetFullPath(exe); }
            catch { return; }
            var preset = EmulatorPresets.Detect(full);
            if (preset is null || !known.Add(full)) return;

            // A second copy of the same emulator is named for where it lives, so two rows that
            // both say "RetroArch" cannot happen.
            var name = preset.Name;
            if (!names.Add(name))
            {
                name = $"{preset.Name} ({Path.GetFileName(Path.GetDirectoryName(full))})";
                names.Add(name);
            }
            found.Add(new EmulatorDef
            {
                Id = NewId(), Name = name, ExePath = full, Args = preset.Args, Preset = preset.Key,
                Platforms = preset.Platforms.ToList(), Detected = true,
            });
            Log.Info($"Emulator detection: {name} at {full} ({source})");
        }

        foreach (var dir in CandidateProgramDirs())
            foreach (var exe in TopLevelExes(dir)) Consider(exe, "folder");
        foreach (var exe in RegistryExes()) Consider(exe, "registry");
        foreach (var exe in ShortcutExes()) Consider(exe, "start menu");
        return found;
    }

    /// <summary>
    /// Where an emulator's folder tends to be. One level under the program folders, the profile
    /// folders and every drive root; two levels under %LOCALAPPDATA% and the download folders,
    /// but only into a folder whose name says emulator, so "Downloads" is not walked in full.
    /// </summary>
    private static IEnumerable<string> CandidateProgramDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        var shallow = new List<string?>
        {
            Special(Environment.SpecialFolder.ProgramFiles),
            Special(Environment.SpecialFolder.ProgramFilesX86),
            Special(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Special(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Special(Environment.SpecialFolder.ApplicationData),
            Special(Environment.SpecialFolder.UserProfile),
            Special(Environment.SpecialFolder.DesktopDirectory),
            Special(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Special(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable) shallow.Add(drive.RootDirectory.FullName); }
            catch { /* a drive that is not there */ }
        }

        foreach (var root in shallow)
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(dir)) yield return dir;
                // A collection folder, or a versioned folder holding the real one: descend once.
                if (LooksEmulatorish(name))
                    foreach (var sub in SafeDirs(dir))
                        if (seen.Add(sub)) yield return sub;
            }
            // EmuDeck puts everything under <drive>\Emulation\emulators\<Name>.
            var emudeck = Path.Combine(root, "Emulation", "emulators");
            if (Directory.Exists(emudeck))
                foreach (var sub in SafeDirs(emudeck))
                    if (seen.Add(sub)) yield return sub;
        }

        // Steam sells RetroArch; it lands in a library folder like any game.
        foreach (var steamRoot in LibraryScanner.SteamLibraryRoots())
        {
            var dir = Path.Combine(steamRoot, "common", "RetroArch");
            if (Directory.Exists(dir) && seen.Add(dir)) yield return dir;
        }
    }

    private static readonly string[] EmulatorWords = { "emu", "retroarch", "emulation" };

    /// <summary>A folder worth looking one level further into: named for an emulator, or for
    /// emulation in general.</summary>
    private static bool LooksEmulatorish(string name)
    {
        var lower = name.ToLowerInvariant();
        if (EmulatorWords.Any(lower.Contains)) return true;
        return EmulatorPresets.All.Any(p => p.Name.Length >= 4 && lower.Contains(p.Name.ToLowerInvariant()));
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> TopLevelExes(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.exe").ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Installed emulators, from the uninstall entries: the icon usually IS the exe, and
    /// the install folder holds it otherwise.</summary>
    private static IEnumerable<string> RegistryExes()
    {
        var exes = new List<string>();
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub is null) continue;
                        if (sub.GetValue("DisplayIcon") is string icon)
                        {
                            var exe = icon.Split(',')[0].Trim().Trim('"');
                            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exes.Add(exe);
                        }
                        if (sub.GetValue("InstallLocation") is string loc && loc.Length > 0)
                        {
                            loc = loc.Trim().Trim('"');
                            if (Directory.Exists(loc)) exes.AddRange(TopLevelExes(loc));
                        }
                    }
                    catch { /* one unreadable entry */ }
                }
            }
            catch { /* hive not readable */ }
        }
        return exes;
    }

    /// <summary>
    /// Start Menu shortcuts whose name looks like an emulator's, resolved to their targets. This
    /// is what catches an emulator installed somewhere unusual. Only the likely shortcuts are
    /// resolved -- each resolution is a COM call -- and the list is small.
    /// </summary>
    private static IEnumerable<string> ShortcutExes()
    {
        var exes = new List<string>();
        var wanted = EmulatorPresets.All
            .SelectMany(p => p.ExeNames.Select(Path.GetFileNameWithoutExtension).Append(p.Name))
            .Select(s => s!.ToLowerInvariant())
            .Where(s => s.Length >= 4)
            .Distinct()
            .ToList();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        object? shell = null;
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            List<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
            }
            catch { continue; }

            foreach (var lnk in links)
            {
                var stem = Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                if (!wanted.Any(stem.Contains)) continue;
                try
                {
                    shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                    if (shell is null) return exes;
                    dynamic shortcut = ((dynamic)shell).CreateShortcut(lnk);
                    string target = shortcut.TargetPath;
                    if (!string.IsNullOrWhiteSpace(target)) exes.Add(target);
                }
                catch { /* a shortcut that will not resolve */ }
            }
        }
        return exes;
    }

    // ---------- ROMs ----------

    public static List<RomFolderDef> FindRomFolders(IReadOnlyList<EmulatorDef> emulators,
        IReadOnlyList<RomFolderDef> existing, IReadOnlyCollection<string> ignored)
    {
        var taken = new HashSet<string>(existing.Select(f => f.Path).Concat(ignored), StringComparer.OrdinalIgnoreCase);
        var found = new List<RomFolderDef>();
        // Folders that playlist entries live in. A playlist already lists what is there, so the
        // folder is not added a second time on top of it.
        var covered = new List<string>();

        foreach (var ra in emulators.Where(e => e.Preset == "retroarch"))
        {
            foreach (var lpl in RetroArchPlaylists.Find(ra))
            {
                var playlist = RetroArchPlaylists.Read(lpl);
                if (playlist is null || playlist.PlatformId is null) continue;
                var live = playlist.Entries.Where(en => SafeExists(en.RomPath)).ToList();
                covered.AddRange(live.Select(en => Path.GetDirectoryName(en.RomPath) ?? ""));
                if (live.Count == 0 || !taken.Add(Path.GetFullPath(lpl))) continue;

                var platform = EmulatedPlatforms.Find(playlist.PlatformId)!;
                found.Add(new RomFolderDef
                {
                    Id = NewId(), Path = Path.GetFullPath(lpl), Playlist = true, Detected = true,
                    PlatformId = platform.Id, EmulatorId = ra.Id,
                    Core = playlist.CommonCore ?? EmulatorLaunch.SuggestCore(ra, platform),
                });
                Log.Info($"ROM detection: RetroArch playlist {Path.GetFileName(lpl)} ({live.Count} game{(live.Count == 1 ? "" : "s")})");
            }
        }

        foreach (var (dir, platformId, preferred) in CandidateRomDirs(emulators))
        {
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); } catch { continue; }
            var platform = EmulatedPlatforms.Find(platformId);
            if (platform is null || taken.Contains(full)) continue;
            if (covered.Any(c => c.Equals(full, StringComparison.OrdinalIgnoreCase)
                              || c.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            // Under a folder already in the library for the same system, so already scanned.
            if (existing.Concat(found).Any(f => !f.Playlist && f.PlatformId == platform.Id
                    && full.StartsWith(f.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            if (!HasRoms(full, platform)) continue;

            var emu = PickEmulator(platform, emulators, preferred);
            taken.Add(full);
            found.Add(new RomFolderDef
            {
                Id = NewId(), Path = full, PlatformId = platform.Id, Detected = true,
                EmulatorId = emu?.Id,
                Core = emu is null ? null : EmulatorLaunch.SuggestCore(emu, platform),
            });
            Log.Info($"ROM detection: {full} as {platform.Name}" + (emu is null ? ", no emulator for it" : $" via {emu.Name}"));
        }
        return found;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>
    /// The emulator a found folder should run with. The source's own program first (a PCSX2 ini
    /// names PS2 games for PCSX2); then a standalone emulator made for the system, the most
    /// specialised one when there are several -- mGBA over an everything-emulator for the GBA;
    /// then RetroArch, if it has a core for the system installed.
    /// </summary>
    public static EmulatorDef? PickEmulator(EmulatedPlatforms.Def platform, IReadOnlyList<EmulatorDef> emulators, string? preferredPreset)
    {
        if (preferredPreset is not null && emulators.FirstOrDefault(e => e.Preset == preferredPreset) is { } own)
            return own;
        var standalone = emulators
            .Where(e => e.Preset != "retroarch" && e.Platforms.Contains(platform.Id))
            .OrderBy(e => e.Platforms.Count)
            .FirstOrDefault();
        if (standalone is not null) return standalone;
        return emulators.Where(e => e.Preset == "retroarch")
            .FirstOrDefault(e => EmulatorLaunch.SuggestCore(e, platform) is not null);
    }

    /// <summary>Folders that might hold one system's ROMs, with the system, and the preset of the
    /// program that named the folder when one did.</summary>
    private static IEnumerable<(string Dir, string PlatformId, string? Preferred)> CandidateRomDirs(IReadOnlyList<EmulatorDef> emulators)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Roots whose SUBfolders are systems: D:\ROMs\snes, D:\Emulation\roms\psx, RetroArch's own
        // downloads folder sorted by hand, a "roms" folder beside an emulator.
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try { if (!drive.IsReady) continue; root = drive.RootDirectory.FullName; }
            catch { continue; }
            roots.Add(Path.Combine(root, "ROMs"));
            roots.Add(Path.Combine(root, "Emulation", "roms"));
            roots.Add(Path.Combine(root, "Games", "ROMs"));
            roots.Add(Path.Combine(root, "Emulators", "ROMs"));
            roots.Add(Path.Combine(root, "Emulation", "ROMs"));
        }
        foreach (var sub in new[] { "ROMs", "Documents\\ROMs", "Downloads\\ROMs", "Desktop\\ROMs", "Games\\ROMs" })
            roots.Add(Path.Combine(profile, sub));
        foreach (var e in emulators)
        {
            var dir = Path.GetDirectoryName(e.ExePath);
            if (dir is null) continue;
            roots.Add(Path.Combine(dir, "roms"));
            roots.Add(Path.Combine(dir, "downloads"));
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null) { roots.Add(Path.Combine(parent, "roms")); roots.Add(Path.Combine(parent, "ROMs")); }
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root) || !seen.Add(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                var id = EmulatedPlatforms.Guess(Path.GetFileName(dir));
                if (id is not null && seen.Add(dir)) yield return (dir, id, null);
            }
        }

        // Folders that are themselves named for a system, at a drive root or in the profile:
        // D:\SNES, Downloads\GBA.
        var shallow = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable) shallow.Add(drive.RootDirectory.FullName); }
            catch { }
        }
        shallow.Add(profile);
        shallow.Add(Path.Combine(profile, "Downloads"));
        shallow.Add(Path.Combine(profile, "Desktop"));
        shallow.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        foreach (var root in shallow)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                var id = EmulatedPlatforms.Guess(Path.GetFileName(dir));
                if (id is not null && seen.Add(dir)) yield return (dir, id, null);
            }
        }

        // The game-list folders of the emulators that keep one.
        foreach (var e in emulators)
        {
            var dir = Path.GetDirectoryName(e.ExePath) ?? "";
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var (inis, platformId) = e.Preset switch
            {
                "pcsx2" => (new[] { Path.Combine(dir, "inis", "PCSX2.ini"), Path.Combine(docs, "PCSX2", "inis", "PCSX2.ini") }, "ps2"),
                "duckstation" => (new[] { Path.Combine(dir, "settings.ini"), Path.Combine(docs, "DuckStation", "settings.ini") }, "ps1"),
                _ => (Array.Empty<string>(), ""),
            };
            foreach (var ini in inis)
                foreach (var listed in GameListPaths(ini))
                    if (Directory.Exists(listed) && seen.Add(listed)) yield return (listed, platformId, e.Preset);
        }
    }

    /// <summary>The [GameList] Paths / RecursivePaths of a PCSX2 or DuckStation ini. Both write
    /// the key once per folder.</summary>
    private static IEnumerable<string> GameListPaths(string ini)
    {
        var paths = new List<string>();
        try
        {
            if (!File.Exists(ini)) return paths;
            var inSection = false;
            foreach (var raw in File.ReadLines(ini))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inSection = line.Equals("[GameList]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inSection) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq].Trim();
                if (!key.Equals("Paths", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("RecursivePaths", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(eq + 1)..].Trim().Trim('"');
                if (value.Length > 0) paths.Add(value);
            }
        }
        catch { /* not readable; nothing to suggest */ }
        return paths;
    }

    /// <summary>True when the folder holds at least one file of the system's kind. Stops at the
    /// first, and gives up after a few thousand files so a folder full of something else cannot
    /// stall the scan.</summary>
    private static bool HasRoms(string dir, EmulatedPlatforms.Def platform)
    {
        var exts = platform.Extensions.Select(e => "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var looked = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*",
                         new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (exts.Contains(Path.GetExtension(f))) return true;
                if (++looked > 5000) return false;
            }
        }
        catch { /* unreadable */ }
        return false;
    }
}
