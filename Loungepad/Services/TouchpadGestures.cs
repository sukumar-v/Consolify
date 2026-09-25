using System.Runtime.InteropServices;
using Loungepad.Interop;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>One poll's worth of touchpad: the fingers' average travel, how much further apart two
/// of them got, how many are down, and whether the pad is pressed. Pad units, 1920 x 1080.</summary>
internal readonly record struct TouchFrame(int Dx, int Dy, double Spread, int Fingers, bool Click);

/// <summary>
/// A DualSense or DualShock 4 touchpad turned into a Windows precision touchpad, gesture for
/// gesture, as far as two touch points allow:
///
///   one finger        moves the pointer (speed-dependent gain, still while a finger rests)
///   press the pad     left click, held while held; with two fingers down, right click
///   tap               left click -- sent after a short wait, so the tap can become the next two
///   tap, tap          double click
///   tap, touch + hold left button held down (tap-and-drag): drag a window, select text; lifting
///                     keeps it held for a moment so a long drag can be carried across two strokes,
///                     and a tap ends it
///   two-finger tap    right click
///   two-finger swipe  scrolls, both axes, and keeps coasting after a flick
///   pinch / spread    zooms (Ctrl + wheel, which is what Windows' own touchpads send)
///
/// Three- and four-finger gestures are not possible: the pad reports two contacts, never more.
///
/// Called on every poll of the gamepad thread while the touchpad is being read as a mouse, and
/// Reset whenever it stops being, so no button can be left held down behind it.
/// </summary>
internal sealed class TouchpadGestures
{
    // ---- tuning, in pad units (~37 to a millimetre) and milliseconds ----
    /// <summary>A finger that has stopped has to move this far before the pointer follows it again.</summary>
    private const double RestDeadband = 14;
    private const int RestAfterMs = 120;
    /// <summary>Movement is ignored this long after the pad is pressed and after it is let go: a
    /// finger rocks a few units as it works the switch.</summary>
    private const int PressSettleMs = 160, ReleaseSettleMs = 120;
    /// <summary>A tap: on and off within this time, having moved no further than this.</summary>
    private const int TapMaxMs = 220;
    private const double TapMaxTravel = 40;
    /// <summary>How long a tap waits to see whether a second touch makes it a double click or a drag.</summary>
    private const int TapDragWindowMs = 230;
    /// <summary>A second touch held this long without moving starts a drag, as on a laptop.</summary>
    private const int DragHoldMs = 200;
    /// <summary>A tap-drag survives the finger lifting for this long, so it can be continued.</summary>
    private const int DragLockMs = 350;
    /// <summary>Two fingers have to move this far together, or this much apart, before it is a scroll or a pinch.</summary>
    private const double ScrollStart = 22, PinchStart = 60;
    /// <summary>Wheel delta per pad unit at speed 1.0. The pad's height is ~12 notches.</summary>
    private const double ScrollGain = 1.3;
    /// <summary>Ctrl+wheel delta per pad unit of spread.</summary>
    private const double PinchGain = 1.2;
    /// <summary>Coasting after a flick: how fast it has to be to coast, and how quickly it dies away.</summary>
    /// The stop is well above zero on purpose: below ~2 notches a second the coast only dribbles out
    /// one small step every tenth of a second, which reads as stutter rather than as slowing down.
    private const double CoastMinSpeed = 700, CoastStopSpeed = 250, CoastTauSec = 0.33;

    private enum Mode { Idle, Pointer, TwoFingerUndecided, Scroll, Pinch }
    private enum Rail { None, Vertical, Horizontal }
    private Rail _rail;

    /// <summary>The pointer moved: the host flips the UI into pointer mode.</summary>
    public Action? PointerMoved;
    /// <summary>A click is about to be sent, so the page does not read it as the real mouse.</summary>
    public Action? Clicking;

    // the touch in progress
    private Mode _mode;
    private int _fingersPrev, _maxFingers;
    private long _startAt;
    private double _netX, _netY;
    private bool _pressedThisTouch, _secondTouch;
    private double _gx, _gy, _gs;

    // pointer
    private double _fracX, _fracY, _restX, _restY;
    private bool _resting = true;
    private long _lastMoveAt, _holdUntil;

    // buttons
    private bool _physDown, _physRight;
    private bool _dragDown;
    private long _dragLockUntil;
    private bool _tapPending;
    private long _tapDeadline;

    // scrolling
    private double _wheelY, _wheelX, _velY, _velX, _pinch;
    private bool _coasting;
    private long _lastScrollAt;

    public void Update(in TouchFrame f, AppSettings s, long now, double dt)
    {
        bool tapToClick = s.TouchpadTapToClick;
        bool tapDrag = tapToClick && s.TouchpadTapDrag;

        // ---- a touch begins ----
        if (f.Fingers > 0 && _fingersPrev == 0)
        {
            _startAt = now; _maxFingers = f.Fingers; _pressedThisTouch = false;
            _netX = 0; _netY = 0; _gx = 0; _gy = 0; _gs = 0;
            _mode = Mode.Idle;
            _resting = true; _restX = 0; _restY = 0;
            _coasting = false;                        // a finger on the pad stops a coast, as on glass
            _secondTouch = _tapPending && tapDrag;   // the second touch of a tap-tap or a tap-drag
        }
        if (f.Fingers > _maxFingers) _maxFingers = f.Fingers;
        _netX += f.Dx; _netY += f.Dy;

        // ---- what the fingers are doing ----
        if (f.Fingers >= 2 && _mode is Mode.Idle or Mode.Pointer && !_physDown && !_dragDown)
        {
            // A second finger turns whatever this was into a two-finger gesture. It is no longer the
            // second half of a tap-drag either.
            _mode = Mode.TwoFingerUndecided;
            _secondTouch = false;
        }
        else if (f.Fingers == 1 && _mode == Mode.Idle) _mode = Mode.Pointer;

        switch (_mode)
        {
            case Mode.Pointer when f.Fingers == 1:
                MovePointer(f, s, now, dt);
                // Tap, then touch and hold -- or touch and move -- presses the button down.
                if (_secondTouch && !_dragDown && (!_resting || now - _startAt >= DragHoldMs))
                {
                    _tapPending = false;
                    _secondTouch = false;
                    StartDrag();
                }
                break;

            case Mode.TwoFingerUndecided when f.Fingers == 2:
                _gx += f.Dx; _gy += f.Dy; _gs += f.Spread;
                if (Math.Abs(_gs) >= PinchStart && Math.Abs(_gs) > 1.5 * Math.Sqrt(_gx * _gx + _gy * _gy))
                    _mode = Mode.Pinch;
                else if (Math.Sqrt(_gx * _gx + _gy * _gy) >= ScrollStart)
                {
                    _mode = Mode.Scroll;
                    // Rails, as Windows' touchpads have: a swipe that sets off mostly along one axis
                    // scrolls along that axis only, so two fingers' wobble does not drift a page
                    // sideways while reading down it. A genuinely diagonal start scrolls freely.
                    _rail = Math.Abs(_gy) > 2 * Math.Abs(_gx) ? Rail.Vertical
                          : Math.Abs(_gx) > 2 * Math.Abs(_gy) ? Rail.Horizontal
                          : Rail.None;
                }
                break;

            // Scrolling and pinching follow two fingers only. When one lifts, the gesture is over
            // until both are off, rather than the survivor suddenly becoming a pointer.
            case Mode.Scroll when f.Fingers == 2:
                Scroll(_rail == Rail.Vertical ? 0 : f.Dx, _rail == Rail.Horizontal ? 0 : f.Dy, s, now, dt);
                break;

            case Mode.Pinch when f.Fingers == 2:
                _pinch += f.Spread * PinchGain;
                if (Math.Abs(_pinch) >= 40)
                {
                    Clicking?.Invoke();
                    SendCtrlWheel((int)Math.Round(_pinch));
                    _pinch = 0;
                }
                break;
        }

        // ---- the pad pressed down ----
        if (f.Click && !_physDown)
        {
            _physDown = true;
            _pressedThisTouch = true;
            _tapPending = false;                 // the press is the click; a waiting tap would double it
            _physRight = f.Fingers >= 2;
            _holdUntil = now + PressSettleMs;
            _fracX = 0; _fracY = 0;
            // A tap-drag already has the button down, so the press adds nothing to it.
            if (!_dragDown)
            {
                Clicking?.Invoke();
                SendMouse(_physRight ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_LEFTDOWN);
            }
        }
        else if (!f.Click && _physDown)
        {
            _physDown = false;
            if (!_dragDown) SendMouse(_physRight ? NativeMethods.MOUSEEVENTF_RIGHTUP : NativeMethods.MOUSEEVENTF_LEFTUP);
            _holdUntil = now + ReleaseSettleMs;
        }

        // ---- a touch ends ----
        if (f.Fingers == 0 && _fingersPrev > 0) EndTouch(s, now, tapToClick, tapDrag);

        // ---- timers ----
        if (_tapPending && f.Fingers == 0 && now >= _tapDeadline)
        {
            _tapPending = false;
            Click(false);
        }
        if (_dragDown && f.Fingers == 0 && now >= _dragLockUntil) EndDrag();
        if (_coasting) Coast(s, dt);

        _fingersPrev = f.Fingers;
    }

    private void EndTouch(AppSettings s, long now, bool tapToClick, bool tapDrag)
    {
        bool quick = now - _startAt <= TapMaxMs;
        bool still = Math.Sqrt(_netX * _netX + _netY * _netY) <= TapMaxTravel;
        bool gestured = _mode is Mode.Scroll or Mode.Pinch;
        bool tap = tapToClick && quick && still && !gestured && !_pressedThisTouch;

        if (_mode == Mode.Scroll && now - _lastScrollAt < 60
            && Math.Sqrt(_velX * _velX + _velY * _velY) >= CoastMinSpeed)
            _coasting = true;
        _pinch = 0;

        if (_dragDown)
        {
            // A tap while a drag is locked down drops it; lifting after moving keeps it for a moment.
            if (tap) EndDrag();
            else _dragLockUntil = now + DragLockMs;
        }
        else if (tap && _maxFingers >= 2)
        {
            if (_tapPending) { _tapPending = false; Click(false); }
            Click(true);
        }
        else if (tap && _secondTouch)
        {
            // Tap, tap: the waiting click and this one, back to back, which is a double click.
            _tapPending = false;
            Click(false);
            Click(false);
        }
        else if (tap)
        {
            if (tapDrag) { _tapPending = true; _tapDeadline = now + TapDragWindowMs; }
            else Click(false);
        }
        else if (_secondTouch)
        {
            // A second touch that was neither a tap nor long enough to drag: the first tap still counts.
            _tapPending = false;
            Click(false);
        }
        _secondTouch = false;
        _mode = Mode.Idle;
    }

    // ---- pointer ----

    private void MovePointer(in TouchFrame f, AppSettings s, long now, double dt)
    {
        int rawX = f.Dx, rawY = f.Dy;
        // Rest deadband: a finger that stops has to travel RestDeadband before the pointer follows
        // it again, and that travel is dropped rather than replayed. It holds the pointer still
        // while the pad is pressed, and keeps a resting thumb's jitter from creeping it along. The
        // accumulator leaks, so jitter never adds up to a false start.
        if (!_resting && now - _lastMoveAt > RestAfterMs) { _resting = true; _restX = 0; _restY = 0; }
        if (_resting)
        {
            _restX = _restX * 0.9 + rawX;
            _restY = _restY * 0.9 + rawY;
            if (Math.Sqrt(_restX * _restX + _restY * _restY) < RestDeadband) return;
            _resting = false;
            _lastMoveAt = now;
            return;
        }
        if ((rawX == 0 && rawY == 0) || now < _holdUntil) return;

        _lastMoveAt = now;
        // Gain follows speed, as a laptop's does: slow is for placing, a flick for crossing the screen.
        double k = Math.Clamp(s.TouchpadSensitivity, 0.25, 4.0);
        double speed = Math.Sqrt((double)rawX * rawX + (double)rawY * rawY) / Math.Max(dt, 0.004);
        double gain = k * Math.Clamp(0.45 + speed / 2200.0, 0.45, 2.2);
        _fracX += rawX * gain;
        _fracY += rawY * gain;
        int mx = (int)_fracX, my = (int)_fracY;
        _fracX -= mx; _fracY -= my;
        if (mx == 0 && my == 0) return;
        PointerMoved?.Invoke();
        MoveCursor(mx, my);
    }

    // ---- scrolling ----

    private void Scroll(int dx, int dy, AppSettings s, long now, double dt)
    {
        double k = Math.Clamp(s.TouchpadScrollSpeed, 0.25, 4.0) * ScrollGain;
        // Wheel up is positive, and HWHEEL right is positive. Natural scrolling moves the content
        // with the fingers -- fingers down, content down, which is scrolling UP -- and is what
        // Windows' own touchpads default to.
        int sign = s.TouchpadNaturalScroll ? 1 : -1;
        double wy = sign * dy * k, wx = -sign * dx * k;
        if (wy == 0 && wx == 0) return;
        _lastScrollAt = now;
        double inv = 1 / Math.Max(dt, 0.004);
        _velY = _velY * 0.6 + wy * inv * 0.4;
        _velX = _velX * 0.6 + wx * inv * 0.4;
        EmitWheel(wy, wx);
    }

    private void Coast(AppSettings s, double dt)
    {
        EmitWheel(_velY * dt, _velX * dt);
        double decay = Math.Exp(-dt / CoastTauSec);
        _velY *= decay; _velX *= decay;
        if (Math.Sqrt(_velX * _velX + _velY * _velY) < CoastStopSpeed) { _coasting = false; _wheelX = 0; _wheelY = 0; }
    }

    /// <summary>Small wheel deltas, not whole notches: every modern app scrolls smoothly by the pixel from them.</summary>
    private void EmitWheel(double wy, double wx)
    {
        _wheelY += wy; _wheelX += wx;
        if (Math.Abs(_wheelY) >= 8) { int n = (int)_wheelY; _wheelY -= n; SendWheelDelta(NativeMethods.MOUSEEVENTF_WHEEL, n); }
        if (Math.Abs(_wheelX) >= 8) { int n = (int)_wheelX; _wheelX -= n; SendWheelDelta(NativeMethods.MOUSEEVENTF_HWHEEL, n); }
    }

    // ---- buttons ----

    private void Click(bool right)
    {
        Clicking?.Invoke();
        SendMouse(right ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_LEFTDOWN);
        SendMouse(right ? NativeMethods.MOUSEEVENTF_RIGHTUP : NativeMethods.MOUSEEVENTF_LEFTUP);
    }

    private void StartDrag()
    {
        _dragDown = true;
        Clicking?.Invoke();
        SendMouse(NativeMethods.MOUSEEVENTF_LEFTDOWN);
    }

    private void EndDrag()
    {
        if (!_dragDown) return;
        _dragDown = false;
        if (!_physDown) SendMouse(NativeMethods.MOUSEEVENTF_LEFTUP);
    }

    /// <summary>
    /// Stop everything: let go of any button held down, drop a waiting tap, stop coasting. Called
    /// whenever the touchpad stops being read as a mouse -- a menu opened, a game took focus, the
    /// pad went away -- so nothing it started can outlive that.
    /// </summary>
    public void Reset()
    {
        // A tap-drag's button is the left one; otherwise whichever the press put down.
        if (_dragDown) SendMouse(NativeMethods.MOUSEEVENTF_LEFTUP);
        else if (_physDown) SendMouse(_physRight ? NativeMethods.MOUSEEVENTF_RIGHTUP : NativeMethods.MOUSEEVENTF_LEFTUP);
        _dragDown = false; _physDown = false;
        _tapPending = false; _secondTouch = false; _coasting = false;
        _fingersPrev = 0; _mode = Mode.Idle;
        _wheelX = 0; _wheelY = 0; _velX = 0; _velY = 0; _pinch = 0;
    }

    // ---- input ----

    private void SendMouse(uint flags) => Send(Mouse(flags, 0));

    private void SendWheelDelta(uint flags, int delta) => Send(Mouse(flags, delta));

    /// <summary>Ctrl held around a wheel, in one SendInput so nothing can land between them.</summary>
    private void SendCtrlWheel(int delta) => Send(
        Key(VK_CONTROL, false), Mouse(NativeMethods.MOUSEEVENTF_WHEEL, delta), Key(VK_CONTROL, true));

    private const ushort VK_CONTROL = 0x11;

    private static NativeMethods.INPUT Mouse(uint flags, int data) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        u = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) } },
    };

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0 } },
    };

    private void Send(params NativeMethods.INPUT[] inputs) => Output(inputs);

    /// <summary>Where input goes. SendInput, except in a test harness that records it instead --
    /// the gestures cannot be tried by hand from a script, and a real click is never safe there.</summary>
    public Action<NativeMethods.INPUT[]> Output = inputs =>
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

    /// <summary>Moves the pointer by a relative amount; replaceable for the same reason as Output.</summary>
    public Action<int, int> MoveCursor = (dx, dy) =>
    {
        NativeMethods.GetCursorPos(out var p);
        NativeMethods.MoveCursorTo(p.X + dx, p.Y + dy);
    };
}
