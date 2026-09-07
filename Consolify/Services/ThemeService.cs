using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Consolify.Services;

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
    /// <summary>URL of the theme's stylesheet on the consolify.data host, or null if it has none.</summary>
    public string? Css { get; set; }
    /// <summary>URL of the theme's markup (its &lt;template&gt; blocks), or null if it has none.</summary>
    public string? Html { get; set; }
    /// <summary>Tokens applied on top of the stylesheet, e.g. {"--accent": "#0FF"}.</summary>
    public Dictionary<string, string>? Tokens { get; set; }
    /// <summary>Set when the manifest could not be read; the theme still loads, badly named.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Finds themes in %APPDATA%\Consolify\themes and tells the UI when they change on disk.
///
/// A theme is just a folder: theme.json for the name and any token overrides, theme.css for
/// the styling. Nothing is copied or compiled -- the page loads the CSS straight off the
/// consolify.data virtual host that already maps the data folder, so "installing" a theme is
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
            new() { Id = "", Name = "Consolify (default)" },
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
    /// A theme file's URL, stamped with its own mtime.
    ///
    /// The stamp is what makes hot reload work: without it the WebView keeps serving the copy
    /// it already has, and saving an edit appears to do nothing until a restart.
    /// </summary>
    private static string? FileUrl(string dir, string id, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return null;
        return $"https://consolify.data/themes/{Uri.EscapeDataString(id)}/{file}"
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
