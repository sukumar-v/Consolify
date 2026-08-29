using System.Diagnostics;
using System.Text;
using CouchLauncher.Interop;

namespace CouchLauncher.Services;

public record WindowInfo(long Handle, string Title, string ProcessName, bool Minimized, string Display);

/// <summary>
/// The window and system actions behind the radial power menu, so a gamepad can drive Windows
/// itself: list and focus open windows, close or minimize one, throw one onto the TV, launch a
/// handful of shell shortcuts, and run power actions.
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

    public void Minimize(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
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

    /// <summary>Cycle a window to the next display in the list.</summary>
    public void MoveToNextDisplay(IntPtr hwnd)
    {
        var all = _displays.GetDisplays();
        if (all.Count < 2) return;
        var cur = DisplayOf(hwnd);
        int i = all.FindIndex(d => d.DeviceName == cur);
        MoveToDisplay(hwnd, all[(i + 1) % all.Count].DeviceName);
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

    /// <summary>Power actions. Destructive ones are confirmed in the UI before reaching here.</summary>
    public void Power(string action)
    {
        try
        {
            switch (action)
            {
                case "sleep": Process.Start("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"); break;
                case "shutdown": Process.Start("shutdown.exe", "/s /t 0"); break;
                case "restart": Process.Start("shutdown.exe", "/r /t 0"); break;
                case "signout": Process.Start("shutdown.exe", "/l"); break;
                default: Log.Info($"Radial: unknown power action {action}"); break;
            }
            Log.Info($"Radial: power action {action}");
        }
        catch (Exception ex) { Log.Info($"Radial power {action} failed: {ex.Message}"); }
    }

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
