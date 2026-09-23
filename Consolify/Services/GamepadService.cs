using System.Diagnostics;
using System.Runtime.InteropServices;
using Consolify.Interop;
using Consolify.Models;

namespace Consolify.Services;

/// <summary>
/// Background gamepad polling loop (~125 Hz).
///
/// Two sources, one loop. Xbox pads come from XInput; everything else -- a DualSense, a Switch
/// Pro controller, a generic HID pad -- comes from HidGamepadReader, already translated into
/// XInput's shape. Whichever pad moved most recently is the one driving, and its family
/// ("xbox", "playstation", "switch", "generic") is published so the UI can draw the right
/// glyphs next to "Select".
///
/// Two roles:
///  • UI navigation — when the launcher window is foreground, D-pad and face buttons are
///    delivered as events to the web UI (A=accept, B=back, X/Y/MENU per the on-screen legend).
///  • Gamepad-mouse — the left stick always moves the Windows cursor (deadzone + power
///    acceleration curve); the configured buttons send real left/right clicks whenever the
///    launcher is NOT foreground (e.g. the user tabbed to the desktop). While a game runs the
///    whole service idles unless GamepadMouseDuringGame is enabled, so games with native
///    controller support never see phantom mouse input.
/// </summary>
internal class GamepadService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Func<bool> _isLauncherForeground;
    private readonly Func<bool> _isGameFocused;
    private readonly HidGamepadReader? _hid;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>UI navigation event: Up, Down, Left, Right, A, B, X, Y, Menu, View.</summary>
    public event Action<string>? UiEvent;
    /// <summary>
    /// A pad produced input: which family it is and what it calls itself. Raised when the driving
    /// pad changes, and again when a pad is picked up after a pause -- the UI switches its legend
    /// to the keyboard on a keypress, and this is what switches it back.
    /// </summary>
    public event Action<string, string>? PadUsed;
    /// <summary>Raised when the keyboard-toggle chord is held.</summary>
    public event Action? KeyboardToggleRequested;
    /// <summary>Combo tapped: minimize/restore, or the in-game menu while a game runs.</summary>
    public event Action? MinimizeToggleRequested;
    /// <summary>Combo double-tapped: open the Power Wheel.</summary>
    public event Action? RadialRequested;
    /// <summary>"pad" when the D-pad/buttons drive navigation, "pointer" when the stick moves the cursor.</summary>
    public event Action<string>? InputModeChanged;
    /// <summary>Charge level of the attached pad; see BatteryState.</summary>
    public event Action<BatteryState>? BatteryChanged;
    public event Action<bool>? ConnectedChanged;
    /// <summary>Left-stick direction while the radial menu is up (x right, y up, normalized).</summary>
    public event Action<double, double>? StickDirection;
    /// <summary>A button was pressed while suspended: wake the displays and swallow the press.</summary>
    public event Action? WakeRequested;
    /// <summary>
    /// A touchpad press is about to be sent as a real mouse click. The page treats a mouse click as
    /// "the keyboard and mouse are in use" and swaps its legend to key caps; this tells it that
    /// this one click is the pad.
    /// </summary>
    public event Action? TouchClick;
    /// <summary>The right stick's scroll speed while the launcher is in front, in wheel notches per
    /// second, up positive; 0 when it is let go. The page scrolls by it every frame.</summary>
    public event Action<double>? UiScroll;

    /// <summary>
    /// Set while any overlay menu is up. Those menus are pad-driven and hide the cursor, so the
    /// stick must not drag the pointer around underneath them — it points at radial spokes
    /// instead. Letting it move the cursor also flipped the UI into pointer mode with the pointer
    /// over nothing, which left A doing nothing at all.
    /// </summary>
    public volatile bool MenuOwnsStick;

    /// <summary>Displays are blanked; the pad is inert until a button wakes them.</summary>
    public volatile bool Suspended;

    /// <summary>
    /// The built-in on-screen keyboard is up and takes every button. Set for the keyboard the
    /// same way MenuOwnsStick is set for an overlay menu, and honoured before the in-game
    /// silence gate so it still works over a game.
    /// </summary>
    public volatile bool KeyboardOwnsPad;

    /// <summary>A direction or button for the on-screen keyboard while it owns the pad.</summary>
    public event Action<string>? KeyboardInput;

    /// <summary>
    /// Whether the keyboard currently has a highlighted key. It does not while the pointer is
    /// driving and rests on no key, and the buttons then go back to being a mouse -- otherwise
    /// there is no way to click the text field you are typing into once focus wanders off it.
    /// Polled from the input thread, so it must not touch the UI tree.
    /// </summary>
    public Func<bool>? KeyboardArmed;
    /// <summary>
    /// Buttons the launcher page has asked to receive itself (by UI name, e.g. "View"), set from
    /// the page's claimButtons message. Today that is View on the library, for search: when the
    /// keyboard toggle is bound to the same button, the page wins there and the toggle keeps the
    /// button everywhere else. Only honoured while the launcher is in front and the keyboard is
    /// not driving -- with the keyboard up, the toggle must always be able to close it.
    /// </summary>
    public volatile string[] UiClaimedButtons = Array.Empty<string>();

    /// <summary>
    /// A window of ours that is in front instead of the launcher -- the Nexus browse window --
    /// and wants some of the pad's buttons: given the name of each face or shoulder button as it
    /// is pressed, on this thread, and says whether it took it. A button it takes is not also a
    /// mouse click. Null whenever no such window is open.
    /// </summary>
    public volatile Func<string, bool>? ModalButtonHandler;

    public bool Connected { get; private set; }

    /// <summary>The family of the pad that last produced input: xbox, playstation, switch or generic.</summary>
    public string ActiveLayout { get; private set; } = "xbox";
    /// <summary>What that pad calls itself, for the toast.</summary>
    public string ActiveName { get; private set; } = "";
    /// <summary>The driving pad's device node when it is a HID pad, for the battery lookup; null for XInput.</summary>
    private volatile string? _activeHidInstance;

    /// <summary>Latest reading, so the UI can ask for it after the bridge is up. The pad is
    /// usually detected before the WebView exists, and that first push has nowhere to go.</summary>
    public BatteryState CurrentBattery { get; private set; }

    private const double MaxSpeedPxPerSec = 1400;
    private const double MaxScrollNotchesPerSec = 18;
    private const int RepeatDelayMs = 380, RepeatIntervalMs = 115;
    // Second combo tap within this window opens the radial. Measured from the first press, and
    // the combo is two buttons (LS+RS by default), so the budget has to cover holding the first
    // tap, releasing BOTH buttons, and pressing again. 320ms did not: most double taps missed,
    // and the deferred single tap then opened the launcher instead.
    private const int DoubleTapMs = 550;
    private const byte TriggerThreshold = 40;  // analog triggers count as "pressed" past this
    /// <summary>A pad that has been quiet this long is "picked up again" on its next input.</summary>
    private const int PadQuietMs = 1500;
    /// <summary>
    /// How far a stick has to travel from where it last registered before that counts as the pad
    /// being used. A DualSense streams a report every 4ms and its sticks jitter by a few counts
    /// at rest, and a wobble must not steal the legend from the pad someone is actually holding.
    /// Measured from the last registered position rather than the last tick, so a slow push still
    /// crosses it. About 5% of the travel; the cursor deadzone is 18%.
    /// </summary>
    private const int StickNoise = 1600;

    public GamepadService(SettingsStore settings, Func<bool> isLauncherForeground, Func<bool> isGameFocused, HidGamepadReader? hid = null)
    {
        _settings = settings;
        _isLauncherForeground = isLauncherForeground;
        _isGameFocused = isGameFocused;
        _hid = hid;
    }

    public void Start()
    {
        ControllerBattery.Prime();   // WinRT fills its gamepad list lazily
        _running = true;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "GamepadService" };
        _thread.Start();
    }

    public void Dispose() => _running = false;

    // Mirrors the web UI's own input mode. The two MUST start out agreeing: when this said
    // "pointer" while the UI booted in "pad", the first stick movement was a no-op here, no
    // change was ever pushed, and the UI stayed in pad mode — the pointer moved but hovering
    // highlighted nothing and CSS kept the cursor hidden.
    private string _inputMode = "pad";

    /// <summary>
    /// Put the pad back in charge and re-assert it to the UI. Called whenever the launcher comes
    /// back to the foreground, so the two ends can never drift apart across a game session.
    /// </summary>
    public void ResetInputMode()
    {
        _inputMode = "pad";
        InputModeChanged?.Invoke("pad");
    }

    /// <summary>
    /// The UI switched modes on its own — opening an overlay, or centring the pointer. Record it
    /// without echoing back, so the next stick movement is seen as a real change and pushed.
    /// </summary>
    public void NotifyInputMode(string mode) => _inputMode = mode;

    private void PollLoop()
    {
        ushort prevButtons = 0;
        double fracX = 0, fracY = 0, scrollAccum = 0, hScrollAccum = 0;
        var repeat = new Dictionary<ushort, long>();      // button -> next repeat time (ms)
        long toggleDownAt = -1;
        bool toggleFired = false, leftDown = false, rightDown = false, comboLatched = false, pendingTap = false;
        bool shotLatched = false;
        bool ltLatched = false;
        bool prevTrigger = false;                          // trigger edge, used only while suspended
        long lastComboTapAt = -1;
        var sw = Stopwatch.StartNew();
        long lastTick = sw.ElapsedMilliseconds;
        long nextBatteryPoll = 0, nextStickPush = 0;
        CurrentBattery = new BatteryState(false, -1, false, -99);   // impossible, so the first read always pushes
        int missCount = 0;
        bool xinputMissing = false;
        // Which pad is driving, and where each one last registered a movement (see StickNoise).
        bool activeIsHid = false;
        var anchorX = default(NativeMethods.XINPUT_GAMEPAD);
        var anchorHid = default(NativeMethods.XINPUT_GAMEPAD);
        bool anchorXSet = false, anchorHidSet = false;
        long lastPadInputAt = long.MinValue / 2;

        // The right stick's scroll speed for the launcher page (see the right-stick section). Sent
        // when it changes, at most every 16 ms, and re-sent every 100 ms while it is not zero: the
        // page stops by itself on a speed older than 250 ms, so a push lost on the way -- the
        // launcher hidden mid-scroll, the pad unplugged -- can never leave the list scrolling.
        double uiScrollSent = 0;
        long uiScrollSentAt = long.MinValue / 2;
        void PushUiScroll(double v, long now)
        {
            if (v == 0 && uiScrollSent == 0) return;
            bool changed = Math.Abs(v - uiScrollSent) >= 0.2 || (v == 0) != (uiScrollSent == 0);
            bool due = now - uiScrollSentAt >= (changed ? 16 : 100);
            if (!due && v != 0) return;
            if (!changed && !due) return;
            uiScrollSent = v;
            uiScrollSentAt = now;
            UiScroll?.Invoke(v);
        }

        void SetInputMode(string mode)
        {
            if (_inputMode == mode) return;
            _inputMode = mode;
            InputModeChanged?.Invoke(mode);
        }

        // The touchpad as a precision touchpad. Reset on every path that stops reading it as a
        // mouse, so a held click or a drag can never outlive its context as a stuck button.
        var touch = new TouchpadGestures
        {
            PointerMoved = () => SetInputMode("pointer"),
            Clicking = () => TouchClick?.Invoke(),
        };

        while (_running)
        {
            Thread.Sleep(8);
            long now = sw.ElapsedMilliseconds;
            double dt = Math.Min((now - lastTick) / 1000.0, 0.1);
            lastTick = now;
            double uiScroll = 0;    // set by the right stick while the launcher is in front

            NativeMethods.XINPUT_STATE xs = default;
            int rc = 1;
            if (!xinputMissing)
            {
                try { rc = NativeMethods.XInputGetStateAny(0, out xs); }
                catch (DllNotFoundException)
                {
                    // No XInput at all is unusual, but a HID pad can still drive everything.
                    xinputMissing = true;
                    Log.Info("XInput is not available; only HID pads will be read");
                }
            }
            bool xOk = rc == 0;
            var hid = _hid?.Snapshot() ?? default;
            bool hOk = hid.Present;

            if (!xOk && !hOk)
            {
                if (Connected && ++missCount > 60) { Connected = false; ConnectedChanged?.Invoke(false); }
                // Still report the battery while nothing is attached, or the UI never hears that
                // there is no controller and shows an empty corner instead of the missing-pad icon.
                if (now >= nextBatteryPoll)
                {
                    nextBatteryPoll = now + 10_000;
                    var none = new BatteryState(false, null, false, 0);
                    if (none != CurrentBattery) { CurrentBattery = none; Log.Info("Battery: no controller"); BatteryChanged?.Invoke(none); }
                }
                prevButtons = 0;
                if (leftDown) { SendClick(NativeMethods.MOUSEEVENTF_LEFTUP); leftDown = false; }
                if (rightDown) { SendClick(NativeMethods.MOUSEEVENTF_RIGHTUP); rightDown = false; }
                touch.Reset();
                Thread.Sleep(400); // don't hammer XInput when no pad is attached
                continue;
            }
            missCount = 0;
            if (!Connected) { Connected = true; ConnectedChanged?.Invoke(true); nextBatteryPoll = 0; }

            // ---- which pad is driving ----
            // The one that moved. Two pads can be attached at once -- a DualSense on the cable
            // and an Xbox pad on Bluetooth is exactly this PC -- and the one in somebody's hands
            // is the one whose buttons and sticks are changing. A pad that has gone away hands
            // over to whatever is left.
            // The first reading only sets the anchor: a DualSense's sticks rest a few percent
            // off centre, and against a zero anchor that read as the pad being picked up before
            // anyone had touched it.
            if (xOk && !anchorXSet) { anchorX = xs.Gamepad; anchorXSet = true; }
            if (hOk && !anchorHidSet) { anchorHid = hid.Pad; anchorHidSet = true; }
            bool xMoved = xOk && Moved(xs.Gamepad, anchorX);
            if (xMoved) anchorX = xs.Gamepad;
            // A finger on the touchpad is the pad being used as much as a stick is.
            bool hMoved = hOk && (Moved(hid.Pad, anchorHid) || hid.TouchDx != 0 || hid.TouchDy != 0 || hid.TouchSpread != 0 || hid.TouchClick);
            if (hMoved) anchorHid = hid.Pad;
            if (hMoved && !xMoved) activeIsHid = true;
            else if (xMoved && !hMoved) activeIsHid = false;
            if (activeIsHid && !hOk) activeIsHid = false;
            if (!activeIsHid && !xOk) activeIsHid = true;

            var state = activeIsHid ? new NativeMethods.XINPUT_STATE { Gamepad = hid.Pad } : xs;
            // Announce on input -- and once at the start when the only pad attached is a HID one,
            // or the legend would show Xbox letters to somebody holding a DualSense until they
            // pressed something.
            if (xMoved || hMoved || (activeIsHid && ActiveName.Length == 0))
            {
                string layout = activeIsHid ? hid.Layout : "xbox";
                string name = activeIsHid ? hid.Name : "Xbox controller";
                bool changed = layout != ActiveLayout || name != ActiveName;
                if (changed)
                {
                    ActiveLayout = layout;
                    ActiveName = name;
                    _activeHidInstance = activeIsHid ? hid.InstanceId : null;
                    nextBatteryPoll = 0;   // the gauge should follow the pad in hand, not the last one
                    Log.Info($"Pad in use: {name} ({layout})");
                }
                if (changed || now - lastPadInputAt > PadQuietMs) PadUsed?.Invoke(layout, name);
                lastPadInputAt = now;
            }

            if (now >= nextBatteryPoll)
            {
                nextBatteryPoll = now + 10_000;
                var batt = ControllerBattery.Read(0, Connected, _activeHidInstance);
                if (batt != CurrentBattery)
                {
                    CurrentBattery = batt;
                    Log.Info($"Battery: {batt}");
                    BatteryChanged?.Invoke(batt);
                }
            }

            // ---- suspended: the pad only wakes the screen ----
            // Nothing else may run, and the stick in particular must not move the cursor: any
            // pointer movement is a wake signal to Windows, so the displays would come straight
            // back on their own. The waking press is swallowed rather than delivered.
            if (Suspended)
            {
                ushort held = state.Gamepad.wButtons;
                bool trig = state.Gamepad.bLeftTrigger >= TriggerThreshold
                         || state.Gamepad.bRightTrigger >= TriggerThreshold;
                // A fresh press, not merely a held one: the button that confirmed "Suspend" is
                // often still down when the screens go dark, and a level test would wake them
                // straight back up on that same press.
                bool woke = (ushort)(held & ~prevButtons) != 0 || (trig && !prevTrigger);
                prevButtons = held;
                prevTrigger = trig;
                if (woke)
                {
                    Suspended = false;
                    WakeRequested?.Invoke();
                }
                continue;
            }

            var s = _settings.Settings;
            // Only a FOCUSED game silences the pad. While it runs in the background the gamepad
            // mouse and keyboard toggle stay available for the desktop.
            bool gameFocused = _isGameFocused();
            bool launcherFg = _isLauncherForeground();
            // The keyboard needs the cursor alive even inside a game: pointing at a key is one
            // of the two ways to drive it.
            bool serviceActive = !gameFocused || s.GamepadMouseDuringGame || KeyboardOwnsPad;

            ushort buttons = state.Gamepad.wButtons;
            ushort pressed = (ushort)(buttons & ~prevButtons);
            ushort released = (ushort)(prevButtons & ~buttons);

            // ---- menu combo ----
            // Evaluated BEFORE the serviceActive gate below: inside a focused game the rest of
            // the pad is deliberately silent, but this combo is the only way back out, so it has
            // to keep working there.
            //
            // Tap = minimize/restore (or the in-game menu), double tap = the Power Wheel. The single
            // tap is held back until the double-tap window closes, otherwise a quick double tap
            // would minimize and restore the launcher on the way to opening the radial.
            bool comboNow = ComboPressed(state.Gamepad, s.MinimizeCombo);
            if (comboNow && !comboLatched)
            {
                comboLatched = true;
                toggleDownAt = -1; toggleFired = true;   // swallow any keyboard chord inside the combo

                if (lastComboTapAt >= 0 && now - lastComboTapAt <= DoubleTapMs)
                {
                    lastComboTapAt = -1;
                    pendingTap = false;
                    RadialRequested?.Invoke();
                }
                else
                {
                    lastComboTapAt = now;
                    pendingTap = true;
                }
            }
            else if (!comboNow)
            {
                comboLatched = false;
            }

            // Wait for the combo to be let go before acting on a single tap. Firing mid-hold
            // meant that holding the buttons down past the window parked the launcher under
            // your thumbs, and it also stole the press that was meant to be the second tap.
            if (pendingTap && !comboNow && lastComboTapAt >= 0 && now - lastComboTapAt > DoubleTapMs)
            {
                pendingTap = false;
                lastComboTapAt = -1;
                MinimizeToggleRequested?.Invoke();
            }

            // ---- screenshot key ----
            // Also evaluated before the serviceActive gate: a screenshot is only ever wanted while
            // a game is focused, which is exactly when the rest of the pad is silent. The Xbox
            // Share button would be the natural home for this, but Windows keeps it to itself --
            // it reaches neither XInput nor WinRT's RawGameController -- so it has to be a combo.
            bool shotNow = s.ScreenshotCombo != "Off" && ComboPressed(state.Gamepad, s.ScreenshotCombo);
            if (shotNow && !shotLatched)
            {
                shotLatched = true;
                NativeMethods.SendKeyTap(NativeMethods.VK_F12, NativeMethods.SCAN_F12);
                Log.Info("Screenshot key sent (F12)");
            }
            else if (!shotNow)
            {
                shotLatched = false;
            }

            // ---- on-screen keyboard ----
            // Before the serviceActive gate, like the menu combo: the keyboard is most useful over
            // a game, which is exactly when the rest of the pad is silenced. It does NOT swallow
            // the left stick -- that keeps driving the mouse, so a key can be pointed at as well
            // as walked to -- and it stands down entirely while a menu is up, so the Power Wheel
            // opened on top of it still gets the stick and the buttons.
            bool keyboardDriving = KeyboardOwnsPad && !MenuOwnsStick;
            // Armed = a key is highlighted. Unarmed the buttons are a mouse again (below), so the
            // pointer can go and click a text field the keyboard is not attached to.
            bool keyboardArmed = keyboardDriving && (KeyboardArmed?.Invoke() ?? true);
            if (keyboardDriving)
            {
                // The D-pad always reaches the keyboard: it is what re-arms the highlight after
                // the pointer has cleared it.
                foreach (var (mask, name) in NavButtons)
                {
                    if ((pressed & mask) != 0)
                    {
                        KeyboardInput?.Invoke(name);
                        repeat[mask] = now + Math.Max(120, s.KeyRepeatDelayMs);
                    }
                    else if ((buttons & mask) != 0 && repeat.TryGetValue(mask, out var t) && now >= t)
                    {
                        KeyboardInput?.Invoke(name);
                        repeat[mask] = now + Math.Max(20, s.KeyRepeatIntervalMs);
                    }
                    if ((released & mask) != 0) repeat.Remove(mask);
                }

                // Close is the exception to arming: B has to shut the keyboard whether or not a
                // key is lit, or a pointer resting on nothing would strand it on screen.
                foreach (var (mask, name) in KeyboardButtons)
                    if ((pressed & mask) != 0 && (keyboardArmed || name == "Close"))
                        KeyboardInput?.Invoke(name);

                // LT switches layer, on its own edge because a trigger has no button bit.
                bool ltNow = state.Gamepad.bLeftTrigger >= TriggerThreshold;
                if (ltNow && !ltLatched && keyboardArmed) KeyboardInput?.Invoke("Layer");
                ltLatched = ltNow;
            }

            // ---- keyboard toggle ----
            // Above the serviceActive gate, like the menu combo and the screenshot key: inside a
            // focused game the rest of the pad is deliberately silent, and the keyboard is most
            // useful precisely there. What decides whether it may fire is toggleAllowed below.
            //
            // Two shapes, because the button suits different people differently. A tap is quicker
            // and is the default; a hold is for anyone whose toggle button also has a job inside
            // the launcher, since a hold leaves the tap free to do that job.
            //
            // Inside a focused game the toggle is skipped entirely unless the user has opted in:
            // every button there belongs to the game. Closing a keyboard that is already up is the
            // exception and is always allowed, or an opted-out player could strand one on screen.
            ushort toggleMask = ButtonMask(s.KeyboardToggleButton);
            ushort claimedMask = 0;
            foreach (var claimed in UiClaimedButtons) claimedMask |= ButtonMask(claimed);
            // The page wants this button for itself (View for search, on the library). Leave it
            // unspent here and let the UI loop below deliver it as an ordinary press.
            bool toggleClaimedByUi = launcherFg && !keyboardDriving && (claimedMask & toggleMask) != 0;
            bool toggleAllowed = (!gameFocused || s.KeyboardInGame || KeyboardOwnsPad) && !toggleClaimedByUi;
            bool holdToToggle = string.Equals(s.KeyboardToggleMode, "Hold", StringComparison.OrdinalIgnoreCase);

            if ((pressed & toggleMask) != 0)
            {
                toggleDownAt = now;
                toggleFired = false;
                // The page took this press (see UiClaimedButtons). Count it as spent, or once the
                // page withdraws the claim -- search is open by then -- a Hold would still fire the
                // toggle on this same press, and the release would arrive as a second tap.
                if (toggleClaimedByUi) toggleFired = true;
                if (!holdToToggle && toggleAllowed)
                {
                    // Marking it fired is what stops the same press also reaching the UI below:
                    // in Press mode the keyboard has taken the button outright.
                    toggleFired = true;
                    KeyboardToggleRequested?.Invoke();
                }
            }
            if (holdToToggle && (buttons & toggleMask) != 0 && toggleDownAt >= 0 && !toggleFired
                && now - toggleDownAt >= s.KeyboardToggleHoldMs && toggleAllowed)
            {
                toggleFired = true;
                KeyboardToggleRequested?.Invoke();
            }
            bool toggleReleasedAsTap = (released & toggleMask) != 0 && !toggleFired && toggleDownAt >= 0;
            if ((released & toggleMask) != 0) toggleDownAt = -1;

            if (!serviceActive)
            {
                prevButtons = buttons;
                if (leftDown) { SendClick(NativeMethods.MOUSEEVENTF_LEFTUP); leftDown = false; }
                if (rightDown) { SendClick(NativeMethods.MOUSEEVENTF_RIGHTUP); rightDown = false; }
                touch.Reset();
                PushUiScroll(0, now);
                continue;
            }

            // Only D-pad navigation means "the pad is driving". A face button must not re-arm a
            // highlight the pointer has cleared, or pressing A over empty space would activate
            // whatever was last hovered.
            const ushort dpadMask = NativeMethods.XINPUT_GAMEPAD_DPAD_UP | NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN
                                  | NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT | NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;
            if ((pressed & dpadMask) != 0) SetInputMode("pad");

            // ---- launcher UI navigation ----
            // Not while the keyboard is driving, or the D-pad would walk the library grid behind it.
            if (launcherFg && !keyboardDriving)
            {
                foreach (var (mask, name) in NavButtons)
                {
                    if ((pressed & mask) != 0)
                    {
                        UiEvent?.Invoke(name);
                        if (IsDpad(mask)) repeat[mask] = now + RepeatDelayMs;
                    }
                    else if ((buttons & mask) != 0 && IsDpad(mask) && repeat.TryGetValue(mask, out var t) && now >= t)
                    {
                        UiEvent?.Invoke(name);
                        repeat[mask] = now + RepeatIntervalMs;
                    }
                    if ((released & mask) != 0) repeat.Remove(mask);
                }

                foreach (var (mask, name) in FaceButtons)
                {
                    // In Hold mode the toggle button still does its UI job on a tap (Start taps
                    // open the quick menu). In Press mode the press above consumed it.
                    if (mask == toggleMask && !toggleClaimedByUi)
                    {
                        if (toggleReleasedAsTap) UiEvent?.Invoke(name);
                    }
                    else if ((pressed & mask) != 0)
                    {
                        UiEvent?.Invoke(name);
                    }
                }
            }
            else
            {
                // ---- a window of ours in front ----
                // The Nexus browse window takes B and X for Back and Forward (see
                // StoreLoginWindow.HandlePadButton). Not while the keyboard is driving: B is its
                // Close then. A button the window took is not also a click below.
                ushort modalTaken = 0;
                if (ModalButtonHandler is { } modal && !keyboardDriving)
                    foreach (var (mask, name) in FaceButtons)
                        if ((pressed & mask) != 0 && modal(name)) modalTaken |= mask;

                // ---- desktop mouse clicks ----
                // Not while a key is lit: A is that key's own press there, and a stray click would
                // land on the app underneath instead. With nothing lit the pointer is in charge,
                // and clicking is exactly what it is for.
                if (s.GamepadMouseEnabled && !keyboardArmed)
                {
                    ushort lMask = ButtonMask(s.LeftClickButton), rMask = ButtonMask(s.RightClickButton);
                    // B still closes the keyboard, so it cannot also be a click while one is up.
                    if (keyboardDriving)
                    {
                        if (lMask == NativeMethods.XINPUT_GAMEPAD_B) lMask = 0;
                        if (rMask == NativeMethods.XINPUT_GAMEPAD_B) rMask = 0;
                    }
                    if ((pressed & lMask) != 0 && !leftDown && (modalTaken & lMask) == 0) { SendClick(NativeMethods.MOUSEEVENTF_LEFTDOWN); leftDown = true; }
                    if ((released & lMask) != 0 && leftDown) { SendClick(NativeMethods.MOUSEEVENTF_LEFTUP); leftDown = false; }
                    if ((pressed & rMask) != 0 && !rightDown && (modalTaken & rMask) == 0) { SendClick(NativeMethods.MOUSEEVENTF_RIGHTDOWN); rightDown = true; }
                    if ((released & rMask) != 0 && rightDown) { SendClick(NativeMethods.MOUSEEVENTF_RIGHTUP); rightDown = false; }
                }
            }

            // ---- left stick ----
            if (MenuOwnsStick)
            {
                // An overlay menu owns the stick: it points at a radial spoke instead of dragging
                // the pointer around, and the cursor is hidden while it is up.
                double rnx = state.Gamepad.sThumbLX / 32767.0;
                double rny = state.Gamepad.sThumbLY / 32767.0;
                if (Math.Sqrt(rnx * rnx + rny * rny) > 0.55 && now >= nextStickPush)
                {
                    nextStickPush = now + 60;
                    StickDirection?.Invoke(rnx, rny);
                }
                touch.Reset();
            }
            else if (s.GamepadMouseEnabled)
            {
                // Hold the boost button to cross the screen quickly. It scales the cursor and both
                // scroll axes by the same factor, so the pad keeps feeling like one device.
                double boost = ComboPressed(state.Gamepad, s.BoostButton) ? Math.Clamp(s.BoostMultiplier, 1.0, 5.0) : 1.0;

                double nx = state.Gamepad.sThumbLX / 32767.0;
                double ny = state.Gamepad.sThumbLY / 32767.0;
                double mag = Math.Sqrt(nx * nx + ny * ny);
                if (mag > s.Deadzone)
                {
                    // rescale so movement starts at zero right past the deadzone, then apply accel curve
                    double t = Math.Min((mag - s.Deadzone) / (1 - s.Deadzone), 1.0);
                    double speed = MaxSpeedPxPerSec * s.Sensitivity * Math.Pow(t, s.AccelExponent) * boost;
                    fracX += nx / mag * speed * dt;
                    fracY += -ny / mag * speed * dt;
                    int dx = (int)fracX, dy = (int)fracY;
                    fracX -= dx; fracY -= dy;
                    if (dx != 0 || dy != 0)
                    {
                        // Moving the stick brings the pointer back.
                        SetInputMode("pointer");
                        NativeMethods.GetCursorPos(out var p);
                        NativeMethods.MoveCursorTo(p.X + dx, p.Y + dy);
                    }
                }
                else { fracX = 0; fracY = 0; }

                // Right stick -> scroll wheel, vertical or horizontal. Accumulated per elapsed
                // time exactly like the cursor above: the old "emit while (now % 96 < 12)" gate
                // depended on a poll landing inside a 12ms window every 96ms, so with ~8ms polls
                // plus jitter whole cycles emitted nothing and the scroll stuttered.
                double ry = state.Gamepad.sThumbRY / 32767.0;
                double rx = state.Gamepad.sThumbRX / 32767.0;
                // Whichever axis is pushed further wins, so a diagonal nudge never scrolls both ways.
                if (Math.Abs(ry) >= Math.Abs(rx))
                {
                    hScrollAccum = 0;
                    // Over the launcher the page is told the stick's speed and scrolls by exactly
                    // that much every frame. Whole wheel notches -- 120 units, a 100px jump each,
                    // arriving up to 18 times a second on whatever poll crossed the line -- were
                    // the jitter: the list moved in uneven lurches however smoothly the stick was
                    // pushed. Everywhere else a wheel notch is still what Windows apps expect.
                    if (launcherFg && !keyboardDriving)
                    {
                        scrollAccum = 0;
                        uiScroll = StickSpeed(ry, s.Deadzone, boost);
                    }
                    else scrollAccum = StickScroll(ry, s.Deadzone, dt, boost, scrollAccum, n => SendWheel(n * 120));
                }
                else
                {
                    scrollAccum = 0;
                    hScrollAccum = StickScroll(rx, s.Deadzone, dt, boost, hScrollAccum, n => SendHWheel(n * 120));
                }

                // ---- touchpad ----
                // A DualSense or DualShock 4 as a precision touchpad: pointer, clicks, taps,
                // tap-and-drag, two-finger scroll and pinch. See TouchpadGestures. Only for the pad
                // that is driving: a hand resting on a second pad's touchpad must not move anything.
                if (s.TouchpadMouse && activeIsHid)
                    touch.Update(new TouchFrame(hid.TouchDx, hid.TouchDy, hid.TouchSpread, hid.TouchFingers, hid.TouchClick), s, now, dt);
                else touch.Reset();
            }
            else touch.Reset();
            PushUiScroll(uiScroll, now);

            prevButtons = buttons;
        }
    }

    private static readonly (ushort mask, string name)[] NavButtons =
    {
        (NativeMethods.XINPUT_GAMEPAD_DPAD_UP, "Up"),
        (NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN, "Down"),
        (NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT, "Left"),
        (NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT, "Right"),
    };

    /// <summary>
    /// What the buttons do while the on-screen keyboard has the pad. Deliberately the Xbox
    /// keyboard's own bindings, so muscle memory carries over, and each is printed on the key
    /// it drives.
    /// </summary>
    private static readonly (ushort mask, string name)[] KeyboardButtons =
    {
        (NativeMethods.XINPUT_GAMEPAD_A, "Press"),
        (NativeMethods.XINPUT_GAMEPAD_B, "Close"),
        (NativeMethods.XINPUT_GAMEPAD_X, "Backspace"),
        (NativeMethods.XINPUT_GAMEPAD_Y, "Space"),
        (NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER, "CaretLeft"),
        (NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER, "CaretRight"),
        (NativeMethods.XINPUT_GAMEPAD_LEFT_THUMB, "Shift"),
        (NativeMethods.XINPUT_GAMEPAD_START, "Commit"),
    };

    private static readonly (ushort mask, string name)[] FaceButtons =
    {
        (NativeMethods.XINPUT_GAMEPAD_A, "A"),
        (NativeMethods.XINPUT_GAMEPAD_B, "B"),
        (NativeMethods.XINPUT_GAMEPAD_X, "X"),
        (NativeMethods.XINPUT_GAMEPAD_Y, "Y"),
        (NativeMethods.XINPUT_GAMEPAD_START, "Menu"),
        (NativeMethods.XINPUT_GAMEPAD_BACK, "View"),
        (NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER, "LB"),
        (NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER, "RB"),
    };

    /// <summary>Has this pad been touched since it last registered? Buttons count at once; the analog parts past the noise.</summary>
    private static bool Moved(in NativeMethods.XINPUT_GAMEPAD a, in NativeMethods.XINPUT_GAMEPAD b) =>
        a.wButtons != b.wButtons
        || Math.Abs(a.bLeftTrigger - b.bLeftTrigger) > 24
        || Math.Abs(a.bRightTrigger - b.bRightTrigger) > 24
        || Math.Abs(a.sThumbLX - b.sThumbLX) > StickNoise
        || Math.Abs(a.sThumbLY - b.sThumbLY) > StickNoise
        || Math.Abs(a.sThumbRX - b.sThumbRX) > StickNoise
        || Math.Abs(a.sThumbRY - b.sThumbRY) > StickNoise;

    private static bool IsDpad(ushort mask) => mask is NativeMethods.XINPUT_GAMEPAD_DPAD_UP
        or NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN or NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT
        or NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;

    /// <summary>
    /// One axis of stick-driven scrolling: rescales past the deadzone, accumulates notches per
    /// elapsed time and emits whole notches. Returns the carried-over remainder.
    /// </summary>
    /// <summary>The same curve as StickScroll, as a speed in notches per second (up positive) rather than whole notches.</summary>
    private static double StickSpeed(double axis, double deadzone, double boost)
    {
        double mag = Math.Abs(axis);
        if (mag <= deadzone) return 0;
        double t = Math.Min((mag - deadzone) / (1 - deadzone), 1.0);
        return Math.Sign(axis) * MaxScrollNotchesPerSec * Math.Pow(t, 1.5) * boost;
    }

    private static double StickScroll(double axis, double deadzone, double dt, double boost, double accum, Action<int> emit)
    {
        double mag = Math.Abs(axis);
        if (mag <= deadzone) return 0;
        double t = Math.Min((mag - deadzone) / (1 - deadzone), 1.0);
        accum += Math.Sign(axis) * MaxScrollNotchesPerSec * Math.Pow(t, 1.5) * boost * dt;
        int notches = (int)accum;
        if (notches != 0)
        {
            accum -= notches;
            emit(notches);
        }
        return accum;
    }

    /// <summary>
    /// Is the whole combo held? Handles face/shoulder/stick buttons, the analog triggers, and the
    /// Guide button (which only reports through the extended XInput export).
    /// </summary>
    internal static bool ComboPressed(in NativeMethods.XINPUT_GAMEPAD pad, string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo) || combo.Equals("Off", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("LT", StringComparison.OrdinalIgnoreCase))
            {
                if (pad.bLeftTrigger < TriggerThreshold) return false;
            }
            else if (part.Equals("RT", StringComparison.OrdinalIgnoreCase))
            {
                if (pad.bRightTrigger < TriggerThreshold) return false;
            }
            else
            {
                ushort m = ButtonMask(part);
                if (m == 0 || (pad.wButtons & m) == 0) return false;
            }
        }
        return true;
    }

    /// <summary>Mask for a "A + B" style combo string; 0 when disabled or unparseable.</summary>
    public static ushort ComboMask(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo) || combo.Equals("Off", StringComparison.OrdinalIgnoreCase))
            return 0;
        ushort mask = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            mask |= ButtonMask(part);
        return mask;
    }

    public static ushort ButtonMask(string name) => name switch
    {
        "A" => NativeMethods.XINPUT_GAMEPAD_A,
        "B" => NativeMethods.XINPUT_GAMEPAD_B,
        "X" => NativeMethods.XINPUT_GAMEPAD_X,
        "Y" => NativeMethods.XINPUT_GAMEPAD_Y,
        "Start" or "Menu" => NativeMethods.XINPUT_GAMEPAD_START,
        "Back" or "View" => NativeMethods.XINPUT_GAMEPAD_BACK,
        "LB" => NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER,
        "RB" => NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER,
        "LS" => NativeMethods.XINPUT_GAMEPAD_LEFT_THUMB,
        "RS" => NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB,
        "Guide" or "Xbox" or "PS" => NativeMethods.XINPUT_GAMEPAD_GUIDE,
        _ => 0    // unknown name matches nothing rather than silently meaning A
    };

    private static void SendClick(uint flag)
    {
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, u = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dwFlags = flag } } };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendHWheel(int delta)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = unchecked((uint)delta) } }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendWheel(int delta)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta) } }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
