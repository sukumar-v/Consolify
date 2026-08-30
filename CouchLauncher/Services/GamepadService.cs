using System.Diagnostics;
using System.Runtime.InteropServices;
using CouchLauncher.Interop;
using CouchLauncher.Models;

namespace CouchLauncher.Services;

/// <summary>
/// Background XInput polling loop (~125 Hz).
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
public class GamepadService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Func<bool> _isLauncherForeground;
    private readonly Func<bool> _isGameFocused;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>UI navigation event: Up, Down, Left, Right, A, B, X, Y, Menu, View.</summary>
    public event Action<string>? UiEvent;
    /// <summary>Raised when the keyboard-toggle chord is held.</summary>
    public event Action? KeyboardToggleRequested;
    /// <summary>Combo tapped: minimize/restore, or the in-game menu while a game runs.</summary>
    public event Action? MinimizeToggleRequested;
    /// <summary>Combo double-tapped: open the Power Wheel.</summary>
    public event Action? RadialRequested;
    /// <summary>"pad" when the D-pad/buttons drive navigation, "pointer" when the stick moves the cursor.</summary>
    public event Action<string>? InputModeChanged;
    /// <summary>type: 0 none/wired, 2 alkaline, 3 NiMH; level: 0 empty .. 3 full.</summary>
    public event Action<byte, byte>? BatteryChanged;
    public event Action<bool>? ConnectedChanged;
    /// <summary>Left-stick direction while the radial menu is up (x right, y up, normalized).</summary>
    public event Action<double, double>? StickDirection;
    /// <summary>A button was pressed while suspended: wake the displays and swallow the press.</summary>
    public event Action? WakeRequested;

    /// <summary>
    /// Set while any overlay menu is up. Those menus are pad-driven and hide the cursor, so the
    /// stick must not drag the pointer around underneath them — it points at radial spokes
    /// instead. Letting it move the cursor also flipped the UI into pointer mode with the pointer
    /// over nothing, which left A doing nothing at all.
    /// </summary>
    public volatile bool MenuOwnsStick;

    /// <summary>Displays are blanked; the pad is inert until a button wakes them.</summary>
    public volatile bool Suspended;

    public bool Connected { get; private set; }

    private const double MaxSpeedPxPerSec = 1400;
    private const double MaxScrollNotchesPerSec = 18;
    private const int RepeatDelayMs = 380, RepeatIntervalMs = 115;
    // Second combo tap within this window opens the radial. Measured from the first press, and
    // the combo is two buttons (LS+RS by default), so the budget has to cover holding the first
    // tap, releasing BOTH buttons, and pressing again. 320ms did not: most double taps missed,
    // and the deferred single tap then opened the launcher instead.
    private const int DoubleTapMs = 550;
    private const byte TriggerThreshold = 40;  // analog triggers count as "pressed" past this

    public GamepadService(SettingsStore settings, Func<bool> isLauncherForeground, Func<bool> isGameFocused)
    {
        _settings = settings;
        _isLauncherForeground = isLauncherForeground;
        _isGameFocused = isGameFocused;
    }

    public void Start()
    {
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
        bool prevTrigger = false;                          // trigger edge, used only while suspended
        long lastComboTapAt = -1;
        var sw = Stopwatch.StartNew();
        long lastTick = sw.ElapsedMilliseconds;
        long nextBatteryPoll = 0, nextStickPush = 0;
        var lastBattery = (type: (byte)255, level: (byte)255);
        int missCount = 0;

        void SetInputMode(string mode)
        {
            if (_inputMode == mode) return;
            _inputMode = mode;
            InputModeChanged?.Invoke(mode);
        }

        while (_running)
        {
            Thread.Sleep(8);
            long now = sw.ElapsedMilliseconds;
            double dt = Math.Min((now - lastTick) / 1000.0, 0.1);
            lastTick = now;

            NativeMethods.XINPUT_STATE state;
            int rc = 1;
            try { rc = NativeMethods.XInputGetStateAny(0, out state); }
            catch (DllNotFoundException) { break; }

            if (rc != 0)
            {
                if (Connected && ++missCount > 60) { Connected = false; ConnectedChanged?.Invoke(false); }
                prevButtons = 0;
                if (leftDown) { SendClick(NativeMethods.MOUSEEVENTF_LEFTUP); leftDown = false; }
                if (rightDown) { SendClick(NativeMethods.MOUSEEVENTF_RIGHTUP); rightDown = false; }
                Thread.Sleep(400); // don't hammer XInput when no pad is attached
                continue;
            }
            missCount = 0;
            if (!Connected) { Connected = true; ConnectedChanged?.Invoke(true); nextBatteryPoll = 0; }

            if (now >= nextBatteryPoll)
            {
                nextBatteryPoll = now + 10_000;
                if (NativeMethods.TryGetBattery(0, out var bat) && (bat.BatteryType, bat.BatteryLevel) != lastBattery)
                {
                    lastBattery = (bat.BatteryType, bat.BatteryLevel);
                    BatteryChanged?.Invoke(bat.BatteryType, bat.BatteryLevel);
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
            bool serviceActive = !gameFocused || s.GamepadMouseDuringGame;

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
            // meant that holding the buttons down past the window minimized the launcher under
            // your thumbs, and it also stole the press that was meant to be the second tap.
            if (pendingTap && !comboNow && lastComboTapAt >= 0 && now - lastComboTapAt > DoubleTapMs)
            {
                pendingTap = false;
                lastComboTapAt = -1;
                MinimizeToggleRequested?.Invoke();
            }
            if (!serviceActive)
            {
                prevButtons = buttons;
                if (leftDown) { SendClick(NativeMethods.MOUSEEVENTF_LEFTUP); leftDown = false; }
                if (rightDown) { SendClick(NativeMethods.MOUSEEVENTF_RIGHTUP); rightDown = false; }
                continue;
            }

            // ---- keyboard toggle chord (hold) ----
            ushort toggleMask = ButtonMask(s.KeyboardToggleButton);
            if ((pressed & toggleMask) != 0) { toggleDownAt = now; toggleFired = false; }
            if ((buttons & toggleMask) != 0 && toggleDownAt >= 0 && !toggleFired && now - toggleDownAt >= s.KeyboardToggleHoldMs)
            {
                toggleFired = true;
                KeyboardToggleRequested?.Invoke();
            }
            bool toggleReleasedAsTap = (released & toggleMask) != 0 && !toggleFired && toggleDownAt >= 0;
            if ((released & toggleMask) != 0) toggleDownAt = -1;

            // Only D-pad navigation means "the pad is driving". A face button must not re-arm a
            // highlight the pointer has cleared, or pressing A over empty space would activate
            // whatever was last hovered.
            const ushort dpadMask = NativeMethods.XINPUT_GAMEPAD_DPAD_UP | NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN
                                  | NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT | NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;
            if ((pressed & dpadMask) != 0) SetInputMode("pad");

            // ---- launcher UI navigation ----
            if (launcherFg)
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
                    // The toggle button doubles as a UI button on tap (e.g. Start taps open the quick menu)
                    if (mask == toggleMask)
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
                // ---- desktop mouse clicks ----
                if (s.GamepadMouseEnabled)
                {
                    ushort lMask = ButtonMask(s.LeftClickButton), rMask = ButtonMask(s.RightClickButton);
                    if ((pressed & lMask) != 0 && !leftDown) { SendClick(NativeMethods.MOUSEEVENTF_LEFTDOWN); leftDown = true; }
                    if ((released & lMask) != 0 && leftDown) { SendClick(NativeMethods.MOUSEEVENTF_LEFTUP); leftDown = false; }
                    if ((pressed & rMask) != 0 && !rightDown) { SendClick(NativeMethods.MOUSEEVENTF_RIGHTDOWN); rightDown = true; }
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
            }
            else if (s.GamepadMouseEnabled)
            {
                double nx = state.Gamepad.sThumbLX / 32767.0;
                double ny = state.Gamepad.sThumbLY / 32767.0;
                double mag = Math.Sqrt(nx * nx + ny * ny);
                if (mag > s.Deadzone)
                {
                    // rescale so movement starts at zero right past the deadzone, then apply accel curve
                    double t = Math.Min((mag - s.Deadzone) / (1 - s.Deadzone), 1.0);
                    double speed = MaxSpeedPxPerSec * s.Sensitivity * Math.Pow(t, s.AccelExponent);
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
                    scrollAccum = StickScroll(ry, s.Deadzone, dt, scrollAccum, n => SendWheel(n * 120));
                }
                else
                {
                    scrollAccum = 0;
                    hScrollAccum = StickScroll(rx, s.Deadzone, dt, hScrollAccum, n => SendHWheel(n * 120));
                }
            }

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

    private static bool IsDpad(ushort mask) => mask is NativeMethods.XINPUT_GAMEPAD_DPAD_UP
        or NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN or NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT
        or NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;

    /// <summary>
    /// One axis of stick-driven scrolling: rescales past the deadzone, accumulates notches per
    /// elapsed time and emits whole notches. Returns the carried-over remainder.
    /// </summary>
    private static double StickScroll(double axis, double deadzone, double dt, double accum, Action<int> emit)
    {
        double mag = Math.Abs(axis);
        if (mag <= deadzone) return 0;
        double t = Math.Min((mag - deadzone) / (1 - deadzone), 1.0);
        accum += Math.Sign(axis) * MaxScrollNotchesPerSec * Math.Pow(t, 1.5) * dt;
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
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, mi = new NativeMethods.MOUSEINPUT { dwFlags = flag } };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendHWheel(int delta)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_HWHEEL, mouseData = unchecked((uint)delta) }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendWheel(int delta)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta) }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
