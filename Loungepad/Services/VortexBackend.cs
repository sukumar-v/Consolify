using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loungepad.Interop;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// Vortex, Nexus Mods' manager, driven from outside.
///
/// Vortex has no API of its own to call, so this works through an extension of ours that runs
/// inside it (vortex-bridge/index.js, copied into Vortex's plugins folder by <see cref="SyncPlugin"/>)
/// and answers JSON on the loopback interface. Everything that reads or changes a mod goes
/// through that. Downloads and installs do not: <c>Vortex.exe --install &lt;url&gt;</c> is a
/// documented command line, a second instance hands it to the running one, and it works whether
/// or not the extension is loaded -- so <see cref="Install"/> just runs it.
///
/// Vortex deploys mods into the game's own folder with hardlinks, which is the property that makes
/// it fit a launcher: once a deploy has run the game is started the normal way, and nothing here
/// has to wrap the launch.
/// </summary>
public sealed class VortexBackend
{
    public const int DefaultPort = 47391;
    public const string PluginFolderName = "loungepad-bridge";
    /// <summary>Where the installer is. The Files tab is the one with the download button.</summary>
    public const string DownloadUrl = "https://www.nexusmods.com/site/mods/1?tab=files";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Func<string?> _configuredPath;
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly int _port = DefaultPort;
    private bool _pluginJustInstalled;

    public string? ExePath { get; private set; }
    public string? Version { get; private set; }

    public static string VortexDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex");
    public static string PluginDir { get; } = Path.Combine(VortexDataDir, "plugins", PluginFolderName);
    private static string ShippedPluginDir => Path.Combine(AppContext.BaseDirectory, "vortex-bridge");

    public VortexBackend(Func<string?> configuredPath)
    {
        _configuredPath = configuredPath;
        // A fresh secret per run. The extension re-reads bridge.json on every request, so a new
        // token is picked up the moment it is written and never has to be remembered anywhere.
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _http.DefaultRequestHeaders.Host = $"127.0.0.1:{_port}";
        Locate();
    }

    // ---- finding it ----

    /// <summary>
    /// Where Vortex.exe is, or null. The setting first, for a portable copy or an unusual folder;
    /// then the per-user default that its installer uses; then the uninstall entries; then the
    /// all-users default. Re-run whenever it matters, because the point of the Get Vortex row is
    /// that this answer changes while the launcher is running.
    /// </summary>
    public string? Locate()
    {
        var candidates = new List<string?>();
        var configured = _configuredPath()?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(configured))
            candidates.Add(Directory.Exists(configured) ? Path.Combine(configured, "Vortex.exe") : configured);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Vortex", "Vortex.exe"));
        candidates.AddRange(RegistryCandidates());
        // The all-users install goes to Program Files\Vortex (seen with 1.15); older installers
        // used the publisher's folder.
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vortex", "Vortex.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Black Tree Gaming Ltd", "Vortex", "Vortex.exe"));

        ExePath = candidates.FirstOrDefault(c => c is not null && File.Exists(c));
        Version = ExePath is null ? null : ReadVersion(ExePath);
        return ExePath;
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        var found = new List<string>();
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
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
                        var display = sub?.GetValue("DisplayName") as string;
                        if (display is null || !display.StartsWith("Vortex", StringComparison.OrdinalIgnoreCase)) continue;
                        if (sub!.GetValue("DisplayIcon") is string icon)
                        {
                            var exe = icon.Split(',')[0].Trim().Trim('"');
                            if (exe.EndsWith("Vortex.exe", StringComparison.OrdinalIgnoreCase)) found.Add(exe);
                        }
                        if (sub.GetValue("InstallLocation") is string loc && loc.Length > 0)
                            found.Add(Path.Combine(loc.Trim().Trim('"'), "Vortex.exe"));
                    }
                    catch { /* one unreadable entry */ }
                }
            }
            catch { /* hive not readable */ }
        }
        return found;
    }

    private static string? ReadVersion(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            return string.IsNullOrWhiteSpace(v.ProductVersion) ? v.FileVersion : v.ProductVersion;
        }
        catch { return null; }
    }

    public static bool IsRunning => Process.GetProcessesByName("Vortex").Length > 0;

    // ---- the extension ----

    /// <summary>
    /// Put the bridge extension where Vortex loads extensions from, or bring it up to date, and
    /// write this run's port and token beside it. The version in info.json decides whether the
    /// files are copied, the same way the bundled themes work; bridge.json is rewritten every
    /// time because the token is new every time.
    /// </summary>
    public void SyncPlugin()
    {
        try
        {
            var src = ShippedPluginDir;
            if (!Directory.Exists(src)) { Log.Info($"Vortex bridge: nothing shipped at {src}"); return; }
            RemoveOldPlugin();
            Directory.CreateDirectory(PluginDir);

            var shipped = PluginVersion(Path.Combine(src, "info.json"));
            var installed = PluginVersion(Path.Combine(PluginDir, "info.json"));
            if (shipped is null || shipped != installed || !File.Exists(Path.Combine(PluginDir, "index.js")))
            {
                foreach (var f in Directory.GetFiles(src))
                    File.Copy(f, Path.Combine(PluginDir, Path.GetFileName(f)), overwrite: true);
                _pluginJustInstalled = true;
                Log.Info($"Vortex bridge: {(installed is null ? "installed" : $"updated from {installed}")} to {shipped} in {PluginDir}");
            }

            var cfg = JsonSerializer.Serialize(new { port = _port, token = _token });
            File.WriteAllText(Path.Combine(PluginDir, "bridge.json"), cfg);
        }
        catch (Exception ex) { Log.Info($"Vortex bridge: could not sync the extension: {ex.Message}"); }
    }

    /// <summary>
    /// The extension shipped as consolify-bridge up to 1.4. Left in place, Vortex loads both at
    /// its next start: they race for the one port, and the old one reads a bridge.json nobody
    /// writes any more, so whichever wins, every request can fail on a stale token. A Vortex
    /// already running with the old one loaded answers 503 once its config is gone, which is the
    /// ordinary "restart Vortex once" state.
    /// </summary>
    private static void RemoveOldPlugin()
    {
        var old = Path.Combine(VortexDataDir, "plugins", ConsolifyMigration.OldPluginFolderName);
        if (!Directory.Exists(old)) return;
        try
        {
            Directory.Delete(old, recursive: true);
            Log.Info($"Vortex bridge: removed the old extension at {old}");
        }
        catch (Exception ex) { Log.Info($"Vortex bridge: could not remove the old extension at {old}: {ex.Message}"); }
    }

    private static string? PluginVersion(string infoJson)
    {
        try
        {
            if (!File.Exists(infoJson)) return null;
            return JsonNode.Parse(File.ReadAllText(infoJson))?["version"]?.GetValue<string>();
        }
        catch { return null; }
    }

    // ---- running it ----

    /// <summary>
    /// Get to a state where the bridge answers, starting Vortex minimized if it is not running.
    /// The status says why when it cannot: not installed; running without the extension (a
    /// restart is needed, and only the first time); or simply slow to come up.
    /// </summary>
    public async Task<ModManagerStatus> EnsureBridgeAsync(CancellationToken ct)
    {
        var status = new ModManagerStatus { Installed = ExePath is not null, Path = ExePath, Version = Version };
        if (ExePath is null) { status.Error = "Vortex is not installed"; return status; }
        SyncPlugin();

        if (await PingAsync(ct) is { } v)
        {
            status.Running = true; status.Version = v ?? Version;
            // Vortex loads an extension once, at startup: a bridge updated on disk is not the one
            // answering until Vortex restarts, and an older one is missing routes this build
            // asks for. Say so rather than let a missing route read as an empty answer.
            var shipped = PluginVersion(Path.Combine(ShippedPluginDir, "info.json"));
            if (shipped is not null && _bridgeVersion is not null && _bridgeVersion != shipped)
            {
                status.NeedsRestart = true;
                status.Error = $"Vortex is running an older Loungepad bridge ({_bridgeVersion}); restart Vortex once to load {shipped}";
                return status;
            }
            status.BridgeReady = true;
            return status;
        }
        if (IsRunning)
        {
            // Give a Vortex that is still starting up a moment before concluding the extension
            // is not loaded in it.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(_pluginJustInstalled ? 3 : 20);
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (await PingAsync(ct) is { } v2)
                {
                    status.Running = true; status.BridgeReady = true; status.Version = v2 ?? Version;
                    return status;
                }
            }
            status.Running = true;
            status.NeedsRestart = true;
            status.Error = "Vortex is running but has not loaded the Loungepad bridge yet. Restart Vortex once";
            return status;
        }

        Log.Info("Starting Vortex minimized for the mod list");
        if (!Start("--start-minimized"))
        {
            status.Error = "Vortex could not be started";
            return status;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            if (await PingAsync(ct) is { } v3)
            {
                status.Running = true; status.BridgeReady = true; status.Version = v3 ?? Version;
                return status;
            }
            if (!IsRunning && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(75))
            {
                status.Error = "Vortex closed again before the bridge answered";
                return status;
            }
        }
        status.Running = IsRunning;
        status.NeedsRestart = status.Running;
        status.Error = status.Running
            ? "Vortex started but the Loungepad bridge did not answer. Restart Vortex once"
            : "Vortex did not start";
        return status;
    }

    /// <summary>The version of the bridge extension that last answered, for the restart check.</summary>
    private string? _bridgeVersion;

    /// <summary>Vortex's version if the bridge answers, else null. Inner null means it answered without one.</summary>
    private async Task<string?> PingAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var node = await GetAsync("status", cts.Token);
            if (node?["ok"]?.GetValue<bool>() != true) return null;
            _bridgeVersion = node["bridge"]?.GetValue<string>();
            return node["vortex"]?.GetValue<string>() ?? "";
        }
        catch { return null; }
    }

    public bool Start(string args)
    {
        if (ExePath is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(ExePath)
            {
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "",
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Vortex could not be started: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Download and install from a URL, through Vortex's own command line. Any protocol Vortex
    /// handles: an nxm:// link from the website is the usual one. A running Vortex takes it over
    /// from the second instance; a stopped one starts, minimized, and does it.
    /// </summary>
    public bool Install(string url)
    {
        if (!url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        Log.Info($"Vortex install: {url}");
        return Start($"--install \"{url.Replace("\"", "")}\" --start-minimized");
    }

    /// <summary>Ask the running Vortex to close, wait for it, and start it again minimized.</summary>
    public async Task<bool> RestartAsync(CancellationToken ct)
    {
        var procs = Process.GetProcessesByName("Vortex");
        if (procs.Length == 0) return Start("--start-minimized");
        if (CloseWindows() == 0) foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTime.UtcNow < until && IsRunning && !ct.IsCancellationRequested) await Task.Delay(500, ct);
        foreach (var p in procs) p.Dispose();
        if (IsRunning) { Log.Info("Vortex did not close when asked"); return false; }
        await Task.Delay(1000, ct);
        return Start("--start-minimized");
    }

    /// <summary>
    /// WM_CLOSE to every top-level window of Vortex's that is titled "Vortex", visible or not. A
    /// Vortex started minimized keeps its main window hidden, so Process.CloseMainWindow has
    /// nothing to close and the visible-window search below finds nothing either -- but the
    /// hidden window still takes the message, and Vortex exits cleanly within a second of it.
    /// Returns how many windows were asked.
    /// </summary>
    public static int CloseWindows()
    {
        var pids = VortexPids();
        if (pids.Count == 0) return 0;
        var n = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            if (sb.ToString() != "Vortex") return true;
            NativeMethods.PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }

    private static HashSet<uint> VortexPids() =>
        Process.GetProcessesByName("Vortex").Select(p => { var id = (uint)p.Id; p.Dispose(); return id; }).ToHashSet();

    /// <summary>Vortex's main window: the visible top-level window of a process named Vortex,
    /// or zero when it is minimized to the tray or not running.</summary>
    public static IntPtr MainWindow()
    {
        var pids = VortexPids();
        if (pids.Count == 0) return IntPtr.Zero;
        var found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // ---- the bridge ----

    public async Task<List<ModGame>> GamesAsync(CancellationToken ct)
    {
        var node = await GetAsync("games", ct);
        return node?["games"].Deserialize<List<ModGame>>(JsonOpts) ?? new();
    }

    public async Task<List<ModInfo>> ModsAsync(string gameId, CancellationToken ct) =>
        ParseMods(await GetAsync($"mods?game={Uri.EscapeDataString(gameId)}", ct));

    public async Task<List<ModInfo>> SetEnabledAsync(string gameId, IEnumerable<string> modIds, bool enabled, CancellationToken ct) =>
        ParseMods(await PostAsync("enable", new { game = gameId, modIds = modIds.ToArray(), enabled, deploy = true }, ct, TimeSpan.FromMinutes(10)));

    public async Task<List<ModInfo>> RemoveAsync(string gameId, string modId, CancellationToken ct) =>
        ParseMods(await PostAsync("remove", new { game = gameId, modId }, ct, TimeSpan.FromMinutes(5)));

    public Task DeployAsync(string gameId, CancellationToken ct) =>
        PostAsync("deploy", new { game = gameId }, ct, TimeSpan.FromMinutes(10));

    /// <summary>Switch Vortex to the game, setting it up first if it never has been. <c>Active</c>
    /// is false when the switch stopped on one of Vortex's questions (<c>Asking</c> says how many
    /// are showing) or did not finish in time.</summary>
    public async Task<(bool Active, bool Created, int Asking)> ActivateAsync(string gameId, CancellationToken ct)
    {
        var node = await PostAsync("activate", new { game = gameId }, ct, TimeSpan.FromSeconds(60));
        return (node?["active"]?.GetValue<bool>() == true,
                node?["created"]?.GetValue<bool>() == true,
                node?["asking"] is { } a ? a.GetValue<int>() : 0);
    }

    /// <summary>The questions Vortex is showing, with their buttons, and its warnings as one line each.</summary>
    public async Task<(List<ModPrompt> Prompts, List<string> Notices)> AttentionAsync(CancellationToken ct)
    {
        var node = await GetAsync("attention", ct);
        var prompts = node?["dialogs"].Deserialize<List<ModPrompt>>(JsonOpts) ?? new();
        var notices = new List<string>();
        foreach (var n in node?["notifications"]?.AsArray() ?? new JsonArray())
        {
            var t = n?["title"]?.GetValue<string>();
            var m = n?["message"]?.GetValue<string>();
            var line = string.IsNullOrEmpty(t) ? m : string.IsNullOrEmpty(m) ? t : $"{t}: {m}";
            if (!string.IsNullOrEmpty(line)) notices.Add(line);
        }
        return (prompts, notices);
    }

    /// <summary>Press one of a dialog's buttons, by the label /attention reported.</summary>
    public Task AnswerAsync(string dialogId, string action, CancellationToken ct) =>
        PostAsync("answer", new { id = dialogId, action }, ct, TimeSpan.FromSeconds(20));

    /// <summary>Game extensions in Vortex's catalogue whose game looks like this title.</summary>
    public async Task<List<ModExtension>> ExtensionsAsync(string title, CancellationToken ct)
    {
        var node = await GetAsync($"extensions?query={Uri.EscapeDataString(title)}", ct);
        return node?["extensions"].Deserialize<List<ModExtension>>(JsonOpts) ?? new();
    }

    /// <summary>Vortex's extension browser, opened on one extension. The Install button is in there.</summary>
    /// <summary>Tell Vortex where a game it knows is, the way its own "manually set location"
    /// does but without the folder dialog. Vortex checks the folder holds the game's files.</summary>
    public Task DiscoverAsync(string gameId, string path, string? store, CancellationToken ct) =>
        PostAsync("discover", new { game = gameId, path, store }, ct, TimeSpan.FromSeconds(30));

    public Task ShowExtensionAsync(long modId, CancellationToken ct) =>
        PostAsync("extension/show", new { modId }, ct, TimeSpan.FromSeconds(20));

    /// <summary>An extension's own page on Nexus Mods. Its "Mod manager download" is an nxm://
    /// link for the site itself, which Vortex installs as an extension rather than as a mod.</summary>
    public static string ExtensionUrl(long modId) => $"https://www.nexusmods.com/site/mods/{modId}";

    private static List<ModInfo> ParseMods(JsonNode? node) =>
        node?["mods"].Deserialize<List<ModInfo>>(JsonOpts) ?? new();

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        return await ReadAsync(res, ct);
    }

    private async Task<JsonNode?> PostAsync(string path, object body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        // The client's own timeout is the ceiling; a deploy can legitimately outlast it.
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return await ReadAsync(res, cts.Token);
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch { }
        if (!res.IsSuccessStatusCode)
            throw new VortexBridgeException((int)res.StatusCode, node?["error"]?.GetValue<string>() ?? $"Vortex answered {(int)res.StatusCode}");
        return node;
    }
}

public sealed class VortexBridgeException : Exception
{
    public int Status { get; }
    public VortexBridgeException(int status, string message) : base(message) => Status = status;
}
