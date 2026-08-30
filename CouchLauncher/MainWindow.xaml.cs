using System.IO;
using System.Windows;
using System.Windows.Interop;
using CouchLauncher.Interop;
using CouchLauncher.Services;
using Microsoft.Web.WebView2.Core;

namespace CouchLauncher;

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
    private readonly CursorService _cursor;
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
            isLauncherForeground: () => NativeMethods.GetForegroundWindow() == _hwnd,
            isGameFocused: () => _launcher.IsGameForeground());

        _launcher.GameStarted += _ => Dispatcher.Invoke(OnGameStarted);
        _launcher.GameExited += _ => Dispatcher.Invoke(OnGameExited);
        _gamepad.UiEvent += name => Dispatcher.BeginInvoke(() => _bridge?.PushPadEvent(name));
        _gamepad.ConnectedChanged += c => Dispatcher.BeginInvoke(() => _bridge?.PushPadConnected(c));
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
        });

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitWebViewAsync();
        Deactivated += OnDeactivated;
        Closed += (_, _) => { _gamepad.Dispose(); _cursor.Dispose(); };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _windows.SetOwnWindow(_hwnd);
        PositionOnTargetDisplay();
        _gamepad.Start();
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
        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(Paths.DataDir, "webview2"));
        await WebView.EnsureCoreWebView2Async(env);

        var core = WebView.CoreWebView2;
        // Match the page so there is never a flash of the WebView2 default white on startup.
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x08, 0x08, 0x0A);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif

        var uiDir = Path.Combine(AppContext.BaseDirectory, "ui");
        core.SetVirtualHostNameToFolderMapping("couch.ui", uiDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("couch.data", Paths.DataDir, CoreWebView2HostResourceAccessKind.Allow);

        _bridge = new UiBridge(this, core, _settings, _library, _displays, _scanner, _launcher, _keyboard, _windows);
        core.WebMessageReceived += _bridge.OnWebMessageReceived;

        core.Navigate("https://couch.ui/index.html");
    }

    private void OnGameStarted()
    {
        _suppressRefocus = true;
        Topmost = false;
        WindowState = WindowState.Minimized;
        _bridge?.PushGameState();
    }

    private void OnGameExited()
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        _gamepad.ResetInputMode();
        _suppressRefocus = false;
        WindowState = WindowState.Normal;
        PositionOnTargetDisplay();
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
        _bridge?.PushGameState();
        _bridge?.PushState(); // refresh playtime/last-played shown in the UI
    }

    /// <summary>
    /// Drop always-on-top around a modal file dialog. Without this the dialog opens *behind* the
    /// full-screen launcher and looks like nothing happened.
    /// </summary>
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

    /// <summary>Back+Start gamepad combo: park the launcher so the desktop is usable, and bring it back.</summary>
    public void ToggleMinimize()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            PositionOnTargetDisplay();
            Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
            _gamepad.ResetInputMode();
        }
        else
        {
            WindowState = WindowState.Minimized;
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
        else ToggleMinimize();
    }

    /// <summary>
    /// Bring the launcher forward showing one of the overlay menus. The window the user was on is
    /// captured first, because showing ourselves steals the foreground and the radial menu's
    /// actions all apply to that window.
    /// </summary>
    public async Task ShowOverlay(string mode)
    {
        _overlayWasMinimized = WindowState == WindowState.Minimized;
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != _hwnd) _overlayTarget = fg;

        // Grab the screen before we put ourselves in front of it: the menu paints this still,
        // dimmed, as its background, which is how you can still see what is behind it now that
        // the window itself is opaque.
        var shot = _windows.CaptureDisplay(_settings.Settings.TvDeviceName);

        // Tell the UI to switch to overlay mode FIRST. Script keeps running while the window is
        // minimized, so by the time we show it the library is already hidden and only the menu
        // is painted -- otherwise the launcher flashes up before the overlay appears.
        _bridge?.PushOverlay(mode, _windows.TitleOf(_overlayTarget), shot);
        await Task.Delay(90);

        _overlayActive = true;
        // Both menus, not just the radial: the in-game list is pad-driven too, and letting the
        // stick move the cursor underneath it flipped the UI into pointer mode with the pointer
        // over nothing, which left A dead — the menu looked frozen.
        _gamepad.MenuOwnsStick = true;
        _suppressRefocus = false;
        WindowState = WindowState.Normal;
        PositionOnTargetDisplay();
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    /// <summary>Dismiss an overlay, putting the launcher back where it was.</summary>
    public void CloseOverlay(bool refocusTarget)
    {
        _overlayActive = false;
        _gamepad.MenuOwnsStick = false;
        bool goBack = _overlayWasMinimized || _launcher.GameRunning;
        if (goBack)
        {
            _suppressRefocus = true;
            Topmost = false;
            WindowState = WindowState.Minimized;
            if (refocusTarget && _overlayTarget != IntPtr.Zero) _windows.Focus(_overlayTarget);
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
        _suppressRefocus = false;
        WindowState = WindowState.Normal;
        PositionOnTargetDisplay();
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    private async void OnDeactivated(object? sender, EventArgs e)
    {
        if (_windowed || _suppressRefocus || !_settings.Settings.KeepFocus || _launcher.GameRunning) return;
        if (WindowState == WindowState.Minimized) return;
        await Task.Delay(350);
        if (_suppressRefocus || _launcher.GameRunning || WindowState == WindowState.Minimized) return;

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
