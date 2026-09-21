using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Consolify.Interop;

namespace Consolify.Services;

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
            if (!IsAltTabWindow(hwnd)) return true;

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

            if (NeverSwitchable.Contains(proc, StringComparer.OrdinalIgnoreCase)) return true;

            // A Store app's window belongs to the frame host, not to the app, so the process name
            // is "ApplicationFrameHost" for every one of them. Printing that under "Windows
            // Security" tells the reader nothing and looks like the noise we just removed; the
            // title is the app's own and says enough on its own.
            if (FrameHosts.Contains(proc, StringComparer.OrdinalIgnoreCase)) proc = "";

            list.Add(new WindowInfo((long)hwnd, title, proc,
                NativeMethods.IsIconic(hwnd), DisplayOf(hwnd)));
            return true;
        }, IntPtr.Zero);

        return list;
    }


    /// <summary>
    /// Processes whose windows are never a place anyone means to go: the shell's own furniture.
    ///
    /// Deliberately short, and deliberately NOT holding ApplicationFrameHost or SystemSettings.
    /// ApplicationFrameHost owns the frame window of every Store app, so blocking it hides
    /// Settings, Mail, Photos and Windows Security along with the ghosts -- and the ghosts are
    /// already handled: a suspended Store app's window is cloaked, and the cloak test drops it
    /// while leaving a genuinely open one alone. The blocklist is the backstop, not the mechanism.
    /// </summary>
    /// <summary>Processes that host somebody else's window, so their name is not the app's name.</summary>
    private static readonly string[] FrameHosts = { "ApplicationFrameHost" };

    private static readonly string[] NeverSwitchable =
    {
        "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "LockApp", "PeopleExperienceHost",
        "Widgets", "WidgetBoard", "NVIDIA Overlay", "GameBar", "GameBarFTServer",
    };

    /// <summary>
    /// The rules Alt+Tab itself uses, which is the list a person expects to see.
    ///
    /// The one that actually mattered here is the cloak test. UWP keeps a window alive per
    /// suspended app, and IsWindowVisible answers true for those -- they are hidden by being
    /// cloaked by the compositor, not by being invisible in the old sense. Without asking DWM,
    /// every suspended Store app appears to be open.
    /// </summary>
    private static bool IsAltTabWindow(IntPtr hwnd)
    {
        var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;

        var style = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CHILD) != 0) return false;

        // Only the root of an owner chain is switchable: a dialog belongs to the window that
        // opened it, and listing both offers the same destination twice.
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER) != hwnd) return false;

        try
        {
            if (NativeMethods.DwmGetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return false;
        }
        catch { /* pre-DWM or a locked-down window: fall through to the other rules */ }

        // A window with no area is a message sink or a placeholder, never a destination -- but a
        // minimized one reports 160x28 wherever Windows parks it, and those are the very windows
        // a switcher exists to get back to. Two minimized File Explorer windows disappeared to
        // this rule before it asked.
        if (!NativeMethods.IsIconic(hwnd)
            && NativeMethods.GetWindowRect(hwnd, out var r)
            && (r.Right - r.Left < 32 || r.Bottom - r.Top < 32)) return false;

        return true;
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
    /// Grab what is on a display as a data: URI, so an overlay menu can show the desktop or the
    /// paused game dimmed behind it. The launcher's own window cannot be see-through — see the
    /// note in MainWindow — so the next best thing is a still of what was there a moment before
    /// the menu came up. Call it BEFORE showing the overlay, or it captures the overlay itself.
    ///
    /// Downscaled and JPEG-encoded on purpose: it sits behind a dark wash and a slight blur, so
    /// it gets no scrutiny, and a raw 4K frame would be megabytes of base64 over the bridge.
    /// Returns null if the capture fails, which a game in exclusive fullscreen can cause; the
    /// menu then just falls back to its solid background.
    /// </summary>
    public string? CaptureDisplay(string? deviceName)
    {
        var d = (deviceName is not null ? _displays.GetDisplay(deviceName) : null)
                ?? _displays.GetDisplays().FirstOrDefault(x => x.IsPrimary);
        if (d is null || d.Width <= 0 || d.Height <= 0) return null;

        // Straight GDI rather than System.Drawing, which is a separate package this app does not
        // otherwise need; WPF's own imaging stack does the scaling and the JPEG encoding.
        IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, prev = IntPtr.Zero;
        try
        {
            screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            mem = NativeMethods.CreateCompatibleDC(screen);
            bmp = NativeMethods.CreateCompatibleBitmap(screen, d.Width, d.Height);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero) return null;
            prev = NativeMethods.SelectObject(mem, bmp);

            if (!NativeMethods.BitBlt(mem, 0, 0, d.Width, d.Height, screen, d.X, d.Y,
                                      NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                Log.Info("Overlay capture: BitBlt failed");
                return null;
            }

            int w = Math.Min(d.Width, CaptureWidth);
            int h = (int)Math.Round(d.Height * (w / (double)d.Width));
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(w, h));

            var encoder = new JpegBitmapEncoder { QualityLevel = 62 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            Log.Info($"Overlay capture: {w}x{h}, {ms.Length / 1024} KB");
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Info($"Overlay capture failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (prev != IntPtr.Zero) NativeMethods.SelectObject(mem, prev);
            if (bmp != IntPtr.Zero) NativeMethods.DeleteObject(bmp);
            if (mem != IntPtr.Zero) NativeMethods.DeleteDC(mem);
            if (screen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int CaptureWidth = 1280;

    /// <summary>
    /// A picture of a window, as a data URI, or null. This is what makes the switcher usable from
    /// a sofa: a row of titles tells you nothing about which Chrome window is the right one.
    ///
    /// PrintWindow rather than a screen grab, because it asks the window to paint itself and so
    /// works for one that is behind another, or on a display the TV is not showing.
    /// PW_RENDERFULLCONTENT is the part that matters -- without it anything drawn through
    /// DirectComposition, which is every UWP app and every modern browser, comes back blank.
    ///
    /// Minimized windows have no surface to paint and are skipped rather than returning a black
    /// rectangle, which would read as a broken thumbnail rather than an absent one.
    /// </summary>
    public string? CaptureWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return null;

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 32 || h < 32 || w > 16384 || h > 16384) return null;

        IntPtr src = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, prev = IntPtr.Zero;
        try
        {
            src = NativeMethods.GetWindowDC(hwnd);
            if (src == IntPtr.Zero) return null;
            mem = NativeMethods.CreateCompatibleDC(src);
            bmp = NativeMethods.CreateCompatibleBitmap(src, w, h);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero) return null;
            prev = NativeMethods.SelectObject(mem, bmp);

            if (!NativeMethods.PrintWindow(hwnd, mem, NativeMethods.PW_RENDERFULLCONTENT))
                return null;

            // Scaled down here rather than in CSS: these go to the UI as base64 inside a JSON
            // message, and a full-size 4K window would be several megabytes of string per row.
            int tw = Math.Min(w, ThumbWidth);
            int th = Math.Max(1, (int)Math.Round(h * (tw / (double)w)));
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(tw, th));

            var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Info($"Window capture failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (prev != IntPtr.Zero) NativeMethods.SelectObject(mem, prev);
            if (bmp != IntPtr.Zero) NativeMethods.DeleteObject(bmp);
            if (mem != IntPtr.Zero) NativeMethods.DeleteDC(mem);
            if (src != IntPtr.Zero) NativeMethods.ReleaseDC(hwnd, src);
        }
    }

    /// <summary>Wide enough to read at the size the switcher draws them, small enough that a
    /// dozen of them are not a megabyte of JSON.</summary>
    private const int ThumbWidth = 480;


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
            new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, u = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dx = 1, dwFlags = NativeMethods.MOUSEEVENTF_MOVE } } },
            new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, u = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dx = -1, dwFlags = NativeMethods.MOUSEEVENTF_MOVE } } },
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        Log.Info("Suspend: displays on");
    }

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
