using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CouchLauncher.Interop;

namespace CouchLauncher.Services;

public record WindowInfo(long Handle, string Title, string ProcessName, bool Minimized, string Display);

/// <summary>
/// The window and system actions behind the radial menu, so a gamepad can drive Windows itself:
/// list open windows and bring one to the TV, close one, launch a handful of shell shortcuts,
/// park the pointer, and blank or wake the displays.
/// </summary>
public class WindowService
{
    private readonly DisplayService _displays;
    private IntPtr _ownWindow;

    public WindowService(DisplayService displays) => _displays = displays;

    public void SetOwnWindow(IntPtr hwnd) => _ownWindow = hwnd;

    /// <summary>Visible, titled, top-level windows — roughly what alt-tab would show.</summary>
    public List<WindowInfo> ListWindows()
    {
        var list = new List<WindowInfo>();
        var shell = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || hwnd == _ownWindow) return true;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return true;

            string proc = "";
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                proc = Process.GetProcessById((int)pid).ProcessName;
            }
            catch { /* process may be protected or gone */ }

            list.Add(new WindowInfo((long)hwnd, title, proc,
                NativeMethods.IsIconic(hwnd), DisplayOf(hwnd)));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>Which display a window's centre currently sits on.</summary>
    public string DisplayOf(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return "";
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        foreach (var d in _displays.GetDisplays())
            if (cx >= d.X && cx < d.X + d.Width && cy >= d.Y && cy < d.Y + d.Height)
                return d.DeviceName;
        return "";
    }

    public string TitleOf(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Polite close (WM_CLOSE); the app decides whether to prompt to save.</summary>
    public void Close(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.PostMessage(hwnd, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_CLOSE), IntPtr.Zero);
        Log.Info($"Radial: closed window {hwnd}");
    }

    public void Focus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>Move a window onto a display, keeping its size unless it would not fit.</summary>
    public void MoveToDisplay(IntPtr hwnd, string deviceName)
    {
        var d = _displays.GetDisplay(deviceName);
        if (d is null || hwnd == IntPtr.Zero) return;
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;

        int w = Math.Min(r.Right - r.Left, d.Width);
        int h = Math.Min(r.Bottom - r.Top, d.Height);
        int x = d.X + (d.Width - w) / 2;
        int y = d.Y + (d.Height - h) / 2;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
        Log.Info($"Radial: moved window {hwnd} to {deviceName}");
    }

    /// <summary>Park the pointer in the middle of a display — a quick way to retrieve a cursor
    /// that has wandered onto another monitor.</summary>
    public void CenterCursorOn(string deviceName)
    {
        var d = _displays.GetDisplay(deviceName) ?? _displays.GetDisplays().FirstOrDefault(x => x.IsPrimary);
        if (d is null) return;
        NativeMethods.MoveCursorTo(d.X + d.Width / 2, d.Y + d.Height / 2);
        Log.Info($"Radial: centred cursor on {d.DeviceName}");
    }

    public void RunShortcut(string id)
    {
        try
        {
            switch (id)
            {
                case "taskManager": Start("taskmgr.exe"); break;
                case "explorer": Start("explorer.exe"); break;
                case "settings": Start("ms-settings:"); break;
                case "displaySettings": Start("ms-settings:display"); break;
                case "volume": Start("sndvol.exe"); break;
                case "lock": Process.Start("rundll32.exe", "user32.dll,LockWorkStation"); break;
                default: Log.Info($"Radial: unknown shortcut {id}"); break;
            }
        }
        catch (Exception ex) { Log.Info($"Radial shortcut {id} failed: {ex.Message}"); }
    }

    /// <summary>
    /// "Suspend": drop the displays into standby without suspending the machine. Sleep would need
    /// Wake-on-LAN or a wake-capable receiver to come back from; this just blanks the TV, leaves
    /// the session and anything running in it alone, and any gamepad button wakes it.
    /// </summary>
    public void BlankDisplays()
    {
        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
            new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(NativeMethods.MONITOR_OFF),
            NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);
        Log.Info("Suspend: displays off");
    }

    /// <summary>
    /// Bring the displays back. The SC_MONITORPOWER "on" message alone is unreliable once the
    /// monitors have actually powered down, so a nudge of real mouse input backs it up — that is
    /// the signal Windows itself treats as a wake.
    /// </summary>
    public void WakeDisplays()
    {
        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
            new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(NativeMethods.MONITOR_ON),
            NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);

        var inputs = new[]
        {
            new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, mi = new NativeMethods.MOUSEINPUT { dx = 1, dwFlags = NativeMethods.MOUSEEVENTF_MOVE } },
            new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, mi = new NativeMethods.MOUSEINPUT { dx = -1, dwFlags = NativeMethods.MOUSEEVENTF_MOVE } },
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        Log.Info("Suspend: displays on");
    }

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
