using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchLauncher.Models;
using CouchLauncher.Services;
using Microsoft.Web.WebView2.Core;

namespace CouchLauncher;

/// <summary>
/// JSON message bridge between the WebView2 UI and the native shell.
/// UI -> host: { cmd: "...", ... }   host -> UI: { type: "...", ... }
/// </summary>
public class UiBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MainWindow _window;
    private readonly CoreWebView2 _core;
    private readonly SettingsStore _settings;
    private readonly LibraryStore _library;
    private readonly DisplayService _displays;
    private readonly LibraryScanner _scanner;
    private readonly GameLaunchService _launcher;
    private readonly VirtualKeyboardService _keyboard;
    private bool _scanning;

    public UiBridge(MainWindow window, CoreWebView2 core, SettingsStore settings, LibraryStore library,
        DisplayService displays, LibraryScanner scanner, GameLaunchService launcher, VirtualKeyboardService keyboard)
    {
        _window = window;
        _core = core;
        _settings = settings;
        _library = library;
        _displays = displays;
        _scanner = scanner;
        _launcher = launcher;
        _keyboard = keyboard;
    }

    public void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); }
        catch { return; }
        var cmd = msg?["cmd"]?.GetValue<string>();
        if (cmd is null) return;

        try { Handle(cmd, msg!); }
        catch (Exception ex)
        {
            Log.Info($"Bridge command '{cmd}' failed: {ex}");
            Push(new { type = "toast", message = $"Something went wrong: {ex.Message}" });
        }
    }

    private void Handle(string cmd, JsonNode msg)
    {
        switch (cmd)
        {
            case "ready":
                PushState();
                if (_library.Games.Count == 0) StartScan();
                break;

            case "launch":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                if (!game.Installed || (game.ExePath is not null && !File.Exists(game.ExePath) && game.Platform is "GOG" or "Manual"))
                {
                    Push(new { type = "toast", message = $"{game.Title} is not installed" });
                    break;
                }
                _launcher.Launch(game);
                break;
            }

            case "rescan":
                StartScan();
                break;

            case "addManual":
                AddManualGame();
                break;

            case "removeGame":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null && _library.Find(id) is { Manual: true })
                {
                    _library.Remove(id);
                    PushState();
                }
                break;
            }

            case "pickCover":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null) PickCover(id);
                break;
            }

            case "saveSettings":
            {
                var incoming = msg["settings"].Deserialize<AppSettings>(JsonOpts);
                if (incoming is null) break;
                var displayChanged = incoming.TvDeviceName != _settings.Settings.TvDeviceName;
                var startupChanged = incoming.LaunchOnStartup != StartupService.IsRegistered();
                CopySettings(incoming);
                _settings.Save();
                if (startupChanged)
                {
                    try { StartupService.SetRegistered(incoming.LaunchOnStartup); }
                    catch (Exception ex) { Push(new { type = "toast", message = $"Startup registration failed: {ex.Message}" }); }
                }
                if (displayChanged) _window.PositionOnTargetDisplay();
                PushState();
                break;
            }

            case "toggleKeyboard":
                _keyboard.Toggle();
                break;

            case "showKeyboard":
                _keyboard.Show();
                break;

            case "hideKeyboard":
                _keyboard.Hide();
                break;

            case "toggleHidden":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.Hidden = !game.Hidden;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = game.Hidden ? $"{game.Title} hidden" : $"{game.Title} restored to the library" });
                break;
            }

            case "toggleFavorite":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.Favorite = !game.Favorite;
                _library.Save();
                PushState();
                break;
            }

            case "createCollection":
            {
                var name = msg["name"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrEmpty(name)) break;
                var colId = _library.CreateCollection(name);
                if (msg["gameId"]?.GetValue<string>() is { } gid)
                    _library.ToggleInCollection(colId, gid);
                PushState();
                Push(new { type = "toast", message = $"Created collection “{name}”" });
                break;
            }

            case "toggleInCollection":
            {
                var colId = msg["collectionId"]?.GetValue<string>();
                var gid = msg["id"]?.GetValue<string>();
                if (colId is null || gid is null) break;
                _library.ToggleInCollection(colId, gid);
                PushState();
                break;
            }

            case "deleteCollection":
            {
                var colId = msg["id"]?.GetValue<string>();
                if (colId is null) break;
                _library.DeleteCollection(colId);
                PushState();
                break;
            }

            case "setArgs":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.Args = msg["args"]?.GetValue<string>()?.Trim() is { Length: > 0 } a ? a : null;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = game.Args is null ? "Launch arguments cleared" : $"Launch arguments set: {game.Args}" });
                break;
            }

            case "pickExe":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null) PickExe(id);
                break;
            }

            case "launchViaStore":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null) break;
                game.PreferDirectLaunch = false;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = $"{game.Title} will launch through {game.Platform} again" });
                break;
            }

            case "exitApp":
                _window.ExitApp();
                break;

            case "log":
                Log.Info($"UI: {msg["msg"]?.GetValue<string>()}");
                break;
        }
    }

    private void CopySettings(AppSettings s)
    {
        var t = _settings.Settings;
        t.TvDeviceName = s.TvDeviceName;
        t.SwitchPrimaryOnLaunch = s.SwitchPrimaryOnLaunch;
        t.RepositionGameWindow = s.RepositionGameWindow;
        t.KeepFocus = s.KeepFocus;
        t.LaunchOnStartup = s.LaunchOnStartup;
        t.GamepadMouseEnabled = s.GamepadMouseEnabled;
        t.GamepadMouseDuringGame = s.GamepadMouseDuringGame;
        t.Deadzone = Math.Clamp(s.Deadzone, 0.05, 0.40);
        t.Sensitivity = Math.Clamp(s.Sensitivity, 0.2, 3.0);
        t.AccelExponent = Math.Clamp(s.AccelExponent, 1.0, 3.0);
        t.LeftClickButton = s.LeftClickButton;
        t.RightClickButton = s.RightClickButton;
        t.KeyboardToggleButton = s.KeyboardToggleButton;
        t.KeyboardToggleHoldMs = Math.Clamp(s.KeyboardToggleHoldMs, 200, 2000);
        t.KeyboardApp = s.KeyboardApp;
    }

    private void StartScan()
    {
        if (_scanning) return;
        _scanning = true;
        Push(new { type = "scanning", busy = true });
        Task.Run(() =>
        {
            try
            {
                var found = _scanner.ScanAll();
                _library.MergeScanned(found);
            }
            finally
            {
                _scanning = false;
                _window.Dispatcher.BeginInvoke(() =>
                {
                    Push(new { type = "scanning", busy = false });
                    PushState();
                });
            }
        });
    }

    private void AddManualGame()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the game executable",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (!ShowDialog(dlg)) return;

        var exe = dlg.FileName;
        var game = new Game
        {
            Id = $"manual:{Guid.NewGuid():N}",
            Title = Path.GetFileNameWithoutExtension(exe),
            Platform = "Manual",
            Manual = true,
            ExePath = exe,
            InstallDir = Path.GetDirectoryName(exe),
            Installed = true
        };
        try { game.SizeBytes = new FileInfo(exe).Length; } catch { }

        // Optional cover art
        var art = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art (optional — press Cancel to skip)",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
        };
        if (ShowDialog(art))
            game.CoverFile = CopyCover(art.FileName, game.Id);

        _library.AddManual(game);
        PushState();
        Push(new { type = "toast", message = $"Added {game.Title}" });
    }

    /// <summary>Show a modal dialog with always-on-top suspended, so it can't open behind the launcher.</summary>
    private bool ShowDialog(Microsoft.Win32.CommonDialog dlg)
    {
        _window.BeginModalDialog();
        try { return dlg.ShowDialog(_window) == true; }
        finally { _window.EndModalDialog(); }
    }

    private void PickExe(string id)
    {
        var game = _library.Find(id);
        if (game is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the executable to launch for {game.Title}",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (game.InstallDir is not null && Directory.Exists(game.InstallDir))
            dlg.InitialDirectory = game.InstallDir;
        if (!ShowDialog(dlg)) return;

        game.ExePath = dlg.FileName;
        if (game.Platform is "Steam" or "Epic") game.PreferDirectLaunch = true;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = $"{game.Title} now launches {Path.GetFileName(dlg.FileName)} directly" });
    }

    private void PickCover(string id)
    {
        var game = _library.Find(id);
        if (game is null) return;
        var art = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose cover art for {game.Title}",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
        };
        if (!ShowDialog(art)) return;
        game.CoverFile = CopyCover(art.FileName, game.Id);
        _library.Save();
        PushState();
    }

    private static string? CopyCover(string source, string gameId)
    {
        try
        {
            var safe = gameId.Replace(':', '_');
            var dest = Path.Combine(Paths.CoversDir, safe + Path.GetExtension(source).ToLowerInvariant());
            File.Copy(source, dest, overwrite: true);
            return Path.GetFileName(dest);
        }
        catch { return null; }
    }

    // ---- host -> UI pushes ----

    public void PushState()
    {
        var s = _settings.Settings;
        Push(new
        {
            type = "state",
            games = _library.Games,
            collections = _library.Collections,
            settings = s,
            displays = _displays.GetDisplays(),
            startupRegistered = StartupService.IsRegistered(),
            gameRunning = _launcher.GameRunning,
            runningGameId = _launcher.RunningGameId,
            scanning = _scanning
        });
    }

    public void PushBattery(byte type, byte level) =>
        Push(new { type = "battery", batteryType = type, level });

    public void PushInputMode(string mode) => Push(new { type = "inputMode", mode });

    public void PushGameState() =>
        Push(new { type = "game", running = _launcher.GameRunning, id = _launcher.RunningGameId });

    public void PushPadEvent(string button) => Push(new { type = "pad", button });

    public void PushPadConnected(bool connected) => Push(new { type = "padConnected", connected });

    private void Push(object payload) =>
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
}
