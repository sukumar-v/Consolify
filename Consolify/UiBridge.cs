using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Consolify.Models;
using Consolify.Services;
using Microsoft.Web.WebView2.Core;

namespace Consolify;

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
    private readonly WindowService _windows;
    private bool _scanning;

    public UiBridge(MainWindow window, CoreWebView2 core, SettingsStore settings, LibraryStore library,
        DisplayService displays, LibraryScanner scanner, GameLaunchService launcher, VirtualKeyboardService keyboard, WindowService windows)
    {
        _window = window;
        _core = core;
        _settings = settings;
        _library = library;
        _displays = displays;
        _scanner = scanner;
        _launcher = launcher;
        _keyboard = keyboard;
        _windows = windows;
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
                _window.PushPadState();
                // Scan on every start, not just an empty library: games get installed and
                // uninstalled between sessions, and nobody on a couch wants to go looking for
                // Settings to find out. The merge keeps playtime, favourites, manual entries and
                // every per-game override, so re-running it costs nothing.
                StartScan();
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
                if (_launcher.GameRunning)
                {
                    // The UI has already asked whether to swap; `replace` is that answer.
                    if (msg["replace"]?.GetValue<bool>() == true) _ = SwapRunningGame(game);
                    else Push(new { type = "toast", message = "A game is already running" });
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

            // ---- Power Wheel / in-game menu ----

            case "listWindows":
                Push(new { type = "windows", windows = _windows.ListWindows(), displays = _displays.GetDisplays() });
                break;

            case "windowAction":
            {
                var act = msg["action"]?.GetValue<string>();
                var handle = msg["handle"]?.GetValue<long>() ?? (long)_window.OverlayTarget;
                var h = new IntPtr(handle);
                switch (act)
                {
                    case "close": _windows.Close(h); break;
                    case "focus":
                        // Switching to a window brings it to the TV with it — the point of picking
                        // one from the couch is to look at it. A window already there is left
                        // alone rather than being re-centred for no reason.
                        if (_settings.Settings.TvDeviceName is { } tv && _windows.DisplayOf(h) != tv)
                            _windows.MoveToDisplay(h, tv);
                        _windows.Focus(h);
                        _window.CloseOverlay(false);
                        break;
                }
                Push(new { type = "toast", message = ActionToast(act) });
                break;
            }

            case "shortcut":
                if (msg["id"]?.GetValue<string>() is { } sid) { _windows.RunShortcut(sid); _window.CloseOverlay(false); }
                break;

            case "suspend":
                _ = _window.Suspend();
                break;

            case "closeOverlay":
                _window.CloseOverlay(msg["refocus"]?.GetValue<bool>() ?? true);
                break;

            case "centerMouse":
                if (_settings.Settings.TvDeviceName is { } tvc) _windows.CenterCursorOn(tvc);
                _window.CloseOverlay(false);
                break;

            case "mouseInGame":
            {
                // Toggled from the Power Wheel rather than only from Settings: the moment you need
                // it is mid-game, when walking to Settings means leaving the game to do it.
                var st = _settings.Settings;
                st.GamepadMouseDuringGame = msg["on"]?.GetValue<bool>() ?? !st.GamepadMouseDuringGame;
                _settings.Save();
                PushState();
                Push(new { type = "toast", message = st.GamepadMouseDuringGame
                    ? "Gamepad mouse forced on while a game runs"
                    : "Gamepad mouse off while a game runs" });
                break;
            }

            case "setRadialActive":
                _window.SetRadialActive(msg["active"]?.GetValue<bool>() ?? false);
                break;

            case "inputMode":
                if (msg["mode"]?.GetValue<string>() is { } im) _window.SetInputMode(im);
                break;

            case "goHome":
                _window.GoHome();
                break;

            case "closeGame":
            {
                // Land on the library first. Asked from the in-game menu, the launcher is a
                // transparent overlay over the game — leaving it up while the game tears down
                // shows the user nothing at all, which reads as "it froze".
                _window.GoHome();

                int n = CloseRunningGame();
                Push(new { type = "toast", message = n > 0 ? "Closing game…" : "No game window to close" });
                break;
            }

            case "resumeGame":
                _window.CloseOverlay(true);
                break;

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
        t.BoostButton = s.BoostButton;
        t.BoostMultiplier = Math.Clamp(s.BoostMultiplier, 1.5, 5.0);
        t.LeftClickButton = s.LeftClickButton;
        t.RightClickButton = s.RightClickButton;
        t.MinimizeCombo = s.MinimizeCombo;
        t.ScreenshotCombo = s.ScreenshotCombo;
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

    public void PushBattery(BatteryState b) =>
        Push(new { type = "battery", present = b.Present, percent = b.Percent, charging = b.Charging, level = b.CoarseLevel });

    /// <summary>
    /// Politely close every visible window the running game owns, then ask its processes
    /// directly — a fullscreen game may own no window we can enumerate. Returns how many things
    /// were asked, so the caller can tell "closing…" from "there was nothing to close".
    /// </summary>
    private int CloseRunningGame()
    {
        int n = 0;
        foreach (var w in _windows.ListWindows())
        {
            var h = new IntPtr(w.Handle);
            if (!_launcher.OwnsWindow(h)) continue;
            _windows.Close(h);
            n++;
        }
        return n + _launcher.RequestClose();
    }

    /// <summary>
    /// Close whatever is running and start <paramref name="next"/> once it has actually gone.
    /// Launching straight away would race the old game's teardown — it still owns the display
    /// mode we are about to change and the foreground we are about to take.
    /// </summary>
    private async Task SwapRunningGame(Game next)
    {
        var outgoing = _launcher.RunningGameId is { } id ? _library.Find(id)?.Title : null;
        Log.Info($"Swapping {outgoing ?? "running game"} -> {next.Title}");

        if (CloseRunningGame() == 0)
        {
            Push(new { type = "toast", message = "Could not find the running game to close" });
            return;
        }

        // Generous: a game that prompts to save, or a launcher-chained title, can take a while.
        if (!await _launcher.WaitForExitAsync(TimeSpan.FromSeconds(25)))
        {
            Push(new { type = "toast", message = $"{outgoing ?? "The game"} didn't close — {next.Title} not started" });
            return;
        }

        Push(new { type = "toast", message = $"Launching {next.Title}…" });
        _launcher.Launch(next);
    }

    private static string ActionToast(string? act) => act switch
    {
        "close" => "Closing window…",
        "focus" => "Brought to the TV",
        _ => "Done"
    };

    public void PushOverlay(string mode, string targetTitle, string? shot) =>
        Push(new
        {
            type = "overlay",
            mode,
            targetTitle,
            shot,
            windows = _windows.ListWindows(),
            displays = _displays.GetDisplays(),
            runningGameId = _launcher.RunningGameId
        });

    public void PushStick(double x, double y) => Push(new { type = "stick", x, y });

    /// <summary>Tell the UI to tear down whatever overlay menu it has open.</summary>
    public void PushDismiss() => Push(new { type = "dismiss" });

    public void PushInputMode(string mode) => Push(new { type = "inputMode", mode });

    public void PushGameState() =>
        Push(new { type = "game", running = _launcher.GameRunning, id = _launcher.RunningGameId });

    public void PushPadEvent(string button) => Push(new { type = "pad", button });

    public void PushPadConnected(bool connected) => Push(new { type = "padConnected", connected });

    private void Push(object payload) =>
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
}
