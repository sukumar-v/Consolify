using System.Runtime.InteropServices;
using System.Text;

namespace CouchLauncher.Interop;

internal static class NativeMethods
{
    // ---- Display enumeration / primary switching ----

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint DM_POSITION = 0x00000020;

    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_NORESET = 0x10000000;
    public const uint CDS_SET_PRIMARY = 0x00000010;

    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    public const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    // ---- Window management ----

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_CLOSE = 0xF060;
    public const int SC_MONITORPOWER = 0xF170;
    public const int MONITOR_OFF = 2, MONITOR_ON = -1;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll")]
    public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Broadcast form of SendMessage. The timeout matters: a plain broadcast blocks on
    /// any hung top-level window, which would freeze the UI thread.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    // ---- Process image path (more reliable than Process.MainModule cross-arch) ----

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    // ---- Cursor / mouse input ----

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    // ---- System cursor replacement (optional system-wide pointer hiding) ----

    public const uint OCR_NORMAL = 32512;
    public const uint OCR_IBEAM = 32513;
    public const uint OCR_WAIT = 32514;
    public const uint OCR_CROSS = 32515;
    public const uint OCR_UP = 32516;
    public const uint OCR_SIZENWSE = 32642;
    public const uint OCR_SIZENESW = 32643;
    public const uint OCR_SIZEWE = 32644;
    public const uint OCR_SIZENS = 32645;
    public const uint OCR_SIZEALL = 32646;
    public const uint OCR_NO = 32648;
    public const uint OCR_HAND = 32649;
    public const uint OCR_APPSTARTING = 32650;

    public static readonly uint[] SystemCursorIds =
    {
        OCR_NORMAL, OCR_IBEAM, OCR_CROSS, OCR_UP, OCR_SIZENWSE, OCR_SIZENESW,
        OCR_SIZEWE, OCR_SIZENS, OCR_SIZEALL, OCR_NO, OCR_HAND, OCR_APPSTARTING
    };

    public const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot,
        int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    public static extern bool DestroyCursor(IntPtr hCursor);

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi; // union collapsed to mouse-only; keyboard input is out of scope
        // pad to the size of the largest union member (KEYBDINPUT) — MOUSEINPUT is already the largest
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ---- XInput ----

    public const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
    public const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
    public const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
    public const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    public const ushort XINPUT_GAMEPAD_START = 0x0010;
    public const ushort XINPUT_GAMEPAD_BACK = 0x0020;
    public const ushort XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
    public const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
    public const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
    public const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
    public const ushort XINPUT_GAMEPAD_A = 0x1000;
    public const ushort XINPUT_GAMEPAD_B = 0x2000;
    public const ushort XINPUT_GAMEPAD_X = 0x4000;
    public const ushort XINPUT_GAMEPAD_Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState910(int dwUserIndex, out XINPUT_STATE pState);

    // Battery (xinput1_4 only)
    public const byte BATTERY_DEVTYPE_GAMEPAD = 0;
    public const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    public const byte BATTERY_TYPE_WIRED = 0x01;
    public const byte BATTERY_TYPE_ALKALINE = 0x02;
    public const byte BATTERY_TYPE_NIMH = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel; // 0 empty .. 3 full
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern int XInputGetBatteryInformation14(int dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    public static bool TryGetBattery(int userIndex, out XINPUT_BATTERY_INFORMATION info)
    {
        info = default;
        if (_xinput14Missing) return false;
        try { return XInputGetBatteryInformation14(userIndex, BATTERY_DEVTYPE_GAMEPAD, out info) == 0; }
        catch (DllNotFoundException) { _xinput14Missing = true; return false; }
        catch (EntryPointNotFoundException) { return false; }
    }


    // ---- Window control (radial power menu) ----

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr ordinal);

    // ---- Guide button ----
    // XInput's public API deliberately hides the Guide/PS button. Ordinal 100 of the XInput DLLs
    // is the long-standing undocumented XInputGetStateEx, which reports it as bit 0x0400. If the
    // export is missing we fall back to the documented call and the Guide bit simply never sets.
    public const ushort XINPUT_GAMEPAD_GUIDE = 0x0400;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int XInputGetStateExFn(int dwUserIndex, out XINPUT_STATE pState);

    private static XInputGetStateExFn? _getStateEx;
    private static bool _exProbed;

    public static bool GuideSupported
    {
        get { ProbeStateEx(); return _getStateEx is not null; }
    }

    private static void ProbeStateEx()
    {
        if (_exProbed) return;
        _exProbed = true;
        foreach (var dll in new[] { "xinput1_4.dll", "xinput1_3.dll" })
        {
            var h = LoadLibrary(dll);
            if (h == IntPtr.Zero) continue;
            var p = GetProcAddress(h, new IntPtr(100));
            if (p == IntPtr.Zero) continue;
            _getStateEx = Marshal.GetDelegateForFunctionPointer<XInputGetStateExFn>(p);
            return;
        }
    }

    /// <summary>State including the Guide bit when the extended export exists.</summary>
    public static int XInputGetStateAny(int userIndex, out XINPUT_STATE state)
    {
        ProbeStateEx();
        if (_getStateEx is not null)
        {
            try { return _getStateEx(userIndex, out state); }
            catch { _getStateEx = null; }
        }
        return XInputGetState(userIndex, out state);
    }
    private static bool _xinput14Missing;

    public static int XInputGetState(int userIndex, out XINPUT_STATE state)
    {
        if (!_xinput14Missing)
        {
            try { return XInputGetState14(userIndex, out state); }
            catch (DllNotFoundException) { _xinput14Missing = true; }
        }
        return XInputGetState910(userIndex, out state);
    }
}
