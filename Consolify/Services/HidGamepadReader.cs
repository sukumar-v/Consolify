using System.Runtime.InteropServices;
using System.Text;
using Consolify.Interop;

namespace Consolify.Services;

/// <summary>
/// The latest reading from a HID pad, already in XInput's shape so the rest of the gamepad
/// service does not have to know which kind of controller it is holding.
/// </summary>
/// <param name="Present">At least one HID pad is attached, whether or not it has said anything yet.</param>
/// <param name="Layout">"playstation", "switch" or "generic": which glyphs the UI should draw.</param>
/// <param name="Name">The product string, for the log and the toast.</param>
/// <param name="InstanceId">The device node, so the Bluetooth battery lookup can find the pad's own radio.</param>
/// <param name="Seq">Bumped on every parsed report, so a reader can tell a fresh reading from a repeat.</param>
/// <param name="TouchDx">Finger travel on the touchpad since the last snapshot, in pad units (1920 across). Drained by the read.</param>
/// <param name="TouchSpread">How much further apart two fingers got since the last snapshot, in pad units. Drained by the read.</param>
/// <param name="TouchFingers">Fingers on the touchpad right now, 0 to 2.</param>
/// <param name="TouchClick">The touchpad is pressed down.</param>
internal readonly record struct HidPadSnapshot(bool Present, string Layout, string Name, string? InstanceId,
    NativeMethods.XINPUT_GAMEPAD Pad, long Seq, int TouchDx, int TouchDy, double TouchSpread, int TouchFingers, bool TouchClick);

/// <summary>What one report said about the touchpad: the fingers' travel since the previous report
/// (their average), how much further apart two of them got, how many there are, and the click.</summary>
internal readonly record struct TouchSample(int Dx, int Dy, double Spread, int Fingers, bool Click);

/// <summary>
/// Reads the gamepads XInput cannot see -- a DualSense, a Switch Pro controller, any generic
/// HID pad -- and turns their reports into XINPUT_GAMEPAD, so GamepadService runs one loop over
/// every kind of controller.
///
/// Raw Input rather than Windows.Gaming.Input, for one reason: RIDEV_INPUTSINK. The launcher
/// is read while a game holds the foreground -- the menu combo, the keyboard toggle and the
/// screenshot key all have to work from inside a game -- and Raw Input is the API that promises
/// delivery to a window that is not in front. The reports arrive on the window's thread as
/// WM_INPUT and are parsed there with hid.dll, which knows the report layout from the device's
/// own descriptor; the gamepad thread only ever reads the finished snapshot.
///
/// Xbox pads also appear here as HID devices, marked with "IG_" in their path. Those are left to
/// XInput, which has the Guide button and the battery; everything else is ours.
/// </summary>
internal sealed class HidGamepadReader
{
    private readonly object _lock = new();
    private readonly Dictionary<IntPtr, HidPad> _pads = new();
    private HidPad? _last;
    private long _seq;
    private byte[] _buffer = new byte[512];
    private byte[] _report = new byte[64];
    private ushort[] _usages = new ushort[64];
    private bool _registered;

    /// <summary>A pad arrived or left. Raised on the window thread.</summary>
    public event Action<string, string, bool>? PadChanged;

    /// <summary>
    /// Ask for every joystick and gamepad's reports, wherever the input focus is, plus a note
    /// when one is plugged in or unplugged. Must be called on the thread that owns the window.
    /// </summary>
    public void Register(IntPtr hwnd)
    {
        if (_registered) return;
        var devices = new[]
        {
            new HidNative.RAWINPUTDEVICE { usUsagePage = HidNative.USAGE_PAGE_GENERIC, usUsage = HidNative.USAGE_JOYSTICK,
                dwFlags = HidNative.RIDEV_INPUTSINK | HidNative.RIDEV_DEVNOTIFY, hwndTarget = hwnd },
            new HidNative.RAWINPUTDEVICE { usUsagePage = HidNative.USAGE_PAGE_GENERIC, usUsage = HidNative.USAGE_GAMEPAD,
                dwFlags = HidNative.RIDEV_INPUTSINK | HidNative.RIDEV_DEVNOTIFY, hwndTarget = hwnd },
        };
        if (!HidNative.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<HidNative.RAWINPUTDEVICE>()))
        {
            Log.Info($"HID pads: RegisterRawInputDevices failed ({Marshal.GetLastWin32Error()})");
            return;
        }
        _registered = true;

        // Pads already attached at start never announce themselves, so walk the list once.
        uint n = 0;
        if (HidNative.GetRawInputDeviceList(null, ref n, (uint)Marshal.SizeOf<HidNative.RAWINPUTDEVICELIST>()) != 0 || n == 0) return;
        var list = new HidNative.RAWINPUTDEVICELIST[n];
        n = HidNative.GetRawInputDeviceList(list, ref n, (uint)Marshal.SizeOf<HidNative.RAWINPUTDEVICELIST>());
        if (n == unchecked((uint)-1)) return;
        for (int i = 0; i < n; i++)
            if (list[i].dwType == HidNative.RIM_TYPEHID) TryAdd(list[i].hDevice);
    }

    /// <summary>WM_INPUT_DEVICE_CHANGE: a HID device arrived (wParam 1) or left (2).</summary>
    public void OnDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        if (wParam.ToInt64() == HidNative.GIDC_ARRIVAL) { TryAdd(lParam); return; }
        if (wParam.ToInt64() != HidNative.GIDC_REMOVAL) return;

        HidPad? gone;
        lock (_lock)
        {
            if (!_pads.Remove(lParam, out gone)) return;
            if (_last == gone) _last = null;
        }
        gone.Dispose();
        Log.Info($"HID pad removed: {gone.Name}");
        PadChanged?.Invoke(gone.Layout, gone.Name, false);
    }

    /// <summary>WM_INPUT: one or more reports from a device. Parsed here, on the window thread.</summary>
    public void OnInput(IntPtr hRawInput)
    {
        uint size = 0;
        if (HidNative.GetRawInputData(hRawInput, HidNative.RID_INPUT, null, ref size, HidNative.RawInputHeaderSize) != 0 || size == 0) return;
        if (_buffer.Length < size) _buffer = new byte[size];
        if (HidNative.GetRawInputData(hRawInput, HidNative.RID_INPUT, _buffer, ref size, HidNative.RawInputHeaderSize) == unchecked((uint)-1)) return;

        if (BitConverter.ToUInt32(_buffer, 0) != HidNative.RIM_TYPEHID) return;
        var hDevice = (IntPtr)BitConverter.ToInt64(_buffer, 8);
        int header = (int)HidNative.RawInputHeaderSize;
        uint sizeHid = BitConverter.ToUInt32(_buffer, header);
        uint count = BitConverter.ToUInt32(_buffer, header + 4);
        int data = header + 8;
        if (sizeHid == 0 || count == 0 || data + sizeHid * count > size) return;

        HidPad? pad;
        lock (_lock) _pads.TryGetValue(hDevice, out pad);
        // A pad that arrived without a WM_INPUT_DEVICE_CHANGE (they are not guaranteed) is
        // still worth reading; add it on its first report.
        pad ??= TryAdd(hDevice);
        if (pad is null) return;

        if (_report.Length < sizeHid) _report = new byte[sizeHid];
        if (_usages.Length < pad.MaxUsages) _usages = new ushort[pad.MaxUsages];
        bool any = false;
        var state = default(NativeMethods.XINPUT_GAMEPAD);
        var touch = default(TouchSample);
        for (uint i = 0; i < count; i++)
        {
            Buffer.BlockCopy(_buffer, data + (int)(i * sizeHid), _report, 0, (int)sizeHid);
            if (!pad.Parse(_report, sizeHid, _usages, out var parsed, out var t)) continue;
            any = true;
            state = parsed;
            // Travel adds up across the reports in one message; fingers and the click are the latest word.
            touch = new TouchSample(touch.Dx + t.Dx, touch.Dy + t.Dy, touch.Spread + t.Spread, t.Fingers, t.Click);
        }
        if (!any) return;

        lock (_lock)
        {
            pad.State = state;
            pad.TouchDx += touch.Dx;
            pad.TouchDy += touch.Dy;
            pad.TouchSpread += touch.Spread;
            pad.TouchFingers = touch.Fingers;
            pad.TouchClick = touch.Click;
            _last = pad;
            _seq++;
        }
    }

    /// <summary>What the gamepad thread reads every tick. Reading drains the touchpad travel.</summary>
    public HidPadSnapshot Snapshot()
    {
        lock (_lock)
        {
            if (_pads.Count == 0) return default;
            var p = _last ?? _pads.Values.First();
            var snap = new HidPadSnapshot(true, p.Layout, p.Name, p.InstanceId, p.State, _seq,
                p.TouchDx, p.TouchDy, p.TouchSpread, p.TouchFingers, p.TouchClick);
            p.TouchDx = 0;
            p.TouchDy = 0;
            p.TouchSpread = 0;
            return snap;
        }
    }

    private HidPad? TryAdd(IntPtr hDevice)
    {
        lock (_lock) if (_pads.ContainsKey(hDevice)) return _pads[hDevice];

        HidPad? pad;
        try { pad = HidPad.Open(hDevice); }
        catch (Exception ex) { Log.Info($"HID pad: could not open device: {ex.Message}"); return null; }
        if (pad is null) return null;

        lock (_lock) _pads[hDevice] = pad;
        Log.Info($"HID pad: {pad.Name} ({pad.Layout}, VID {pad.Vid:X4} PID {pad.Pid:X4}, {pad.ButtonCount} buttons)");
        PadChanged?.Invoke(pad.Layout, pad.Name, true);
        return pad;
    }
}

/// <summary>Where a HID button lands in XInput's button word.</summary>
internal enum PadButton { None, A, B, X, Y, LB, RB, LT, RT, Back, Start, LS, RS, Guide }

/// <summary>One HID gamepad: its parsed descriptor, its button map, and its last state.</summary>
internal sealed class HidPad : IDisposable
{
    public string Name { get; private set; } = "";
    public string Layout { get; private set; } = "generic";
    public string InstanceId { get; private set; } = "";
    public uint Vid { get; private set; }
    public uint Pid { get; private set; }
    public int ButtonCount { get; private set; }
    /// <summary>The longest usage list a report can carry, so the reader's buffer is never too small.</summary>
    public int MaxUsages { get; private set; }
    public NativeMethods.XINPUT_GAMEPAD State;
    /// <summary>Touchpad travel accumulated since the reader last drained it, plus the latest finger count and click.</summary>
    public int TouchDx, TouchDy, TouchFingers;
    public double TouchSpread;
    public bool TouchClick;

    private IntPtr _preparsed;
    private PadButton[] _map = Array.Empty<PadButton>();
    private readonly Dictionary<ushort, Axis> _axes = new();
    private uint _maxButtons, _maxGeneric;
    private byte _reportId;
    private bool _rightStickOnZ, _triggersOnRxRy;
    private SonyLayout? _sony;
    private struct Contact { public int Id, X, Y; }
    private readonly Contact[] _contacts = { new() { Id = -1 }, new() { Id = -1 } };
    private double _lastSpread = -1;
    private bool _nativeLogged;

    private readonly record struct Axis(ushort LinkCollection, int Min, int Max, ushort BitSize, bool HasNull, byte ReportId);

    /* ---- Sony's full reports, read by hand ----

       The descriptor-described part of a DualSense report stops at the buttons; the touchpad,
       like the gyro, lives in the vendor bytes after it, which hid.dll cannot name. The layout is
       the same one the Linux hid-playstation driver reads. Over USB the pad sends it by default
       (report 0x01, 64 bytes). Over Bluetooth it sends a 10-byte 0x01 with no touch data until
       something reads its calibration feature report, after which it switches to the full 0x31 --
       the same body at a different offset, with a CRC on the end. Steam does exactly that read,
       and it used to leave the pad unreadable here; now it is what we ask for ourselves.

       Offsets are from the start of the body. The DualShock 4 is laid out differently and is
       unverified on hardware. */
    private readonly record struct SonyLayout(int Axes, int Triggers, int Buttons, int Touch,
        byte UsbReport, int UsbLength, byte BtReport, int BtBody, int BtLength, byte FeatureReport, int FeatureLength);

    private static readonly SonyLayout DualSenseLayout = new(Axes: 0, Triggers: 4, Buttons: 7, Touch: 32,
        UsbReport: 0x01, UsbLength: 64, BtReport: 0x31, BtBody: 2, BtLength: 78, FeatureReport: 0x05, FeatureLength: 41);
    private static readonly SonyLayout DualShock4Layout = new(Axes: 0, Triggers: 7, Buttons: 4, Touch: 34,
        UsbReport: 0x01, UsbLength: 64, BtReport: 0x11, BtBody: 3, BtLength: 78, FeatureReport: 0x02, FeatureLength: 37);

    private static SonyLayout? SonyLayoutFor(uint vid, uint pid)
    {
        if (vid != 0x054C) return null;
        return pid switch
        {
            0x0CE6 or 0x0DF2 => DualSenseLayout,            // DualSense, DualSense Edge
            0x05C4 or 0x09CC or 0x0BA0 => DualShock4Layout,  // DualShock 4 v1, v2, USB wireless adapter
            _ => null,
        };
    }

    /// <summary>A HID-over-Bluetooth device path carries the HID profile's service UUID.</summary>
    private static bool IsBluetooth(string path) =>
        path.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ask a Sony pad on Bluetooth for its full reports, by reading the calibration feature
    /// report -- the read itself is the switch. Needs a read/write handle, which is refused
    /// while another program holds the pad exclusively; that is only logged.
    /// </summary>
    private static void EnableFullReports(string path, in SonyLayout l, string name)
    {
        try
        {
            using var h = HidNative.CreateFile(path, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                Log.Info($"HID pad: {name}: cannot open for the full-report switch (error {Marshal.GetLastWin32Error()}); no touchpad over Bluetooth");
                return;
            }
            var buf = new byte[l.FeatureLength];
            buf[0] = l.FeatureReport;
            bool ok = HidNative.HidD_GetFeature(h, buf, (uint)buf.Length);
            Log.Info(ok ? $"HID pad: {name}: full Bluetooth reports requested"
                        : $"HID pad: {name}: full-report switch refused (error {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex) { Log.Info($"HID pad: {name}: full-report switch failed: {ex.Message}"); }
    }

    /// <summary>
    /// One of Sony's full reports to a state, touchpad included. False for anything else -- the
    /// short Bluetooth report among them, which the descriptor path still handles.
    /// </summary>
    private bool ParseSony(in SonyLayout l, byte[] r, uint length, out NativeMethods.XINPUT_GAMEPAD pad, out TouchSample touch)
    {
        pad = default;
        touch = default;
        int o;
        if (r[0] == l.UsbReport && length >= l.UsbLength) o = 1;
        else if (r[0] == l.BtReport && length >= l.BtLength) o = l.BtBody;
        else return false;

        byte b0 = r[o + l.Buttons], b1 = r[o + l.Buttons + 1], b2 = r[o + l.Buttons + 2];
        ushort buttons = 0;
        // The hat is the low nibble of the first button byte: 0 is up, clockwise to 7, 8 is released.
        int hat = b0 & 0x0F;
        if (hat <= 7)
        {
            if (hat is 7 or 0 or 1) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_UP;
            if (hat is 1 or 2 or 3) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;
            if (hat is 3 or 4 or 5) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN;
            if (hat is 5 or 6 or 7) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT;
        }
        if ((b0 & 0x10) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_X;              // Square
        if ((b0 & 0x20) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_A;              // Cross
        if ((b0 & 0x40) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_B;              // Circle
        if ((b0 & 0x80) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_Y;              // Triangle
        if ((b1 & 0x01) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER;  // L1
        if ((b1 & 0x02) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER; // R1
        if ((b1 & 0x10) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_BACK;           // Create / Share
        if ((b1 & 0x20) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_START;          // Options
        if ((b1 & 0x40) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_LEFT_THUMB;     // L3
        if ((b1 & 0x80) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB;    // R3
        if ((b2 & 0x01) != 0) buttons |= NativeMethods.XINPUT_GAMEPAD_GUIDE;          // PS
        pad.wButtons = buttons;

        pad.sThumbLX = Axis8(r[o + l.Axes], false);
        pad.sThumbLY = Axis8(r[o + l.Axes + 1], true);
        pad.sThumbRX = Axis8(r[o + l.Axes + 2], false);
        pad.sThumbRY = Axis8(r[o + l.Axes + 3], true);
        pad.bLeftTrigger = Math.Max(r[o + l.Triggers], (b1 & 0x04) != 0 ? (byte)255 : (byte)0);
        pad.bRightTrigger = Math.Max(r[o + l.Triggers + 1], (b1 & 0x08) != 0 ? (byte)255 : (byte)0);

        // Two touch points, four bytes each: a contact byte whose top bit is set while NOTHING is
        // touching and whose low bits number the touch, then x (12 bits) and y (12 bits) packed.
        // Travel is only counted while the same numbered touch continues, so a finger lifting
        // and landing elsewhere never throws the pointer across the screen.
        //
        // Both points are tracked. The travel reported is the AVERAGE of the contacts that carried
        // on from the last report -- the centroid's motion -- which is what a two-finger scroll
        // follows and, with one finger, is simply that finger. The spread is how much further
        // apart the two got, for a pinch, and only counts while both carried on.
        int p = o + l.Touch;
        int fingers = 0, moved = 0, sumDx = 0, sumDy = 0;
        bool bothContinued = true;
        for (int slot = 0; slot < 2; slot++)
        {
            int q = p + slot * 4;
            ref var c = ref _contacts[slot];
            if ((r[q] & 0x80) != 0) { c.Id = -1; bothContinued = false; continue; }
            fingers++;
            int id = r[q] & 0x7F;
            int x = ((r[q + 2] & 0x0F) << 8) | r[q + 1];
            int y = (r[q + 3] << 4) | (r[q + 2] >> 4);
            if (c.Id == id) { sumDx += x - c.X; sumDy += y - c.Y; moved++; }
            else bothContinued = false;
            c.Id = id; c.X = x; c.Y = y;
        }
        int dx = moved > 0 ? sumDx / moved : 0, dy = moved > 0 ? sumDy / moved : 0;

        double spread = 0;
        double dist = fingers == 2
            ? Math.Sqrt(Math.Pow(_contacts[0].X - _contacts[1].X, 2) + Math.Pow(_contacts[0].Y - _contacts[1].Y, 2))
            : -1;
        if (bothContinued && fingers == 2 && _lastSpread >= 0) spread = dist - _lastSpread;
        _lastSpread = dist;

        touch = new TouchSample(dx, dy, spread, fingers, (b2 & 0x02) != 0);

        if (!_nativeLogged)
        {
            _nativeLogged = true;
            Log.Info($"HID pad: {Name}: reading full reports (id 0x{r[0]:X2}, {length} bytes; touch bytes {Convert.ToHexString(r, p, 8)})");
        }
        return true;
    }

    private static short Axis8(byte v, bool invert)
    {
        double t = (v - 127.5) / 127.5;
        if (invert) t = -t;
        return (short)Math.Round(Math.Clamp(t, -1, 1) * 32767);
    }

    // Generic Desktop usages
    private const ushort X = 0x30, Y = 0x31, Z = 0x32, Rx = 0x33, Ry = 0x34, Rz = 0x35, Hat = 0x39;
    private const ushort DpadUp = 0x90, DpadDown = 0x91, DpadRight = 0x92, DpadLeft = 0x93;

    /* ---- button maps, by HID button number (1-based) ----

       Sony's order, which is also what most generic pads follow because most of them are shaped
       like a DualShock: Square, Cross, Circle, Triangle, L1, R1, L2, R2, Share/Create, Options,
       L3, R3, PS, then the touchpad and the mute button, which have no XInput equivalent. It is
       the order the DualShock 4 and the DualSense declare in their own report descriptors. */
    private static readonly PadButton[] SonyMap =
    {
        PadButton.X, PadButton.A, PadButton.B, PadButton.Y, PadButton.LB, PadButton.RB, PadButton.LT, PadButton.RT,
        PadButton.Back, PadButton.Start, PadButton.LS, PadButton.RS, PadButton.Guide, PadButton.None, PadButton.None,
    };

    /* The Switch Pro controller in the "simple HID" mode it uses over Bluetooth: B, A, Y, X, L, R,
       ZL, ZR, Minus, Plus, L stick, R stick, Home, Capture. Mapped by POSITION, the way Steam and
       Windows both do it: Nintendo's B is the bottom button, so it is "A" to the launcher, and the
       legend draws the B glyph next to "Select" so what is shown is what is pressed. (Over USB the
       Pro controller sends nothing until it has been through a handshake; Bluetooth is the way.) */
    private static readonly PadButton[] SwitchMap =
    {
        PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LB, PadButton.RB, PadButton.LT, PadButton.RT,
        PadButton.Back, PadButton.Start, PadButton.LS, PadButton.RS, PadButton.Guide, PadButton.None,
    };

    private static (string layout, PadButton[] map) LayoutFor(uint vid, uint pid)
    {
        if (vid == 0x054C) return ("playstation", SonyMap);   // Sony: DualShock 4, DualSense, DualSense Edge
        if (vid == 0x057E) return ("switch", SwitchMap);      // Nintendo: Pro Controller, and 8BitDo pads in Switch mode
        return ("generic", SonyMap);
    }

    public static HidPad? Open(IntPtr hDevice)
    {
        // What kind of device, and is it one of ours?
        var info = new HidNative.RID_DEVICE_INFO { cbSize = 32 };
        uint infoSize = 32;
        var pInfo = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.StructureToPtr(info, pInfo, false);
            if (HidNative.GetRawInputDeviceInfo(hDevice, HidNative.RIDI_DEVICEINFO, pInfo, ref infoSize) == unchecked((uint)-1)) return null;
            info = Marshal.PtrToStructure<HidNative.RID_DEVICE_INFO>(pInfo);
        }
        finally { Marshal.FreeHGlobal(pInfo); }
        if (info.dwType != HidNative.RIM_TYPEHID) return null;
        if (info.usUsagePage != HidNative.USAGE_PAGE_GENERIC
            || (info.usUsage != HidNative.USAGE_JOYSTICK && info.usUsage != HidNative.USAGE_GAMEPAD)) return null;

        var path = DeviceName(hDevice);
        if (path is null) return null;
        // XInput's own devices. XInput has the Guide button and the battery for them, and reading
        // the same pad twice would double every press.
        if (path.Contains("IG_", StringComparison.OrdinalIgnoreCase)) return null;

        // The descriptor, parsed by hid.dll.
        uint ppSize = 0;
        if (HidNative.GetRawInputDeviceInfo(hDevice, HidNative.RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppSize) != 0 || ppSize == 0) return null;
        var preparsed = Marshal.AllocHGlobal((int)ppSize);
        if (HidNative.GetRawInputDeviceInfo(hDevice, HidNative.RIDI_PREPARSEDDATA, preparsed, ref ppSize) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        var pad = new HidPad { _preparsed = preparsed, Vid = info.dwVendorId, Pid = info.dwProductId, InstanceId = InstanceIdOf(path) };
        (pad.Layout, pad._map) = LayoutFor(info.dwVendorId, info.dwProductId);
        pad.Name = ProductString(path) ?? $"{pad.Layout} controller {info.dwVendorId:X4}:{info.dwProductId:X4}";
        if (!pad.ReadCaps()) { pad.Dispose(); return null; }
        pad._sony = SonyLayoutFor(info.dwVendorId, info.dwProductId);
        // On the cable the full report is what the pad sends anyway; on Bluetooth it has to be asked.
        if (pad._sony is { } sony && IsBluetooth(path)) EnableFullReports(path, sony, pad.Name);
        return pad;
    }

    private bool ReadCaps()
    {
        if (HidNative.HidP_GetCaps(_preparsed, out var caps) != HidNative.HIDP_STATUS_SUCCESS) return false;
        _maxButtons = HidNative.HidP_MaxUsageListLength(HidNative.HidP_Input, HidNative.USAGE_PAGE_BUTTON, _preparsed);
        _maxGeneric = HidNative.HidP_MaxUsageListLength(HidNative.HidP_Input, HidNative.USAGE_PAGE_GENERIC, _preparsed);
        ButtonCount = (int)_maxButtons;
        MaxUsages = (int)Math.Max(1, Math.Max(_maxButtons, _maxGeneric));

        ushort n = caps.NumberInputValueCaps;
        if (n > 0)
        {
            var values = new HidNative.HIDP_VALUE_CAPS[n];
            if (HidNative.HidP_GetValueCaps(HidNative.HidP_Input, values, ref n, _preparsed) == HidNative.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var v = values[i];
                    if (v.UsagePage != HidNative.USAGE_PAGE_GENERIC) continue;
                    ushort first = v.UsageMin, last = v.IsRange != 0 ? v.UsageMax : v.UsageMin;
                    for (int u = first; u <= last; u++)
                    {
                        if (u is not (X or Y or Z or Rx or Ry or Rz or Hat)) continue;
                        // The first declaration of an axis wins; a second is an alias or a
                        // second report we are not going to read.
                        _axes.TryAdd((ushort)u, new Axis(v.LinkCollection, v.LogicalMin, v.LogicalMax, v.BitSize, v.HasNull != 0, v.ReportID));
                    }
                }
            }
        }

        // Which report carries the sticks is the one we read; anything else the pad sends (a
        // DualSense on Bluetooth has an undescribed full-state report as well) is skipped.
        _reportId = _axes.TryGetValue(X, out var xa) ? xa.ReportId : (byte)0;

        /* Two conventions for the other four axes. Sony-shaped pads put the right stick on Z/Rz and
           the triggers on Rx/Ry; the rest put the right stick on Rx/Ry and, if they have analog
           triggers at all, share Z between them. Z and Rz together means the first. */
        _rightStickOnZ = _axes.ContainsKey(Z) && _axes.ContainsKey(Rz);
        _triggersOnRxRy = _rightStickOnZ && _axes.ContainsKey(Rx) && _axes.ContainsKey(Ry);
        return _axes.ContainsKey(X) || _maxButtons > 0;
    }

    /// <summary>
    /// One report to an XInput state. False when the report is not the one the sticks live in.
    /// A Sony pad's full report is read by hand first, touchpad and all; anything else goes
    /// through the descriptor.
    /// </summary>
    public bool Parse(byte[] report, uint length, ushort[] usages, out NativeMethods.XINPUT_GAMEPAD pad, out TouchSample touch)
    {
        pad = default;
        touch = default;
        if (length == 0) return false;
        if (_sony is { } sony && ParseSony(sony, report, length, out pad, out touch)) return true;
        if (report[0] != _reportId) return false;

        ushort buttons = 0;
        byte lt = 0, rt = 0;

        // Buttons: the list of every pressed button number, mapped by position in the table.
        if (_maxButtons > 0)
        {
            uint count = Math.Min(_maxButtons, (uint)usages.Length);
            if (HidNative.HidP_GetUsages(HidNative.HidP_Input, HidNative.USAGE_PAGE_BUTTON, 0, usages, ref count, _preparsed, report, length)
                == HidNative.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < count; i++)
                {
                    int idx = usages[i] - 1;
                    var b = idx >= 0 && idx < _map.Length ? _map[idx] : PadButton.None;
                    switch (b)
                    {
                        case PadButton.A: buttons |= NativeMethods.XINPUT_GAMEPAD_A; break;
                        case PadButton.B: buttons |= NativeMethods.XINPUT_GAMEPAD_B; break;
                        case PadButton.X: buttons |= NativeMethods.XINPUT_GAMEPAD_X; break;
                        case PadButton.Y: buttons |= NativeMethods.XINPUT_GAMEPAD_Y; break;
                        case PadButton.LB: buttons |= NativeMethods.XINPUT_GAMEPAD_LEFT_SHOULDER; break;
                        case PadButton.RB: buttons |= NativeMethods.XINPUT_GAMEPAD_RIGHT_SHOULDER; break;
                        case PadButton.Back: buttons |= NativeMethods.XINPUT_GAMEPAD_BACK; break;
                        case PadButton.Start: buttons |= NativeMethods.XINPUT_GAMEPAD_START; break;
                        case PadButton.LS: buttons |= NativeMethods.XINPUT_GAMEPAD_LEFT_THUMB; break;
                        case PadButton.RS: buttons |= NativeMethods.XINPUT_GAMEPAD_RIGHT_THUMB; break;
                        case PadButton.Guide: buttons |= NativeMethods.XINPUT_GAMEPAD_GUIDE; break;
                        // A digital trigger press is a full pull; the analog axis below can only raise it.
                        case PadButton.LT: lt = 255; break;
                        case PadButton.RT: rt = 255; break;
                    }
                }
            }
        }

        // A D-pad declared as four buttons rather than a hat switch.
        if (_maxGeneric > 0)
        {
            uint count = Math.Min(_maxGeneric, (uint)usages.Length);
            if (HidNative.HidP_GetUsages(HidNative.HidP_Input, HidNative.USAGE_PAGE_GENERIC, 0, usages, ref count, _preparsed, report, length)
                == HidNative.HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < count; i++)
                    buttons |= usages[i] switch
                    {
                        DpadUp => NativeMethods.XINPUT_GAMEPAD_DPAD_UP,
                        DpadDown => NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN,
                        DpadLeft => NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT,
                        DpadRight => NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT,
                        _ => (ushort)0,
                    };
            }
        }

        // The hat switch: a direction index clockwise from up, or a value outside the range for
        // "released". Eight-way is usual; the arithmetic copes with four.
        if (_axes.TryGetValue(Hat, out var hat) && Raw(Hat, hat, report, length, out long h))
        {
            int steps = hat.Max - hat.Min + 1;
            if (h >= hat.Min && h <= hat.Max && steps > 0)
            {
                double deg = (h - hat.Min) * 360.0 / steps;
                if (deg >= 315 || deg <= 45) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_UP;
                if (deg >= 45 && deg <= 135) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_RIGHT;
                if (deg >= 135 && deg <= 225) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_DOWN;
                if (deg >= 225 && deg <= 315) buttons |= NativeMethods.XINPUT_GAMEPAD_DPAD_LEFT;
            }
        }

        pad.wButtons = buttons;
        pad.sThumbLX = Stick(X, report, length, false);
        pad.sThumbLY = Stick(Y, report, length, true);
        pad.sThumbRX = Stick(_rightStickOnZ ? Z : Rx, report, length, false);
        pad.sThumbRY = Stick(_rightStickOnZ ? Rz : Ry, report, length, true);
        if (_triggersOnRxRy)
        {
            lt = Math.Max(lt, Trigger(Rx, report, length));
            rt = Math.Max(rt, Trigger(Ry, report, length));
        }
        pad.bLeftTrigger = lt;
        pad.bRightTrigger = rt;
        return true;
    }

    private bool Raw(ushort usage, Axis a, byte[] report, uint length, out long value)
    {
        value = 0;
        if (HidNative.HidP_GetUsageValue(HidNative.HidP_Input, HidNative.USAGE_PAGE_GENERIC, a.LinkCollection, usage, out uint raw, _preparsed, report, length)
            != HidNative.HIDP_STATUS_SUCCESS) return false;
        value = raw;
        // A signed axis comes back as its two's-complement bit pattern.
        if (a.Min < 0 && a.BitSize > 0 && a.BitSize < 32 && (value & (1L << (a.BitSize - 1))) != 0) value -= 1L << a.BitSize;
        return true;
    }

    /// <summary>0..1 along an axis, or null when the pad has no such axis.</summary>
    private double? Unit(ushort usage, byte[] report, uint length)
    {
        if (!_axes.TryGetValue(usage, out var a) || a.Max <= a.Min) return null;
        if (!Raw(usage, a, report, length, out long v)) return null;
        return Math.Clamp((v - a.Min) / (double)(a.Max - a.Min), 0, 1);
    }

    /// <summary>A stick axis as XInput reports it: -32768..32767, up and right positive.</summary>
    private short Stick(ushort usage, byte[] report, uint length, bool invert)
    {
        var t = Unit(usage, report, length);
        if (t is null) return 0;
        double v = t.Value * 2 - 1;
        if (invert) v = -v;   // HID's Y grows downwards; XInput's grows upwards
        return (short)Math.Round(Math.Clamp(v, -1, 1) * 32767);
    }

    private byte Trigger(ushort usage, byte[] report, uint length)
    {
        var t = Unit(usage, report, length);
        return t is null ? (byte)0 : (byte)Math.Round(t.Value * 255);
    }

    // ---- naming ----

    private static string? DeviceName(IntPtr hDevice)
    {
        uint chars = 0;
        if (HidNative.GetRawInputDeviceInfo(hDevice, HidNative.RIDI_DEVICENAME, IntPtr.Zero, ref chars) != 0 || chars == 0) return null;
        var p = Marshal.AllocHGlobal((int)chars * 2 + 2);
        try
        {
            if (HidNative.GetRawInputDeviceInfo(hDevice, HidNative.RIDI_DEVICENAME, p, ref chars) == unchecked((uint)-1)) return null;
            return Marshal.PtrToStringUni(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// The device-interface path Raw Input names a device by, turned into the instance id the
    /// configuration manager uses: strip the prefix, drop the interface class, and the '#'s
    /// were '\'s. That is the id the Bluetooth battery lookup wants.
    /// </summary>
    private static string InstanceIdOf(string path)
    {
        var s = path;
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s.Substring(4);
        int guid = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0) s = s.Substring(0, guid);
        return s.Replace('#', '\\');
    }

    private static string? ProductString(string path)
    {
        try
        {
            using var h = HidNative.CreateFile(path, 0, HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero,
                HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var sb = new StringBuilder(128);
            if (!HidNative.HidD_GetProductString(h, sb, (uint)sb.Capacity * 2)) return null;
            var name = sb.ToString().Trim();
            return name.Length > 0 ? name : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_preparsed != IntPtr.Zero) { Marshal.FreeHGlobal(_preparsed); _preparsed = IntPtr.Zero; }
    }
}
