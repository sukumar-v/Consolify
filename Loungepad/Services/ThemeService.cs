using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Loungepad.Services;

/// <summary>
/// What a theme's manifest declares. Everything but the folder name is optional, so the
/// smallest possible theme is a folder with a theme.css in it.
/// </summary>
public class ThemeInfo
{
    /// <summary>Folder name. This is the identity -- what settings.json stores.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    /// <summary>URL of the theme's stylesheet on the loungepad.data host, or null if it has none.</summary>
    public string? Css { get; set; }
    /// <summary>URL of the theme's markup (its &lt;template&gt; blocks), or null if it has none.</summary>
    public string? Html { get; set; }
    /// <summary>Tokens applied on top of the stylesheet, e.g. {"--accent": "#0FF"}.</summary>
    public Dictionary<string, string>? Tokens { get; set; }
    /// <summary>
    /// The options the theme declares for itself -- the rows under Settings → Appearance that
    /// belong to this theme alone. Passed to the page exactly as written: the page is what turns
    /// them into rows and into CSS, so it is the page that checks them (themeSettingDefs in
    /// app.js), and a bad entry costs that one row rather than the manifest. The shape is
    /// documented in the README's Themes section.
    /// </summary>
    public JsonElement? Settings { get; set; }
    /// <summary>Set when the manifest could not be read; the theme still loads, badly named.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Finds themes in %APPDATA%\Loungepad\themes and tells the UI when they change on disk.
///
/// A theme is just a folder: theme.json for the name and any token overrides, theme.css for
/// the styling. Nothing is copied or compiled -- the page loads the CSS straight off the
/// loungepad.data virtual host that already maps the data folder, so "installing" a theme is
/// unzipping it and "editing" one is saving the file.
/// </summary>
public class ThemeService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounce = new(250) { AutoReset = false };

    /// <summary>Raised (already debounced) when anything under the themes folder changes.</summary>
    public event Action? Changed;

    public ThemeService() => _debounce.Elapsed += (_, _) => Changed?.Invoke();

    /// <summary>
    /// Every theme on disk, plus the built-in default first.
    ///
    /// A folder with no readable manifest is still listed, named after itself, because a theme
    /// that fails to appear is far more confusing than one that appears with a plain name --
    /// and the Error field gives Settings something to show.
    /// </summary>
    public List<ThemeInfo> List()
    {
        var list = new List<ThemeInfo>
        {
            new() { Id = "", Name = "Classic" },
        };

        try
        {
            if (!Directory.Exists(Paths.ThemesDir)) return list;

            foreach (var dir in Directory.GetDirectories(Paths.ThemesDir).OrderBy(d => d))
            {
                var id = Path.GetFileName(dir);
                // Folder names reach the page inside a URL, so anything that could climb out of
                // the themes directory or break the path is skipped rather than sanitised.
                if (id.StartsWith('.') || id.Contains("..") || id.Any(c => c is '/' or '\\' or '?' or '#')) continue;

                var info = new ThemeInfo { Id = id, Name = id };
                var manifest = Path.Combine(dir, "theme.json");
                if (File.Exists(manifest))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<ThemeInfo>(File.ReadAllText(manifest), JsonOpts);
                        if (parsed is not null)
                        {
                            info.Name = string.IsNullOrWhiteSpace(parsed.Name) ? id : parsed.Name;
                            info.Author = parsed.Author;
                            info.Version = parsed.Version;
                            info.Description = parsed.Description;
                            info.Tokens = parsed.Tokens;
                            info.Settings = parsed.Settings;
                        }
                    }
                    catch (Exception ex)
                    {
                        info.Error = $"theme.json is not valid: {ex.Message}";
                        Log.Info($"Theme '{id}': {info.Error}");
                    }
                }

                // Cache-busted on the file's own timestamp: without this the WebView keeps
                // serving the stylesheet it already has, and saving a theme edit appears to do
                // nothing until the app is restarted.
                info.Css = FileUrl(dir, id, "theme.css");
                info.Html = FileUrl(dir, id, "theme.html");

                info.Id = id;
                list.Add(info);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Listing themes failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// Install the themes that ship with the app into the user's themes folder, and bring an
    /// already-installed copy up to date when the shipped one has moved on.
    ///
    /// This used to skip any folder that already existed, which quietly meant a bundled theme was
    /// frozen at whatever shipped the day it was first installed: every later fix to Marquee --
    /// tile art no longer cropped, the backdrop no longer blown up -- landed in the app and was
    /// never seen, because the copy being loaded was the old one on disk.
    ///
    /// The version in theme.json is what decides. Same version, nothing happens. A different one
    /// and the shipped files replace what is there -- but the whole folder is copied aside first,
    /// so a theme someone has been editing is recoverable rather than gone. Delete a folder to get
    /// the shipped version back cleanly.
    /// </summary>
    /// <summary>
    /// Bundled themes that have been renamed, old id to new. The installed copy of the old one is
    /// moved out of the themes folder on the next start, or it would sit there forever as a second
    /// theme with the same layout and an old bug list -- and settings.json is rewritten to point at
    /// the new id, so a user on the old one simply keeps their theme under its new name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Renamed =
        new Dictionary<string, string> { ["marquee"] = "polish" };

    public static void SyncBuiltIn()
    {
        try
        {
            var src = Path.Combine(AppContext.BaseDirectory, "themes");
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(Paths.ThemesDir);

            foreach (var (oldId, newId) in Renamed)
            {
                var stale = Path.Combine(Paths.ThemesDir, oldId);
                if (!Directory.Exists(stale)) continue;
                var moved = BackUp(stale, oldId, VersionOf(stale));
                try { Directory.Delete(stale, recursive: true); }
                catch (Exception ex) { Log.Info($"Could not remove the retired theme '{oldId}': {ex.Message}"); }
                Log.Info($"Retired the bundled theme '{oldId}', now '{newId}'"
                         + (moved is null ? "" : $"; the old copy is in {moved}"));
            }

            foreach (var dir in Directory.GetDirectories(src))
            {
                var id = Path.GetFileName(dir);
                var dst = Path.Combine(Paths.ThemesDir, id);

                if (!Directory.Exists(dst))
                {
                    CopyTheme(dir, dst);
                    Log.Info($"Installed the bundled theme '{id}'");
                    continue;
                }

                var shipped = VersionOf(dir);
                var installed = VersionOf(dst);
                if (shipped is null || installed == shipped) continue;

                var backup = BackUp(dst, id, installed);
                CopyTheme(dir, dst);
                Log.Info($"Updated the bundled theme '{id}' from {installed ?? "an unversioned copy"} " +
                         $"to {shipped}" + (backup is null ? "" : $"; the old copy is in {backup}"));
            }
        }
        catch (Exception ex)
        {
            // Never block startup over a theme; the launcher just opens with fewer of them.
            Log.Info($"Syncing bundled themes failed: {ex.Message}");
        }
    }

    /// <summary>
    /// An option id as theme.json may declare one and as the page checks it: a letter, then
    /// letters, digits and hyphens, 32 at most.
    /// </summary>
    private static readonly Regex OptionId = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Keep what the page sent for the themes' own options to what an option can be -- a bool, a
    /// number or a short string, under an id shaped like one theme.json may declare, for a theme
    /// id shaped like a folder name -- and drop the rest. The page checks every value against
    /// the theme's definition when it applies it; this only stops settings.json from carrying
    /// arbitrary JSON that a page happened to send.
    /// </summary>
    public static Dictionary<string, Dictionary<string, JsonElement>> CleanSettingValues(
        Dictionary<string, Dictionary<string, JsonElement>>? values)
    {
        var clean = new Dictionary<string, Dictionary<string, JsonElement>>();
        if (values is null) return clean;
        foreach (var (themeId, options) in values)
        {
            if (options is null || !IsThemeId(themeId)) continue;
            var kept = new Dictionary<string, JsonElement>();
            foreach (var (id, v) in options)
            {
                if (!OptionId.IsMatch(id)) continue;
                var ok = v.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
                      || (v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 64);
                if (ok) kept[id] = v.Clone();
            }
            if (kept.Count > 0) clean[themeId] = kept;
        }
        return clean;
    }

    /// <summary>A folder name List() would accept, which is what a theme id is; "" is Classic.</summary>
    private static bool IsThemeId(string id) =>
        id.Length <= 64 && !id.StartsWith('.') && !id.Contains("..")
        && !id.Any(c => c is '/' or '\\' or '?' or '#' or ':');

    /// <summary>The version string from a theme folder's manifest, or null if it has none.</summary>
    private static string? VersionOf(string dir)
    {
        var manifest = Path.Combine(dir, "theme.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<ThemeInfo>(File.ReadAllText(manifest), JsonOpts);
            return string.IsNullOrWhiteSpace(parsed?.Version) ? null : parsed!.Version;
        }
        catch { return null; }
    }

    /// <summary>
    /// Move a theme folder aside before it is replaced. Outside the themes directory on purpose:
    /// a backup left next to the real thing would be listed in Settings as a second theme.
    /// </summary>
    private static string? BackUp(string dir, string id, string? version)
    {
        try
        {
            var root = Path.Combine(Paths.DataDir, "theme-backups");
            Directory.CreateDirectory(root);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dst = Path.Combine(root, $"{id}-{version ?? "unversioned"}-{stamp}");
            CopyTheme(dir, dst);
            return dst;
        }
        catch (Exception ex)
        {
            // A backup that cannot be written is not a reason to leave the user on a stale theme,
            // but it is a reason to say so.
            Log.Info($"Could not back up the theme '{id}' before updating it: {ex.Message}");
            return null;
        }
    }

    private static void CopyTheme(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
    }

    /// <summary>
    /// A theme file's URL, stamped with its own mtime.
    ///
    /// The stamp is what makes hot reload work: without it the WebView keeps serving the copy
    /// it already has, and saving an edit appears to do nothing until a restart.
    /// </summary>
    private static string? FileUrl(string dir, string id, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return null;
        return $"https://loungepad.data/themes/{Uri.EscapeDataString(id)}/{file}"
             + $"?v={new FileInfo(path).LastWriteTimeUtc.Ticks}";
    }

    /// <summary>
    /// Watch the themes folder so an edit shows up without a restart.
    ///
    /// Debounced because one save is several events -- editors write, rename and touch the
    /// directory -- and each would otherwise reload the stylesheet again.
    /// </summary>
    public void Watch()
    {
        try
        {
            Directory.CreateDirectory(Paths.ThemesDir);
            _watcher = new FileSystemWatcher(Paths.ThemesDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // A missing or unwatchable folder must not stop the launcher starting; themes just
            // will not hot-reload.
            Log.Info($"Theme watcher not started: {ex.Message}");
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
