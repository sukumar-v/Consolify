using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Loungepad.Models;
using Loungepad.Services;
using Microsoft.Web.WebView2.Core;

namespace Loungepad;

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
    private readonly ThemeService _themes;
    private readonly UpdateService _updates;
    private readonly MetadataService _metadata = new();
    private readonly SteamAccountService _steam = new();
    private readonly GamePassCatalogService _gamePass = new();
    /// <summary>The stores one signs in to, keyed as the page names them. Filled in the
    /// constructor because Xbox reads a setting.</summary>
    private readonly Dictionary<string, IStoreAccount> _accounts = new();
    /// <summary>The mod manager side: Vortex, through the bridge extension. Built here because it
    /// reads the Vortex path setting.</summary>
    private readonly ModService _mods;
    /// <summary>The Mods screen's request in flight. Opening another game's list cancels the last
    /// one, so a slow Vortex start cannot answer for a screen that has since moved on.</summary>
    private CancellationTokenSource? _modsCts;
    private bool _signingIn;
    private System.Threading.Timer? _installPoll;
    private string? _pendingInstall;
    private DateTime _pollUntil;
    private readonly List<FileSystemWatcher> _manifestWatchers = new();
    private System.Threading.Timer? _manifestTimer;
    private bool _scanning;
    private bool _enriching;

    public UiBridge(MainWindow window, CoreWebView2 core, SettingsStore settings, LibraryStore library,
        DisplayService displays, LibraryScanner scanner, GameLaunchService launcher, VirtualKeyboardService keyboard, WindowService windows, ThemeService themes,
        UpdateService updates)
    {
        _updates = updates;
        _window = window;
        _core = core;
        _settings = settings;
        _library = library;
        _displays = displays;
        _scanner = scanner;
        _launcher = launcher;
        _keyboard = keyboard;
        _windows = windows;
        _themes = themes;
        foreach (var account in new IStoreAccount[]
                 {
                     new EpicAccountClient(),
                     new GogAccountClient(),
                     new XboxAccountClient(() => _settings.Settings.XboxClientId),
                 })
            _accounts[account.Store] = account;
        _mods = new ModService(() => _settings.Settings.VortexPath);
        StartInstallWatcher();
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
                if (App.UpdatedFrom is not null)
                {
                    PushToast($"Updated to Loungepad {UpdateService.Format(UpdateService.Current)}");
                    App.UpdatedFrom = null;
                }
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
                // Everything that can be wrong with an emulated launch is known before anything
                // starts -- no emulator, its exe gone, the ROM gone, a core never chosen -- and
                // each is something the person can fix, so it is said here rather than logged.
                if (game.Emulated && _launcher.ResolveEmulated(game, out var problem) is null)
                {
                    Push(new { type = "toast", message = problem ?? "This game cannot be started" });
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
                StartScan(force: true);
                break;

            case "storeSignIn":
                if (msg["store"]?.GetValue<string>() is { } signIn && _accounts.TryGetValue(signIn, out var accountIn))
                    _ = SignInAsync(accountIn);
                break;

            case "storeSignOut":
                if (msg["store"]?.GetValue<string>() is { } signOut && _accounts.TryGetValue(signOut, out var accountOut))
                {
                    accountOut.SignOut();
                    Push(new { type = "toast", message = $"Signed out of {accountOut.DisplayName}" });
                    PushState();
                    // The next scan is what takes the store's games out of the library.
                    StartScan(force: true);
                }
                break;

            // The store's own install flow: Steam and Galaxy put up a dialog with the size and the
            // drive, Epic's launcher and the Microsoft Store open the game's page with an Install
            // button. The manifest watcher or the poll below turns the tile playable afterwards.
            case "install":
            {
                var id = msg["id"]?.GetValue<string>();
                var game = id is null ? null : _library.Find(id);
                if (game is null || game.Installed) break;
                // The URI was written by whichever service listed the game, never by the page --
                // the page only names the game. The scheme check keeps a hand-edited library.json
                // from turning this into "run anything".
                if (game.InstallUri is not { } uri
                    || !InstallSchemes.Any(s => uri.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    Push(new { type = "toast", message = $"{game.Title} has to be installed from {game.Platform}" });
                    break;
                }
                try
                {
                    // The launcher is topmost on the TV, so the store's window would open behind
                    // it and look like nothing happened. Step aside the way a launch does; the
                    // minimize combo brings the launcher back.
                    _window.Park();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
                    Log.Info($"Install requested for {game.Title} ({uri})");
                    BeginInstallPolling(game.Id);
                }
                catch (Exception ex)
                {
                    _window.Unpark();
                    Push(new { type = "toast", message = $"Could not ask {game.Platform} to install: {ex.Message}" });
                }
                break;
            }

            // Credentials can be added long after a game was first looked up and written off, and
            // a title that matched nothing today may match tomorrow. Clearing the timestamps is
            // what makes the next pass reconsider everything rather than honouring the fortnight.
            case "refreshMetadata":
                foreach (var g in _library.Games) g.MetadataFetched = null;
                _ = EnrichMetadata();
                break;

            // Explorer on the themes folder: "put a folder here" is the whole install story,
            // so the launcher may as well open the place you put it.
            case "openThemesFolder":
                try
                {
                    Directory.CreateDirectory(Paths.ThemesDir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.ThemesDir) { UseShellExecute = true });
                }
                catch (Exception ex) { Push(new { type = "toast", message = $"Could not open the themes folder: {ex.Message}" }); }
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

            // "slot" says which picture: the portrait cover, or the landscape tile the library
            // grid is made of. Absent means the cover, which is what the only caller used to mean.
            case "pickCover":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null) PickArt(id, msg["slot"]?.GetValue<string>() ?? "cover");
                break;
            }

            case "resetArt":
            {
                var id = msg["id"]?.GetValue<string>();
                if (id is not null) ResetArt(id, msg["slot"]?.GetValue<string>() ?? "cover");
                break;
            }

            case "saveSettings":
            {
                var incoming = msg["settings"].Deserialize<AppSettings>(JsonOpts);
                if (incoming is null) break;
                var displayChanged = incoming.TvDeviceName != _settings.Settings.TvDeviceName;
                var startupChanged = incoming.LaunchOnStartup != StartupService.IsRegistered();
                // Flipping the Steam library on should show the games now, not on the next start,
                // and flipping it off should take them away just as promptly. A new key is a new
                // route to the same answer, so it re-asks too.
                var cur = _settings.Settings;
                var storesChanged = incoming.SteamShowOwned != cur.SteamShowOwned
                                    || (incoming.SteamApiKey ?? "").Trim() != cur.SteamApiKey
                                    || incoming.GamePassCatalog != cur.GamePassCatalog
                                    || incoming.DetectEmulators != cur.DetectEmulators;
                // Turned on: look now, rather than leave the row saying nothing for six hours.
                var autoUpdateOn = incoming.AutoUpdate && !cur.AutoUpdate;
                CopySettings(incoming);
                _settings.Save();
                if (storesChanged) StartScan(force: true);
                if (autoUpdateOn) _ = _updates.CheckAsync(userAsked: false);
                if (startupChanged)
                {
                    try { StartupService.SetRegistered(incoming.LaunchOnStartup); }
                    catch (Exception ex) { Push(new { type = "toast", message = $"Startup registration failed: {ex.Message}" }); }
                }
                if (displayChanged) _window.PositionOnTargetDisplay();
                // The size slider is only worth having if it moves the keyboard you are looking at.
                _window.RefreshBuiltinKeyboard();
                PushState();
                break;
            }

            // Every setting back to the value a fresh install would have. Deliberately goes
            // through the same path as a save rather than writing the file directly, so the
            // startup entry and the on-screen keyboard both follow it -- resetting "Launch at
            // login" to false has to actually unregister it.
            //
            // The one thing kept is the TV display. Which screen is the television is a fact
            // about the room rather than a preference, and clearing it moves the launcher off
            // the screen the user is looking at -- a restore they would have to undo blind.
            case "resetSettings":
            {
                var defaults = new AppSettings { TvDeviceName = _settings.Settings.TvDeviceName };
                var startupChanged = defaults.LaunchOnStartup != StartupService.IsRegistered();
                CopySettings(defaults);
                _settings.Save();
                if (startupChanged)
                {
                    try { StartupService.SetRegistered(defaults.LaunchOnStartup); }
                    catch (Exception ex) { Push(new { type = "toast", message = $"Startup registration failed: {ex.Message}" }); }
                }
                _window.RefreshBuiltinKeyboard();
                PushState();
                Push(new { type = "toast", message = "Settings restored to defaults" });
                break;
            }

            // ---- Power Wheel / in-game menu ----

            case "listWindows":
            {
                var open = _windows.ListWindows();
                Push(new { type = "windows", windows = open, displays = _displays.GetDisplays() });
                StartThumbnails(open);
                break;
            }

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

            // Buttons the page wants delivered to it rather than spent on something else first --
            // View on the library, where it opens search even when it is also the keyboard toggle.
            case "claimButtons":
                _window.SetUiClaimedButtons(msg["buttons"]?.AsArray()
                    .Select(n => n?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!)
                    .ToArray() ?? Array.Empty<string>());
                break;

            // Keyboard focus into the page, for a text field about to be typed into. Without it the
            // launcher has no focused element at all and keystrokes -- the on-screen keyboard's
            // included -- go nowhere.
            case "focusPage":
                _window.FocusPage();
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

            // A game in several stores is hidden as a whole: the page sends every copy, so hiding
            // the Steam one does not just bring the Xbox one out from behind it.
            case "setHidden":
            {
                var hide = msg["hidden"]?.GetValue<bool>() ?? true;
                var ids = msg["ids"]?.AsArray().Select(n => n?.GetValue<string>()).Where(x => x is not null).ToList() ?? new();
                var changed = ids.Select(i => _library.Find(i!)).Where(x => x is not null).ToList();
                if (changed.Count == 0) break;
                foreach (var x in changed) x!.Hidden = hide;
                _library.Save();
                PushState();
                var name = changed[0]!.Title;
                Push(new { type = "toast", message = hide ? $"{name} hidden" : $"{name} restored to the library" });
                break;
            }

            // Which store a game in several of them launches from. One flag across the copies,
            // so the others are cleared in the same save.
            case "preferEdition":
            {
                var id = msg["id"]?.GetValue<string>();
                var chosen = id is null ? null : _library.Find(id);
                if (chosen is null) break;
                foreach (var sib in msg["siblings"]?.AsArray() ?? new JsonArray())
                    if (sib?.GetValue<string>() is { } sibId && _library.Find(sibId) is { } other) other.PreferredEdition = false;
                chosen.PreferredEdition = true;
                _library.Save();
                PushState();
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

            case "updateCheck":
                _window.CheckForUpdates();
                break;

            case "updateInstall":
                _window.InstallUpdate();
                break;

            case "exitApp":
                _window.ExitApp();
                break;

            case "log":
                Log.Info($"UI: {msg["msg"]?.GetValue<string>()}");
                break;

            // ---- Mods ----
            // Every answer is one "mods" push carrying the whole screen for one game: which state
            // it is in (Vortex missing, needs a restart, game not set up, ready…), the sentence
            // that explains it, and the list. The page never has to assemble it from pieces.

            case "modsOpen":
                if (ModsGame(msg) is { } gOpen) _ = ModsRunAsync(gOpen, ct => _mods.OpenAsync(gOpen, ct));
                break;

            case "modsToggle":
            {
                var modId = msg["modId"]?.GetValue<string>();
                var enabled = msg["enabled"]?.GetValue<bool>() ?? true;
                if (ModsGame(msg) is { } gTog && modId is not null)
                    _ = ModsRunAsync(gTog, ct => _mods.SetEnabledAsync(gTog, modId, enabled, ct));
                break;
            }

            case "modsRemove":
            {
                var modId = msg["modId"]?.GetValue<string>();
                if (ModsGame(msg) is { } gRem && modId is not null)
                    _ = ModsRunAsync(gRem, ct => _mods.RemoveAsync(gRem, modId, ct));
                break;
            }

            // One of the buttons on a question Vortex is showing -- "install this anyway?" is the
            // usual one -- pressed from the sofa instead of walking to the desk.
            case "modsAnswer":
            {
                var dialogId = msg["dialogId"]?.GetValue<string>();
                var action = msg["action"]?.GetValue<string>();
                if (ModsGame(msg) is { } gAns && dialogId is not null && action is not null)
                    _ = ModsRunAsync(gAns, ct => _mods.AnswerAsync(gAns, dialogId, action, ct));
                break;
            }

            // Vortex's own window, on the TV: for the one-time setup of a game, and for anything
            // Vortex is asking about that a list across the room cannot answer.
            case "modsManage":
                if (ModsGame(msg) is { } gMan) _ = ModsManageAsync(gMan);
                break;

            case "modsShowVortex":
                _ = ShowVortexAsync();
                break;

            case "modsRestartVortex":
                if (ModsGame(msg) is { } gRes) _ = ModsRestartAsync(gRes);
                break;

            // The page where the installer is, in the default browser. Not downloaded and run
            // from here: an installer is the one thing worth a mouse in hand.
            case "modsGetVortex":
                try
                {
                    _window.Park();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VortexBackend.DownloadUrl) { UseShellExecute = true });
                    Log.Info("Opened the Vortex download page");
                }
                catch (Exception ex)
                {
                    _window.Unpark();
                    Push(new { type = "toast", message = $"Could not open the browser: {ex.Message}" });
                }
                break;

            // Where on nexusmods.com to open: a mod's own page when the page names one, an
            // extension's page for a game Vortex cannot see yet, else the game's section.
            case "modsBrowse":
            {
                if (ModsGame(msg) is not { } gBr) break;
                var vg = _mods.Cached(gBr);
                string? url = null;
                if (LongOf(msg["extensionModId"]) is > 0 and var ext) url = VortexBackend.ExtensionUrl(ext);
                else if (msg["site"]?.GetValue<bool>() == true) url = "https://www.nexusmods.com/site/mods/";
                else if (LongOf(msg["nexusModId"]) is > 0 and var nexusId)
                    url = ModService.ModUrl(vg, nexusId, msg["nexusDomain"]?.GetValue<string>());
                _ = BrowseModsAsync(gBr, url ?? ModService.NexusUrl(vg));
                break;
            }

            // Vortex's extension browser, on the extension that would teach it this game, and
            // Vortex on the TV so its Install button can be pressed.
            case "modsExtension":
                if (ModsGame(msg) is { } gExt && LongOf(msg["modId"]) is > 0 and var extId)
                    _ = ModsExtensionAsync(gExt, extId);
                break;

            // ---- Emulators and ROM folders ----
            // The host owns these lists outright: every change comes through one of the commands
            // below and is saved into library.json, and the page never sends them back. They are
            // deliberately NOT part of saveSettings, so a settings push from an older page cannot
            // wipe them and "Restore default settings" leaves them alone.

            case "emuAdd":
                AddEmulator();
                break;

            case "emuPickExe":
                if (msg["id"]?.GetValue<string>() is { } emuExeId) PickEmulatorExe(emuExeId);
                break;

            case "emuUpdate":
            {
                var emu = _library.FindEmulator(msg["id"]?.GetValue<string>());
                if (emu is null) break;
                if (msg["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name) emu.Name = name;
                // Arguments may legitimately be emptied: that is "just the ROM", which Resolve
                // supplies. The property holds the template; an empty one is the default.
                if (msg["args"] is { } argsNode) emu.Args = argsNode.GetValue<string>()?.Trim() ?? "";
                _library.Save();
                PushState();
                break;
            }

            case "emuRemove":
            {
                var emu = _library.FindEmulator(msg["id"]?.GetValue<string>());
                if (emu is null) break;
                _library.RemoveEmulator(emu.Id);
                PushState();
                Push(new { type = "toast", message = $"Removed {emu.Name}. Folders that used it need a new emulator" });
                break;
            }

            case "romFolderPick":
                PickRomFolder();
                break;

            case "romFolderAdd":
                AddRomFolder(msg["path"]?.GetValue<string>(), msg["platformId"]?.GetValue<string>(),
                    msg["emulatorId"]?.GetValue<string>());
                break;

            case "romFolderUpdate":
            {
                var folder = _library.FindRomFolder(msg["id"]?.GetValue<string>());
                if (folder is null) break;
                var rescan = false;
                if (EmulatedPlatforms.Find(msg["platformId"]?.GetValue<string>()) is { } platform
                    && platform.Id != folder.PlatformId)
                {
                    folder.PlatformId = platform.Id;
                    folder.Core = null;   // a core is for one system; the new one picks its own
                    rescan = true;
                }
                if (msg["emulatorId"] is { } emuNode)
                {
                    var emuId = emuNode.GetValue<string>();
                    folder.EmulatorId = _library.FindEmulator(emuId)?.Id;
                    // A new emulator may want a core, and the old core was for the old one.
                    folder.Core = null;
                    if (_library.FindEmulator(folder.EmulatorId) is { } newEmu
                        && EmulatedPlatforms.Find(folder.PlatformId) is { } p)
                        folder.Core = EmulatorLaunch.SuggestCore(newEmu, p);
                }
                if (msg["args"] is { } fArgs) folder.Args = fArgs.GetValue<string>()?.Trim() is { Length: > 0 } a ? a : null;
                if (msg["extensions"] is { } extNode)
                {
                    var list = (extNode.GetValue<string>() ?? "")
                        .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
                        .Where(e => e.Length > 0 && e.All(c => char.IsAsciiLetterOrDigit(c)))
                        .Distinct().ToList();
                    folder.Extensions = list.Count > 0 ? list : null;
                    rescan = true;
                }
                _library.Save();
                PushState();
                if (rescan) StartScan();
                break;
            }

            case "romFolderPickCore":
                if (msg["id"]?.GetValue<string>() is { } coreFolderId) PickCore(coreFolderId);
                break;

            case "romFolderRemove":
            {
                var folder = _library.FindRomFolder(msg["id"]?.GetValue<string>());
                if (folder is null) break;
                _library.RemoveRomFolder(folder.Id);
                PushState();
                Push(new { type = "toast", message = $"Removed {folder.Path}. Its games leave the library on this scan" });
                StartScan();
                break;
            }

            // A ROM's title is a guess from its file name, and the guess is what the metadata
            // lookup runs on -- so a rename is also the way to make a wrongly-matched (or
            // unmatched) game fetch again, which is why the stamp is cleared.
            case "setTitle":
            {
                var game = _library.Find(msg["id"]?.GetValue<string>() ?? "");
                var title = msg["title"]?.GetValue<string>()?.Trim();
                if (game is null || string.IsNullOrEmpty(title) || title.Length > 200) break;
                game.Title = title;
                game.TitleEdited = true;
                game.MetadataFetched = null;
                _library.Save();
                PushState();
                Push(new { type = "toast", message = $"Renamed to {title}. Fetching its details again" });
                _ = EnrichMetadata();
                break;
            }

            // Which emulator runs this one game. An empty id puts it back on its folder's.
            case "setEmulator":
            {
                var game = _library.Find(msg["id"]?.GetValue<string>() ?? "");
                if (game is null || !game.Emulated) break;
                var emu = _library.FindEmulator(msg["emulatorId"]?.GetValue<string>());
                game.EmulatorId = emu?.Id;
                _library.Save();
                PushState();
                var now = _library.EmulatorFor(game);
                Push(new { type = "toast", message = now is null
                    ? $"{game.Title} has no emulator to run with"
                    : $"{game.Title} now runs with {now.Name}" });
                break;
            }
        }
    }

    // ---- Mods ----

    /// <summary>A whole number off the page, or null for anything else -- including JSON null,
    /// which GetValue throws on.</summary>
    private static long? LongOf(JsonNode? node)
    {
        try { return node?.GetValue<long>(); }
        catch { return null; }
    }

    private Game? ModsGame(JsonNode msg)
    {
        var game = _library.Find(msg["id"]?.GetValue<string>() ?? "");
        if (game is null) return null;
        if (!ModService.Eligible(game))
        {
            Push(new { type = "toast", message = game.Installed ? "Mods are for games on this PC's disk" : $"{game.Title} is not installed" });
            return null;
        }
        return game;
    }

    /// <summary>Run one Mods request off the UI thread and push its screen. A newer request for
    /// any game cancels this one; the page ignores an answer for a game it is no longer showing.</summary>
    private async Task ModsRunAsync(Game game, Func<CancellationToken, Task<ModsView>> work)
    {
        _modsCts?.Cancel();
        var cts = _modsCts = new CancellationTokenSource();
        try
        {
            var view = await Task.Run(() => work(cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) PushMods(view);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            Log.Info($"Mods: {ex}");
            if (!cts.IsCancellationRequested)
                PushMods(new ModsView { GameId = game.Id, State = "error", Message = ex.Message, Vortex = _mods.Status() });
        }
    }

    private void PushMods(ModsView v) => Push(new
    {
        type = "mods",
        gameId = v.GameId, state = v.State, message = v.Message,
        vortex = v.Vortex, game = v.Game, mods = v.Mods, prompts = v.Prompts, notices = v.Notices,
        extensions = v.Extensions, downloadUrl = v.DownloadUrl,
    });

    /// <summary>
    /// Set the game up in Vortex and switch to it. Vortex's first-time questions come back as
    /// prompt rows on the Mods screen, so its window is only brought to the TV when it stopped
    /// on something the screen cannot carry.
    /// </summary>
    private async Task ModsManageAsync(Game game)
    {
        _modsCts?.Cancel();
        var cts = _modsCts = new CancellationTokenSource();
        try
        {
            var (view, needsWindow) = await Task.Run(() => _mods.ManageAsync(game, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            PushMods(view);
            if (needsWindow) await ShowVortexAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Info($"Mods manage: {ex}");
            if (!cts.IsCancellationRequested)
                PushMods(new ModsView { GameId = game.Id, State = "error", Message = ex.Message, Vortex = _mods.Status() });
        }
    }

    /// <summary>
    /// Vortex's window to the TV and the foreground, with the launcher parked behind it the way it
    /// is for a store's install dialog. A Vortex sitting in the tray has no window to find, so it
    /// is started again -- its second instance only shows the first -- and given a moment.
    /// </summary>
    private async Task ShowVortexAsync()
    {
        if (_mods.Vortex.ExePath is null && _mods.Vortex.Locate() is null)
        {
            Push(new { type = "toast", message = "Vortex is not installed" });
            return;
        }
        var hwnd = VortexBackend.MainWindow();
        if (hwnd == IntPtr.Zero)
        {
            _mods.Vortex.Start("");
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (hwnd == IntPtr.Zero && DateTime.UtcNow < until)
            {
                await Task.Delay(500);
                hwnd = VortexBackend.MainWindow();
            }
        }
        if (hwnd == IntPtr.Zero)
        {
            Push(new { type = "toast", message = "Vortex's window did not appear" });
            return;
        }
        _window.Park();
        if (_settings.Settings.TvDeviceName is { } tv && _windows.DisplayOf(hwnd) != tv) _windows.MoveToDisplay(hwnd, tv);
        _windows.Focus(hwnd);
        Log.Info("Brought Vortex to the TV");
    }

    private async Task ModsRestartAsync(Game game)
    {
        Push(new { type = "toast", message = "Restarting Vortex…" });
        var ok = await Task.Run(() => _mods.Vortex.RestartAsync(CancellationToken.None));
        if (!ok) Push(new { type = "toast", message = "Vortex did not close. Close it yourself, then try again" });
        await ModsRunAsync(game, ct => _mods.OpenAsync(game, ct));
    }

    /// <summary>
    /// nexusmods.com for the game, in a window of its own over the launcher, driven the way the
    /// store sign-ins are: the stick is the mouse, the keyboard toggle types. A "Mod manager
    /// download" click on the site is an nxm:// link, which the window hands to Vortex instead of
    /// letting Windows put up a protocol prompt; Vortex downloads and installs it, and the list is
    /// re-read when the window closes.
    /// </summary>
    private async Task BrowseModsAsync(Game game, string url)
    {
        if (_signingIn) return;
        if (_mods.Vortex.ExePath is null && _mods.Vortex.Locate() is null)
        {
            Push(new { type = "toast", message = "Install Vortex first: it is what downloads and installs the mods" });
            return;
        }
        _signingIn = true;
        _window.BeginModalDialog();
        var sent = 0;
        try
        {
            var s = _settings.Settings;
            await StoreLoginWindow.RunAsync(_window, $"Nexus Mods — {game.Title}", AccountStore.ProfileDir("nexus"), url,
                _ => Task.FromResult(false), visible: true, timeout: TimeSpan.FromHours(4),
                browse: new BrowseOptions
                {
                    Hint = "Mod manager download on a mod's Files tab sends it straight to Vortex. Done when you have what you want.",
                    CloseLabel = "Done",
                    OnExternalUri = uri =>
                    {
                        if (_mods.Vortex.Install(uri)) { sent++; Push(new { type = "toast", message = "Sent to Vortex. It downloads and installs in the background" }); }
                        else Push(new { type = "toast", message = "That link is not one Vortex can take" });
                    },
                    ShowKeyboard = () => _keyboard.Toggle(),
                    KeyboardButton = s.KeyboardToggleButton,
                    KeyboardHold = string.Equals(s.KeyboardToggleMode, "Hold", StringComparison.OrdinalIgnoreCase),
                    RegisterPadHandler = h => _window.SetModalPadHandler(h),
                    PadFamily = _window.PadFamily,
                    WatchPadFamily = w => _window.WatchPadFamily(w),
                });
        }
        catch (Exception ex) { Log.Info($"Nexus browse: {ex}"); }
        finally
        {
            _window.SetModalPadHandler(null);
            _window.WatchPadFamily(null);
            _window.EndModalDialog();
            _signingIn = false;
        }
        Log.Info($"Nexus browse closed; {sent} link(s) handed to Vortex; foreground is now {NativeMethodsForeground()}");
        await ModsRunAsync(game, ct => _mods.OpenAsync(game, ct));
    }

    /// <summary>For the log line after a browse window closes: which window Windows handed the
    /// foreground to, since "the pad stopped working" reports come down to exactly that.</summary>
    private static string NativeMethodsForeground()
    {
        var fg = Loungepad.Interop.NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return "nothing";
        Loungepad.Interop.NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId ? "the launcher" : $"another process (pid {pid})";
    }

    private async Task ModsExtensionAsync(Game game, long modId)
    {
        Push(new { type = "toast", message = "Asking Vortex…" });
        try
        {
            var ok = await Task.Run(() => _mods.ShowExtensionAsync(modId, CancellationToken.None));
            if (!ok) { Push(new { type = "toast", message = "Vortex is not answering; try again in a moment" }); return; }
            await ShowVortexAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"Mods extension: {ex}");
            Push(new { type = "toast", message = $"Vortex could not be asked: {ex.Message}" });
        }
    }

    // ---- Emulators and ROM folders ----

    private void AddEmulator()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the emulator's program (retroarch.exe, Dolphin.exe, pcsx2-qt.exe…)",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        // The page may be in the middle of adding a ROM folder and waiting on this; a cancel
        // has to be reported too, or it waits forever with nothing on screen.
        if (!ShowDialog(dlg)) { Push(new { type = "emuAdded", id = (string?)null }); return; }

        var exe = dlg.FileName;
        if (_library.Emulators.FirstOrDefault(e => string.Equals(e.ExePath, exe, StringComparison.OrdinalIgnoreCase)) is { } dup)
        {
            Push(new { type = "toast", message = $"{dup.Name} is already set up" });
            Push(new { type = "emuAdded", id = dup.Id });
            return;
        }

        // A known exe brings its name, its command line and the systems it runs; anything else
        // is named after its file and started with the ROM's path and nothing more.
        var preset = EmulatorPresets.Detect(exe);
        var emu = new EmulatorDef
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = preset?.Name ?? Path.GetFileNameWithoutExtension(exe),
            ExePath = exe,
            Args = preset?.Args ?? "\"{rom}\"",
            Preset = preset?.Key,
            Platforms = preset?.Platforms.ToList() ?? new List<string>(),
        };
        _library.AddEmulator(emu);
        PushState();
        Push(new { type = "emuAdded", id = emu.Id });
        Push(new { type = "toast", message = preset is null
            ? $"Added {emu.Name}. Check its launch arguments under Settings → Library"
            : $"Added {emu.Name}" });
    }

    private void PickEmulatorExe(string id)
    {
        var emu = _library.FindEmulator(id);
        if (emu is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the program for {emu.Name}",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (Path.GetDirectoryName(emu.ExePath) is { } dir && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (!ShowDialog(dlg)) return;
        emu.ExePath = dlg.FileName;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = $"{emu.Name} now runs {Path.GetFileName(dlg.FileName)}" });
    }

    /// <summary>The first step of adding a ROM folder. The rest -- which system, which emulator --
    /// is asked on the page, where a gamepad can answer; the folder's name seeds the system.</summary>
    private void PickRomFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder of ROMs for one system" };
        if (!ShowDialog(dlg)) return;
        var path = dlg.FolderName;
        if (_library.RomFolders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Push(new { type = "toast", message = "That folder is already in the library" });
            return;
        }
        Push(new { type = "romFolderPicked", path, platformId = EmulatedPlatforms.Guess(Path.GetFileName(path.TrimEnd('\\', '/'))) });
    }

    private void AddRomFolder(string? path, string? platformId, string? emulatorId)
    {
        var platform = EmulatedPlatforms.Find(platformId);
        if (path is null || !Directory.Exists(path) || platform is null)
        {
            Push(new { type = "toast", message = "That folder could not be added" });
            return;
        }
        var emu = _library.FindEmulator(emulatorId);
        var folder = new RomFolderDef
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Path = path,
            PlatformId = platform.Id,
            EmulatorId = emu?.Id,
        };

        // RetroArch needs a core per system. Take the first of the platform's known cores that
        // is installed, and only open a dialog when none of them is.
        var needsCore = emu is not null && emu.Args.Contains("{core}", StringComparison.OrdinalIgnoreCase);
        if (needsCore)
        {
            folder.Core = EmulatorLaunch.SuggestCore(emu!, platform) ?? PickCoreDialog(emu!, platform);
        }

        _library.AddRomFolder(folder);
        PushState();
        var core = folder.Core is null ? "" : $" with {Path.GetFileNameWithoutExtension(folder.Core)}";
        Push(new { type = "toast", message = needsCore && folder.Core is null
            ? $"Added {platform.Name}. Choose a core for it under Settings → Library before playing"
            : $"Added {platform.Name}{core}. Scanning for games…" });
        StartScan();
    }

    private void PickCore(string folderId)
    {
        var folder = _library.FindRomFolder(folderId);
        var emu = _library.FindEmulator(folder?.EmulatorId);
        var platform = EmulatedPlatforms.Find(folder?.PlatformId);
        if (folder is null || emu is null || platform is null) return;
        var core = PickCoreDialog(emu, platform);
        if (core is null) return;
        folder.Core = core;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = $"{platform.Name} now runs on {Path.GetFileNameWithoutExtension(core)}" });
    }

    private string? PickCoreDialog(EmulatorDef emu, EmulatedPlatforms.Def platform)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the {emu.Name} core for {platform.Name}",
            Filter = "Cores (*_libretro.dll)|*_libretro.dll|Libraries (*.dll)|*.dll|All files (*.*)|*.*"
        };
        if (EmulatorLaunch.CoresDir(emu) is { } cores) dlg.InitialDirectory = cores;
        return ShowDialog(dlg) ? dlg.FileName : null;
    }

    private void CopySettings(AppSettings s)
    {
        var t = _settings.Settings;
        // The accent is written straight into a CSS custom property, so only a hex colour may
        // get through; anything else keeps whatever was already there.
        if (SettingsStore.IsHexColor(s.AccentColor)) t.AccentColor = s.AccentColor.ToUpperInvariant();
        t.Theme = s.Theme ?? "";
        t.HideLegend = s.HideLegend;
        t.AnimationsEnabled = s.AnimationsEnabled;
        t.AnimationSpeed = Math.Clamp(s.AnimationSpeed, 0.5, 2.0);
        t.ThemeSettings = ThemeService.CleanSettingValues(s.ThemeSettings);
        t.IgdbClientId = s.IgdbClientId.Trim();
        t.IgdbClientSecret = s.IgdbClientSecret.Trim();
        t.SteamGridDbKey = s.SteamGridDbKey.Trim();
        t.MetadataEndpoint = s.MetadataEndpoint.Trim();
        t.SteamShowOwned = s.SteamShowOwned;
        t.SteamApiKey = (s.SteamApiKey ?? "").Trim();
        t.GamePassCatalog = s.GamePassCatalog;
        t.XboxClientId = (s.XboxClientId ?? "").Trim();
        t.DetectEmulators = s.DetectEmulators;
        t.VortexPath = (s.VortexPath ?? "").Trim();
        t.TvDeviceName = s.TvDeviceName;
        t.SwitchPrimaryOnLaunch = s.SwitchPrimaryOnLaunch;
        t.RepositionGameWindow = s.RepositionGameWindow;
        t.KeepFocus = s.KeepFocus;
        t.LaunchOnStartup = s.LaunchOnStartup;
        t.AutoUpdate = s.AutoUpdate;
        t.GamepadMouseEnabled = s.GamepadMouseEnabled;
        t.GamepadMouseDuringGame = s.GamepadMouseDuringGame;
        t.Deadzone = Math.Clamp(s.Deadzone, 0.05, 0.40);
        t.Sensitivity = Math.Clamp(s.Sensitivity, 0.2, 3.0);
        t.AccelExponent = Math.Clamp(s.AccelExponent, 1.0, 3.0);
        t.BoostButton = s.BoostButton;
        t.BoostMultiplier = Math.Clamp(s.BoostMultiplier, 1.5, 5.0);
        t.HideCursorSystemWide = s.HideCursorSystemWide;
        t.TouchpadMouse = s.TouchpadMouse;
        t.TouchpadSensitivity = Math.Clamp(s.TouchpadSensitivity, 0.25, 4.0);
        t.TouchpadTapToClick = s.TouchpadTapToClick;
        t.TouchpadTapDrag = s.TouchpadTapDrag;
        t.TouchpadNaturalScroll = s.TouchpadNaturalScroll;
        t.TouchpadScrollSpeed = Math.Clamp(s.TouchpadScrollSpeed, 0.25, 4.0);
        t.LeftClickButton = s.LeftClickButton;
        t.RightClickButton = s.RightClickButton;
        t.MinimizeCombo = s.MinimizeCombo;
        t.ScreenshotCombo = s.ScreenshotCombo;
        t.KeyRepeatDelayMs = Math.Clamp(s.KeyRepeatDelayMs, 120, 900);
        t.KeyRepeatIntervalMs = Math.Clamp(s.KeyRepeatIntervalMs, 20, 300);
        t.KeyboardToggleButton = s.KeyboardToggleButton;
        t.KeyboardToggleMode = s.KeyboardToggleMode == "Hold" ? "Hold" : "Press";
        t.KeyboardToggleHoldMs = Math.Clamp(s.KeyboardToggleHoldMs, 200, 2000);
        t.KeyboardInGame = s.KeyboardInGame;
        t.KeyboardApp = s.KeyboardApp;
        t.KeyboardScale = Math.Clamp(s.KeyboardScale, 0.6, 1.6);
    }


    /// <summary>
    /// Pictures of the open windows, pushed one at a time as they are taken.
    ///
    /// Deliberately not part of the list itself. PrintWindow asks a window to paint, which means
    /// waiting on that window's message loop -- one busy or hung program would otherwise hold up
    /// the whole switcher, and the switcher is the thing you open *because* something is stuck.
    /// The menu draws immediately with titles and fills the pictures in underneath.
    /// </summary>
    private void StartThumbnails(IReadOnlyList<WindowInfo> open)
    {
        if (open.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var w in open.Take(MaxThumbnails))
            {
                WindowShot shot;
                try { shot = _windows.CaptureWindow(new IntPtr(w.Handle)); }
                catch (Exception ex) { Log.Info($"Thumbnail for '{w.Title}' failed: {ex.Message}"); continue; }
                if (shot.Image is null) continue;

                var (image, icon) = (shot.Image, shot.IsIcon);
                _ = _window.Dispatcher.BeginInvoke(() =>
                    Push(new { type = "windowThumb", handle = w.Handle, image, icon }));
            }
        });
    }

    /// <summary>Past this many the list is scrolling anyway, and each picture costs a round trip
    /// through another program's message loop.</summary>
    private const int MaxThumbnails = 16;

    /// <summary>
    /// <paramref name="force"/> re-asks Steam for the account's library even when the last answer
    /// is recent, for a rescan the user asked for by hand. <paramref name="quiet"/> is the manifest
    /// watcher's mode: no spinner, and the UI is only repainted when a game's installed state
    /// actually changed, because a download rewrites its manifest every few seconds and a full
    /// repaint each time would throw the highlight around under somebody browsing.
    /// </summary>
    private void StartScan(bool force = false, bool quiet = false)
    {
        if (_scanning)
        {
            // A manifest that changed while a scan was already reading the folder may have been
            // read before or after the change; asking again once this one is done settles it.
            if (quiet) ScheduleQuietScan();
            return;
        }
        _scanning = true;
        if (!quiet) Push(new { type = "scanning", busy = true });
        var settings = _settings.Settings;
        Task.Run(async () =>
        {
            var before = InstallSignature();
            try
            {
                // Emulators and ROM folders first, so anything found is scanned in the same pass.
                var detected = settings.DetectEmulators ? EmulatorDetection.Run(_library) : null;
                if (!quiet && detected is { } d && (d.Emulators.Count > 0 || d.Folders.Count > 0))
                    _ = _window.Dispatcher.BeginInvoke(() => Push(new { type = "toast", message = DetectionToast(d) }));

                var found = _scanner.ScanAll();
                found.AddRange(_scanner.ScanEmulated(_library.RomFolders.ToList()));
                // What the accounts own but the disk does not have. Each source is independent
                // and each is optional; NotAlreadyFound keeps an installed game from appearing
                // a second time as an owned one.
                var owned = new List<Game>();
                if (settings.SteamShowOwned)
                    owned.AddRange(_scanner.OwnedSteamGames(await _steam.GetOwnedAsync(settings, force), found));
                foreach (var account in _accounts.Values)
                    if (account.Status.SignedIn)
                        owned.AddRange(await account.GetOwnedAsync(force));
                if (settings.GamePassCatalog)
                    owned.AddRange(await _gamePass.GetAsync(force));
                found.AddRange(LibraryScanner.NotAlreadyFound(found, owned));
                _library.MergeScanned(found);
            }
            catch (Exception ex) { Log.Info($"Scan failed: {ex.Message}"); }
            finally
            {
                _scanning = false;
                var changed = !quiet || InstallSignature() != before;
                _ = _window.Dispatcher.BeginInvoke(() =>
                {
                    if (!quiet) Push(new { type = "scanning", busy = false });
                    if (changed) PushState();
                });
            }

            await EnrichMetadata();
        });
    }

    /// <summary>"Found RetroArch and PCSX2; added Game Boy Advance and Nintendo DS ROMs".</summary>
    private static string DetectionToast(DetectionSummary d)
    {
        static string Join(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Count switch
            {
                0 => "",
                1 => list[0],
                2 => $"{list[0]} and {list[1]}",
                _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
            };
        }
        var bits = new List<string>();
        if (d.Emulators.Count > 0) bits.Add("found " + Join(d.Emulators.Select(e => e.Name)));
        if (d.Folders.Count > 0)
            bits.Add("added " + Join(d.Folders.Select(f => EmulatedPlatforms.Find(f.PlatformId)?.Name ?? f.PlatformId).Distinct()) + " ROMs");
        var text = string.Join("; ", bits);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Which games exist and which are on disk: the two things a manifest change can alter,
    /// and the only two a quiet scan repaints for.</summary>
    private string InstallSignature() =>
        string.Join("\n", _library.Games.Select(g => g.Installed ? g.Id + "+" : g.Id)
            .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>
    /// A Steam install started from here -- or from the Steam client, or from a phone -- shows up
    /// as an appmanifest arriving and, some minutes later, its StateFlags gaining the "fully
    /// installed" bit. Watching for that is what lets a tile turn from grey to playable while you
    /// are looking at it rather than on the next start. Debounced, because a download rewrites
    /// its manifest every few seconds; and the scan it triggers is the quiet kind.
    /// </summary>
    private void StartInstallWatcher()
    {
        _manifestTimer = new System.Threading.Timer(
            _ => _window.Dispatcher.BeginInvoke(() => StartScan(quiet: true)),
            null, Timeout.Infinite, Timeout.Infinite);

        foreach (var root in LibraryScanner.SteamLibraryRoots())
        {
            try
            {
                var w = new FileSystemWatcher(root, "appmanifest_*.acf")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                w.Created += (_, _) => ScheduleQuietScan();
                w.Changed += (_, _) => ScheduleQuietScan();
                w.Deleted += (_, _) => ScheduleQuietScan();
                w.Renamed += (_, _) => ScheduleQuietScan();
                w.Error += (_, e) => Log.Info($"Manifest watcher on {root}: {e.GetException().Message}");
                w.EnableRaisingEvents = true;
                _manifestWatchers.Add(w);
            }
            catch (Exception ex) { Log.Info($"Cannot watch {root} for installs: {ex.Message}"); }
        }
    }

    private void ScheduleQuietScan() =>
        _manifestTimer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

    /// <summary>The only things an Install may start. Each is a store's own registered scheme, and
    /// each opens that store's client rather than running a file.</summary>
    private static readonly string[] InstallSchemes =
    {
        "steam://install/", "com.epicgames.launcher://apps/", "goggalaxy://openGameView/", "ms-windows-store://pdp/",
        // GOG without Galaxy: the game's own page on gog.com, where the installer is.
        "https://www.gog.com/",
    };

    /// <summary>
    /// The store's own sign-in page, in a window over the launcher. Always-on-top is dropped
    /// around it the way it is around a file dialog, or the window would open behind the
    /// launcher and look like nothing happened; and one sign-in at a time, because two windows
    /// fighting over the foreground is not something a gamepad can sort out.
    /// </summary>
    private async Task SignInAsync(IStoreAccount account)
    {
        if (_signingIn) return;
        _signingIn = true;
        _window.BeginModalDialog();
        var ok = false;
        try { ok = await account.SignInAsync(_window); }
        catch (Exception ex) { Log.Info($"{account.Store}: sign-in failed ({ex})"); }
        finally
        {
            _window.EndModalDialog();
            _signingIn = false;
        }

        PushState();
        if (ok)
        {
            var who = account.Status.User is { Length: > 0 } u ? $" as {u}" : "";
            Push(new { type = "toast", message = $"Signed in to {account.DisplayName}{who}" });
            StartScan(force: true);
        }
        else
        {
            Push(new { type = "toast", message = account.Status.Error ?? $"{account.DisplayName} sign-in cancelled" });
        }
    }

    /// <summary>
    /// Only Steam writes a manifest the watcher can see. Epic, GOG and the Microsoft Store install
    /// wherever the user pointed them, so after asking one of them to install, the library is
    /// re-read once a minute until the game is on disk or three hours have passed -- quietly, so
    /// the tile simply turns playable.
    /// </summary>
    private void BeginInstallPolling(string id)
    {
        _pendingInstall = id;
        _pollUntil = DateTime.UtcNow.AddHours(3);
        _installPoll ??= new System.Threading.Timer(
            _ => _window.Dispatcher.BeginInvoke(PollInstall), null, Timeout.Infinite, Timeout.Infinite);
        _installPoll.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void PollInstall()
    {
        var done = _pendingInstall is null
                   || DateTime.UtcNow > _pollUntil
                   || _library.Find(_pendingInstall)?.Installed == true;
        if (done)
        {
            _installPoll?.Change(Timeout.Infinite, Timeout.Infinite);
            _pendingInstall = null;
            return;
        }
        StartScan(quiet: true);
    }

    /// <summary>
    /// Runs after the library is already on screen, and never blocks it. Fetching art and facts is
    /// network-bound and takes a while on a big library, but nothing here is needed to browse or
    /// to start a game -- so the scan spinner is already down by the time this begins, and the
    /// only visible effect is that art sharpens and the detail page fills in a moment later.
    /// </summary>
    private async Task EnrichMetadata()
    {
        if (_enriching) return;
        _enriching = true;
        try
        {
            // A snapshot: a rescan may replace the library while this is in flight, and its merge
            // carries across whatever has been written by then.
            var changed = await _metadata.EnrichAsync(_library.Games.ToList(), _settings.Settings);
            if (changed == 0) return;
            _library.Save();
            _ = _window.Dispatcher.BeginInvoke(PushState);
        }
        catch (Exception ex) { Log.Info($"Metadata pass failed: {ex.Message}"); }
        finally { _enriching = false; }
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
            game.CoverFile = CopyArt(art.FileName, game.Id, "");

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

    /// <summary>
    /// The two pictures worth letting somebody replace by hand, and why they are separate.
    ///
    /// They are not the same shape and they are not in the same places. The cover is portrait box
    /// art, 2:3; the tile is the 1.75:1 landscape sheet the library grid and the recents dock are
    /// made of. One dialog that set both would put whichever file was chosen into a slot it was
    /// the wrong shape for, which is the exact mistake that made tiles look like they carried
    /// another game's art in the first place.
    /// </summary>
    private static readonly Dictionary<string, (string Label, string Suffix)> ArtSlots = new()
    {
        ["cover"] = ("cover art", ""),
        ["tile"] = ("tile art", "_tile"),
    };

    private void PickArt(string id, string slot)
    {
        var game = _library.Find(id);
        if (game is null || !ArtSlots.TryGetValue(slot, out var def)) return;

        var art = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose {def.Label} for {game.Title}",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp"
        };
        if (!ShowDialog(art)) return;

        var name = CopyArt(art.FileName, game.Id, def.Suffix);
        if (name is null) { Push(new { type = "toast", message = "That image could not be copied" }); return; }

        if (slot == "tile") game.BannerFile = name; else game.CoverFile = name;
        _library.Save();
        PushState();
    }

    /// <summary>
    /// Back to whatever the last fetch found. Clearing the field rather than deleting the file:
    /// the covers folder is a cache and an orphan in it costs nothing, where a delete on a path
    /// built from a game id is the kind of thing that only has to be wrong once. The next enrich
    /// refills the slot -- the stamp is cleared so it does not wait a fortnight to do it.
    /// </summary>
    private void ResetArt(string id, string slot)
    {
        var game = _library.Find(id);
        if (game is null || !ArtSlots.ContainsKey(slot)) return;

        if (slot == "tile") game.BannerFile = null; else game.CoverFile = null;
        game.MetadataFetched = null;
        _library.Save();
        PushState();
        Push(new { type = "toast", message = "Artwork will be fetched again on the next scan" });
    }

    /// <summary>
    /// Art the user chose by hand. The "custom_" prefix is what keeps it: it is the one name
    /// neither the scanner nor MetadataService ever writes, so a rescan cannot overwrite the file
    /// and an enrich cannot point the game away from it. The suffix keeps one slot's choice from
    /// landing on top of another's.
    /// </summary>
    private static string? CopyArt(string source, string gameId, string suffix)
    {
        try
        {
            // Every character that is not plainly a name goes, not just the colon. A game id is
            // ours -- the scanner writes "steam:1091500" and "manual:<guid>" -- but this one
            // arrives from the page and ends up in a file path, and "ours" is an argument about
            // where it came from rather than a property of the string in hand.
            var safe = Regex.Replace(gameId, "[^A-Za-z0-9_-]", "_");
            var ext = Path.GetExtension(source).ToLowerInvariant();
            var dest = Path.Combine(Paths.CoversDir, $"custom_{safe}{suffix}{ext}");
            File.Copy(source, dest, overwrite: true);

            // A second pick with a different extension would otherwise leave the old file behind
            // and, since the name is what the UI loads, leave it showing until a restart.
            foreach (var stale in Directory.GetFiles(Paths.CoversDir, $"custom_{safe}{suffix}.*"))
                if (!string.Equals(stale, dest, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(stale); } catch { /* a cache file; not worth failing over */ }

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
            update = _updates.Status,
            themes = _themes.List(),
            gameRunning = _launcher.GameRunning,
            runningGameId = _launcher.RunningGameId,
            scanning = _scanning,
            steamAccount = _steam.Status,
            stores = new
            {
                epic = _accounts["epic"].Status,
                gog = _accounts["gog"].Status,
                xbox = _accounts["xbox"].Status,
                gamePass = _gamePass.Status,
            },
            // Whether Vortex is here and where, for the Settings row. The Mods screen itself asks
            // afresh through modsOpen, which is what may start Vortex.
            mods = _mods.Status(),
            // The page draws the emulator and ROM folder rows from these, and the system picker
            // from the catalogue; it never sends any of it back (see the emu* commands).
            emulation = new
            {
                emulators = _library.Emulators,
                romFolders = _library.RomFolders,
                platforms = EmulatedPlatforms.All.Select(p => new
                {
                    id = p.Id, name = p.Name, shortName = p.Short,
                    extensions = p.Extensions, hasCores = p.Cores.Length > 0,
                }),
            }
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

    /// <summary>Re-send the theme list after a change on disk, so an edit reloads live.</summary>
    public void PushThemes() => Push(new { type = "themes", themes = _themes.List() });

    public void PushUpdate(UpdateStatus status) => Push(new { type = "update", status });

    public void PushToast(string message) => Push(new { type = "toast", message });

    public void PushGameState() =>
        Push(new { type = "game", running = _launcher.GameRunning, id = _launcher.RunningGameId });

    /// <summary>A press, and the family of the pad it came from: xbox, playstation, switch or generic.</summary>
    public void PushPadEvent(string button, string layout) => Push(new { type = "pad", button, layout });

    /// <summary>The pad in use changed, or was picked up again after a pause. The page redraws its legends from it.</summary>
    public void PushPadLayout(string layout, string name) => Push(new { type = "padLayout", layout, name });

    /// <summary>A real mouse click is on its way from a pad's touchpad, so the page does not read it as the mouse.</summary>
    public void PushPadClick() => Push(new { type = "padClick" });

    /// <summary>The right stick's scroll speed, notches per second, up positive. See GamepadService.UiScroll.</summary>
    public void PushStickScroll(double v) => Push(new { type = "stickScroll", v = Math.Round(v, 2) });

    /// <summary>The built-in keyboard was closed with B while the launcher was in front.</summary>
    public void PushKeyboardDismissed() => Push(new { type = "keyboardDismissed" });

    public void PushPadConnected(bool connected) => Push(new { type = "padConnected", connected });

    private void Push(object payload) =>
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
}
