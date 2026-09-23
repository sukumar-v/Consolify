using System.IO;
using System.Windows;
using System.Windows.Interop;
using Consolify.Interop;
using Consolify.Services;
using Microsoft.Web.WebView2.Core;

namespace Consolify;

public partial class MainWindow : Window
{
    private readonly bool _windowed;
    private readonly SettingsStore _settings = new();
    private readonly LibraryStore _library = new();
    private readonly DisplayService _displays = new();
    private readonly LibraryScanner _scanner = new();
    private readonly VirtualKeyboardService _keyboard;
    private readonly GameLaunchService _launcher;
    private readonly GamepadService _gamepad;
    private readonly HidGamepadReader _hid = new();
    private readonly CursorService _cursor;
    private readonly ThemeService _themes = new();
    private readonly WindowService _windows;
    private bool _overlayWasMinimized;
    private bool _overlayActive;
    private IntPtr _overlayTarget;
    private UiBridge? _bridge;
    private IntPtr _hwnd;
    private bool _suppressRefocus;

    public MainWindow(bool windowed)
    {
        _windowed = windowed;
        InitializeComponent();

        // NOT AllowsTransparency. It made the radial menu float over the desktop, but WPF
        // implements it as a layered window (WS_EX_LAYERED) and the hosted WebView2 then never
        // receives mouse or wheel messages at all — no hover, no clicks, no scrolling, with only
        // the gamepad's own bridge still working. Verified side by side against the same build:
        // opaque highlights the tile under the pointer, layered does not. The overlay menus paint
        // their own dark wash instead, which at the opacity they use looks near enough the same.

        _settings.Load();
        _library.Load();

        _keyboard = new VirtualKeyboardService(_settings);
        _cursor = new CursorService(_settings);
        _windows = new WindowService(_displays);
        _launcher = new GameLaunchService(_displays, _settings, _library);
        _gamepad = new GamepadService(_settings,
            // An open overlay menu owns the pad whoever Windows thinks is in front. If the
            // foreground could not be taken off the game (see TakeForeground), the menu was on
            // screen but the pad still went to the game -- a menu that looked frozen until a
            // mouse click handed Windows' foreground over.
            isLauncherForeground: () => _overlayActive || NativeMethods.GetForegroundWindow() == _hwnd,
            isGameFocused: () => !_overlayActive && _launcher.IsGameForeground(),
            hid: _hid);

        _launcher.GameStarted += _ => Dispatcher.Invoke(OnGameStarted);
        _launcher.GameExited += _ => Dispatcher.Invoke(OnGameExited);
        // The family rides along with every press, so the page never draws a legend for a pad
        // other than the one that was just used.
        _gamepad.UiEvent += name => Dispatcher.BeginInvoke(() => _bridge?.PushPadEvent(name, _gamepad.ActiveLayout));
        _gamepad.PadUsed += (layout, padName) => Dispatcher.BeginInvoke(() =>
        {
            _bridge?.PushPadLayout(layout, padName);
            _kb?.SetLayout(layout);
            _padFamilyWatcher?.Invoke(layout);
        });
        _gamepad.ConnectedChanged += c => Dispatcher.BeginInvoke(() => _bridge?.PushPadConnected(c));
        _gamepad.TouchClick += () => Dispatcher.BeginInvoke(() => _bridge?.PushPadClick());
        _gamepad.UiScroll += v => Dispatcher.BeginInvoke(() => _bridge?.PushStickScroll(v));
        _gamepad.KeyboardToggleRequested += () => Dispatcher.BeginInvoke(() => _keyboard.Toggle());
        _gamepad.MinimizeToggleRequested += () => Dispatcher.BeginInvoke(OnComboTap);
        _gamepad.RadialRequested += () => Dispatcher.BeginInvoke(() => _ = ShowOverlay("radial"));
        _gamepad.StickDirection += (x, y) => Dispatcher.BeginInvoke(() => _bridge?.PushStick(x, y));
        _gamepad.WakeRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _windows.WakeDisplays();
            // The wake nudges real mouse input, which would otherwise land the UI back in
            // pointer mode with nothing highlighted.
            _gamepad.ResetInputMode();
        });
        _gamepad.BatteryChanged += b => Dispatcher.BeginInvoke(() => _bridge?.PushBattery(b));
        _gamepad.InputModeChanged += mode => Dispatcher.BeginInvoke(() =>
        {
            _cursor.SetPadMode(mode == "pad");
            _bridge?.PushInputMode(mode);
            // The keyboard follows the same pad/pointer split as the launcher UI.
            _kb?.SetInputMode(mode);
        });

        _keyboard.BuiltinShow = ShowBuiltinKeyboard;
        _keyboard.BuiltinHide = HideBuiltinKeyboard;
        _keyboard.BuiltinVisible = () => _kb is { IsVisible: true };
        _gamepad.KeyboardInput += what => Dispatcher.BeginInvoke(() => OnKeyboardInput(what));
        _gamepad.KeyboardArmed = () => _kb?.Armed ?? true;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitWebViewAsync();
        Deactivated += OnDeactivated;
        // Keyboard focus into the page whenever the window is the active one. Nothing in the page
        // is focused otherwise and keydown never fires -- which is why a keyboard used to do
        // nothing in the launcher at all. WebView2 only takes focus through the control's own
        // Focus(), never through a Win32 SetFocus on its render window.
        Activated += (_, _) => FocusPageIfShown();
        // The keyboard is a second top-level window, and WPF shuts down on the last one closing,
        // so leaving it open would keep the process alive with no UI.
        Closed += (_, _) => { _kb?.Close(); _gamepad.Dispose(); _cursor.Dispose(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _windows.SetOwnWindow(_hwnd);
        PositionOnTargetDisplay();
        // Non-XInput pads report through this window as WM_INPUT, whoever is in front (see
        // HidGamepadReader). Registered here because it needs the HWND, and before the poll
        // loop starts so the first snapshot already knows what is attached.
        _hid.Register(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _gamepad.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Not marked handled: WM_INPUT has to reach DefWindowProc so the system can release
        // the buffer behind it.
        if (msg == HidNative.WM_INPUT) _hid.OnInput(lParam);
        else if (msg == HidNative.WM_INPUT_DEVICE_CHANGE) _hid.OnDeviceChange(wParam, lParam);
        return IntPtr.Zero;
    }

    /// <summary>The page gets the keyboard whenever the launcher is the window in front.</summary>
    private void FocusPageIfShown()
    {
        if (_parked || !IsActive) return;
        WebView.Focus();
    }

    /// <summary>Place the window fullscreen on the configured TV display (pixel-exact via SetWindowPos).</summary>
    public void PositionOnTargetDisplay()
    {
        if (_hwnd == IntPtr.Zero) return;

        var target = (_settings.Settings.TvDeviceName is { } name ? _displays.GetDisplay(name) : null)
                     ?? _displays.GetDisplays().FirstOrDefault(d => d.IsPrimary)
                     ?? _displays.GetDisplays().FirstOrDefault();
        if (target is null) return;

        if (_windowed)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST,
                target.X + 80, target.Y + 80, 1280, 720, NativeMethods.SWP_SHOWWINDOW);
            return;
        }

        Topmost = true;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            target.X, target.Y, target.Width, target.Height, NativeMethods.SWP_SHOWWINDOW);
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Paths.WebViewDir);
            await WebView.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            // The whole UI is a web page, so there is nothing to fall back to. Windows 11 ships
            // the runtime, but a fresh Windows 10 box may not have it, and without this the window
            // just sits there black -- the exception would be swallowed by the dispatcher handler.
            Log.Info($"WebView2 runtime missing: {ex.Message}");
            MessageBox.Show(
                "Consolify needs the Microsoft Edge WebView2 Runtime, which is not installed.\n\n" +
                "Install the free Evergreen Runtime from\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                "then start Consolify again.",
                "Consolify", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        var core = WebView.CoreWebView2;
        // Match the page so there is never a flash of the WebView2 default white on startup.
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x08, 0x08, 0x0A);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // The page has the keyboard now, so the browser's own shortcuts have to go: F5 would
        // reload the launcher and Ctrl+F would open a find bar over it.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationCompleted += (_, _) => FocusPageIfShown();
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif

        var uiDir = Path.Combine(AppContext.BaseDirectory, "ui");
        core.SetVirtualHostNameToFolderMapping("consolify.ui", uiDir, CoreWebView2HostResourceAccessKind.Allow);
        ServeDataFolder(core);

        _bridge = new UiBridge(this, core, _settings, _library, _displays, _scanner, _launcher, _keyboard, _windows, _themes);
        // Saving a theme file should show up in the launcher, not after a restart.
        _themes.Changed += () => Dispatcher.BeginInvoke(() => _bridge?.PushThemes());
        _themes.Watch();
        core.WebMessageReceived += _bridge.OnWebMessageReceived;

        core.Navigate("https://consolify.ui/index.html");
    }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css", [".js"] = "text/javascript", [".json"] = "application/json",
        [".html"] = "text/html", [".txt"] = "text/plain", [".svg"] = "image/svg+xml",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".ico"] = "image/x-icon", [".mp4"] = "video/mp4", [".webm"] = "video/webm",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
    };

    /// <summary>
    /// Serve https://consolify.data/... out of the data folder, by hand.
    ///
    /// SetVirtualHostNameToFolderMapping cannot do it: the folder is read by WebView2's own
    /// sandboxed process, and it will not serve anything under %APPDATA%. The mapping is
    /// accepted without complaint and then every single request to that host fails, which is
    /// why cover art has never appeared from disk -- the placeholder initials looked like a
    /// design choice rather than a broken host. Reading the file here instead puts it in this
    /// process, which has no such restriction, and fixes covers and themes in one go.
    /// </summary>
    private static void ServeDataFolder(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("https://consolify.data/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                var uri = new Uri(e.Request.Uri);
                // Query is only ever a cache-busting stamp; the path alone names the file.
                var rel = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(Paths.DataDir, rel));

                // Refuse anything that resolves outside the data folder, so a crafted path in a
                // theme cannot read the rest of the disk.
                var root = Path.GetFullPath(Paths.DataDir) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }

                var type = ContentTypes.TryGetValue(Path.GetExtension(full), out var t) ? t : "application/octet-stream";
                // Allow-Origin because the page is served from consolify.ui: without it a theme
                // could not fetch its own JSON, and fonts would be refused outright.
                var headers = $"Content-Type: {type}\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache";
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(File.ReadAllBytes(full)), 200, "OK", headers);
            }
            catch (Exception ex)
            {
                Log.Info($"Serving {e.Request.Uri} failed: {ex.Message}");
                e.Response = core.Environment.CreateWebResourceResponse(null, 500, "Error", "");
            }
        };
    }

    /// <summary>
    /// Out of the way entirely, rather than minimized.
    ///
    /// The window sets ShowInTaskbar=false, so there is no taskbar button to minimize into and
    /// Windows falls back to the legacy minimized stub -- the little titled bar that was appearing
    /// in the bottom-left corner. Hiding takes the window off the screen, out of Alt-Tab and out
    /// of the z-order, which is what "park the launcher" always meant. The HWND and the WebView2
    /// survive, so coming back is instant and the UI keeps its state.
    /// </summary>
    private bool _parked;

    public void Park()
    {
        _parked = true;
        _suppressRefocus = true;
        Topmost = false;
        Hide();
    }

    /// <summary>Bring it back to the TV and to the foreground.</summary>
    public void Unpark()
    {
        _parked = false;
        _suppressRefocus = false;
        Show();                      // PositionOnTargetDisplay restores Topmost for the TV
        PositionOnTargetDisplay();
        Activate();
        TakeForeground();
    }

    /// <summary>
    /// SetForegroundWindow, in the form Windows actually grants.
    ///
    /// Windows only lets a program take the foreground if it received the last input -- and a
    /// controller read through XInput is not input as far as that rule is concerned. So with a
    /// game in front, the plain call is refused: the window shows (it is topmost), but the game
    /// keeps the foreground and the keyboard focus, which is why the Guide menu needed a mouse
    /// click before anything worked. Joining the foreground window's input queue for the length
    /// of the call is the sanctioned way round it; the two queues are separated again at once.
    /// </summary>
    private void TakeForeground()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == _hwnd) return;
        if (NativeMethods.SetForegroundWindow(_hwnd) && NativeMethods.GetForegroundWindow() == _hwnd) return;

        var fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
        var ours = NativeMethods.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != ours && NativeMethods.AttachThreadInput(ours, fgThread, true);
        try
        {
            NativeMethods.BringWindowToTop(_hwnd);
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(ours, fgThread, false);
        }
        if (NativeMethods.GetForegroundWindow() != _hwnd)
            Log.Info("Could not take the foreground from the game; the menu still owns the pad");
    }

    private void OnGameStarted()
    {
        Park();
        _bridge?.PushGameState();
    }

    private void OnGameExited()
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        _gamepad.ResetInputMode();
        Unpark();
        _bridge?.PushGameState();
        _bridge?.PushState(); // refresh playtime/last-played shown in the UI
    }

    /// <summary>
    /// Drop always-on-top around a modal file dialog. Without this the dialog opens *behind* the
    /// full-screen launcher and looks like nothing happened.
    /// </summary>
    /// <summary>
    /// Keyboard focus into the WebView. WPF's Focus() on the control is what calls the
    /// controller's MoveFocus, which is the only thing WebView2 accepts -- a Win32 SetFocus on
    /// its render window is ignored. Needed only when a text field is about to be typed into.
    /// </summary>
    public void FocusPage()
    {
        if (_parked) return;
        Activate();
        WebView.Focus();
    }

    /// <summary>See GamepadService.UiClaimedButtons.</summary>
    public void SetUiClaimedButtons(string[] buttons) => _gamepad.UiClaimedButtons = buttons;

    /// <summary>See GamepadService.ModalButtonHandler: a window of ours in front of the launcher
    /// that wants some of the pad's buttons, or null once it has closed.</summary>
    public void SetModalPadHandler(Func<string, bool>? handler) => _gamepad.ModalButtonHandler = handler;

    /// <summary>The family of the pad in hand -- xbox, playstation, switch, generic -- for a window
    /// of ours that draws button hints of its own.</summary>
    public string PadFamily => _gamepad.ActiveLayout;

    private Action<string>? _padFamilyWatcher;

    /// <summary>Told the pad family whenever it changes, for as long as the watcher is set.</summary>
    public void WatchPadFamily(Action<string>? watcher) => _padFamilyWatcher = watcher;

    public void BeginModalDialog()
    {
        _suppressRefocus = true;
        Topmost = false;
    }

    public void EndModalDialog()
    {
        _suppressRefocus = false;
        if (!_windowed) Topmost = true;
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    /// <summary>Menu combo: park the launcher so the desktop is usable, and bring it back.</summary>
    public void ToggleParked()
    {
        if (_parked)
        {
            Unpark();
            _gamepad.ResetInputMode();
        }
        else
        {
            Park();
        }
    }

    /// <summary>
    /// Combo tapped. While a game is running this raises the in-game menu instead of minimizing,
    /// so the pad can reach "close game" and "home" without touching a keyboard.
    /// </summary>
    private void OnComboTap()
    {
        // A tap while a menu is up dismisses it. Without this the launcher would minimize out
        // from under an open radial, stranding MenuOwnsStick and leaving the stick-mouse dead.
        if (_overlayActive)
        {
            _bridge?.PushDismiss();
            CloseOverlay(true);
            return;
        }
        if (_launcher.GameRunning) _ = ShowOverlay("ingame");
        else ToggleParked();
    }

    /// <summary>
    /// Bring the launcher forward showing one of the overlay menus. The window the user was on is
    /// captured first, because showing ourselves steals the foreground and the radial menu's
    /// actions all apply to that window.
    /// </summary>
    public async Task ShowOverlay(string mode)
    {
        _overlayWasMinimized = _parked;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != _hwnd) _overlayTarget = fg;

        // Grab the screen before we put ourselves in front of it: the menu paints this still,
        // dimmed, as its background, which is how you can still see what is behind it now that
        // the window itself is opaque.
        var shot = _windows.CaptureDisplay(_settings.Settings.TvDeviceName);

        // Tell the UI to switch to overlay mode FIRST. Script keeps running while the window is
        // hidden, so by the time we show it the library is already hidden and only the menu is
        // painted -- otherwise the launcher flashes up before the overlay appears.
        _bridge?.PushOverlay(mode, _windows.TitleOf(_overlayTarget), shot);
        await Task.Delay(90);

        _overlayActive = true;
        // Both menus, not just the radial: the in-game list is pad-driven too, and letting the
        // stick move the cursor underneath it flipped the UI into pointer mode with the pointer
        // over nothing, which left A dead — the menu looked frozen.
        _gamepad.MenuOwnsStick = true;
        Unpark();
    }

    /// <summary>Dismiss an overlay, putting the launcher back where it was.</summary>
    public void CloseOverlay(bool refocusTarget)
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        bool goBack = _overlayWasMinimized || _launcher.GameRunning;
        if (goBack)
        {
            Park();
            if (refocusTarget && _overlayTarget != IntPtr.Zero) _windows.Focus(_overlayTarget);
        }
    }


    // ---- built-in on-screen keyboard ----

    private KeyboardWindow? _kb;

    /// <summary>
    /// Show the built-in keyboard on the TV. It is created lazily: most sessions never raise it,
    /// and a WPF window costs nothing until it exists.
    /// </summary>
    private void ShowBuiltinKeyboard()
    {
        if (_kb is null)
        {
            _kb = new KeyboardWindow();
            // Menu commits and leaves, B just leaves; both come back through here so the pad
            // is handed back and the window hidden in one place.
            _kb.CloseRequested += () => Dispatcher.BeginInvoke(HideBuiltinKeyboard);
        }
        _kb.SetLayout(_gamepad.ActiveLayout);
        var target = (_settings.Settings.TvDeviceName is { } name ? _displays.GetDisplay(name) : null)
                     ?? _displays.GetDisplays().FirstOrDefault(d => d.IsPrimary);
        if (target is not null) _kb.ShowOn(target, Math.Clamp(_settings.Settings.KeyboardScale, 0.6, 1.6));
        // Hand the pad over. Nothing else can read it until the keyboard closes, which is what
        // makes A "press this key" rather than "launch the highlighted game".
        _gamepad.KeyboardOwnsPad = true;
    }

    /// <summary>Re-apply the keyboard settings while it is up, so the size slider is live.</summary>
    public void RefreshBuiltinKeyboard()
    {
        if (_kb is { IsVisible: true }) ShowBuiltinKeyboard();
    }

    private void HideBuiltinKeyboard()
    {
        _kb?.Hide();
        _gamepad.KeyboardOwnsPad = false;
    }

    private void OnKeyboardInput(string what)
    {
        if (_kb is null) return;
        switch (what)
        {
            case "Up": case "Down": case "Left": case "Right": _kb.Move(what); break;
            case "Press":      _kb.Press(); break;
            case "Backspace":  _kb.Backspace(); break;
            case "Space":      _kb.Space(); break;
            case "Commit":     _kb.Commit(); break;
            case "Shift":      _kb.ToggleShift(); break;
            case "CaretLeft":  _kb.CaretLeft(); break;
            case "CaretRight": _kb.CaretRight(); break;
            case "Layer":      _kb.ToggleLayer(); break;
            case "Close":
                HideBuiltinKeyboard();
                // Over the launcher, B also leaves the text field the keyboard was typing into.
                // Over another app it only closes the keyboard.
                if (NativeMethods.GetForegroundWindow() == _hwnd) _bridge?.PushKeyboardDismissed();
                break;
        }
    }

    /// <summary>Radial opened from the in-game menu and back again.</summary>
    public void SetRadialActive(bool active) => _gamepad.MenuOwnsStick = active;

    /// <summary>
    /// Re-send the pad state once the UI is up. The controller is normally detected while the
    /// WebView is still starting, so that first connected/battery push has no bridge to cross and
    /// is lost -- the corner then showed "no controller" with one sitting right there.
    /// </summary>
    public void PushPadState()
    {
        _bridge?.PushPadConnected(_gamepad.Connected);
        _bridge?.PushBattery(_gamepad.CurrentBattery);
        _bridge?.PushPadLayout(_gamepad.ActiveLayout, _gamepad.ActiveName);
    }

    /// <summary>The UI changed input mode by itself; keep the pad service's copy in step.</summary>
    public void SetInputMode(string mode) => _gamepad.NotifyInputMode(mode);

    /// <summary>
    /// "Suspend": blank the displays and park the pad, leaving the session (and anything
    /// downloading or installing) running. Any gamepad button brings it back. This is not sleep —
    /// suspending the machine from the couch is a one-way trip without Wake-on-LAN, and the lock
    /// screen is a separate secure session the pad cannot drive at all.
    /// </summary>
    public async Task Suspend()
    {
        CloseOverlay(false);
        _bridge?.PushDismiss();
        // Come back to the launcher, not to a half-focused desktop, when the screens light up.
        if (!_launcher.GameRunning) GoHome();

        await Task.Delay(250);      // let the overlay tear down before the screen goes dark
        _gamepad.Suspended = true;
        _windows.BlankDisplays();
    }

    /// <summary>The window the radial menu acts on (whatever was in front when it opened).</summary>
    public IntPtr OverlayTarget => _overlayTarget;

    /// <summary>
    /// Leave the game running and show the launcher properly — the "Home" action, and where
    /// "close game" lands too. Clearing the overlay flags matters: without it the host still
    /// believed a menu was up while the library was on screen, so the next combo tap dismissed a
    /// menu that wasn't there instead of minimizing.
    /// </summary>
    public void GoHome()
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        _gamepad.ResetInputMode();
        Unpark();
    }

    private async void OnDeactivated(object? sender, EventArgs e)
    {
        if (_windowed || _suppressRefocus || !_settings.Settings.KeepFocus || _launcher.GameRunning) return;
        if (_parked) return;
        await Task.Delay(350);
        if (_suppressRefocus || _launcher.GameRunning || _parked) return;

        // Don't fight the virtual keyboard for focus
        var kb = NativeMethods.FindWindow("IPTip_Main_Window", null);
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg == kb) return;
        if (System.Diagnostics.Process.GetProcessesByName("osk").Length > 0) return;

        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    public void ExitApp() => Close();
}
