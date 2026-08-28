using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CouchLauncher.Interop;

namespace CouchLauncher.Services;

/// <summary>
/// Shows/hides the Windows touch keyboard (TabTip.exe). The touch keyboard is preferred because
/// it has native gamepad support; osk.exe remains a last-resort fallback only.
/// </summary>
public class VirtualKeyboardService
{
    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    private static readonly Guid TipInvocationClsid = new("4CE576FA-83DC-4F88-951C-9D0782B4E376");

    private readonly SettingsStore _settings;

    public VirtualKeyboardService(SettingsStore settings) => _settings = settings;

    public static bool IsVisible()
    {
        var wnd = NativeMethods.FindWindow("IPTip_Main_Window", null);
        return wnd != IntPtr.Zero && NativeMethods.IsWindowVisible(wnd);
    }

    public void Toggle()
    {
        try
        {
            if (IsVisible()) Hide();
            else Show();
        }
        catch (Exception ex) { Log.Info($"Virtual keyboard toggle failed: {ex.Message}"); }
    }

    public void Show()
    {
        try
        {
            if (IsVisible()) return;

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                "microsoft shared", "ink", "TabTip.exe");

            if (File.Exists(path))
            {
                if (Process.GetProcessesByName("TabTip").Length == 0)
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    Thread.Sleep(350); // give the frame process a moment before COM invocation
                }
                if (!IsVisible()) ComToggle();
                return;
            }

            // No TabTip on this machine — classic on-screen keyboard as last resort
            if (Process.GetProcessesByName("osk").Length == 0)
                Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Info($"Virtual keyboard show failed: {ex.Message}"); }
    }

    public void Hide()
    {
        try
        {
            var wnd = NativeMethods.FindWindow("IPTip_Main_Window", null);
            if (wnd != IntPtr.Zero && NativeMethods.IsWindowVisible(wnd))
            {
                // ITipInvocation.Toggle hides when visible; SC_CLOSE is the fallback
                if (!ComToggle())
                    NativeMethods.PostMessage(wnd, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_CLOSE), IntPtr.Zero);
            }

            foreach (var p in Process.GetProcessesByName("osk")) { try { p.Kill(); } catch { } }
        }
        catch (Exception ex) { Log.Info($"Virtual keyboard hide failed: {ex.Message}"); }
    }

    private static bool ComToggle()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(TipInvocationClsid);
            if (type is null) return false;
            var instance = Activator.CreateInstance(type);
            if (instance is not ITipInvocation tip) return false;
            tip.Toggle(NativeMethods.GetDesktopWindow());
            Marshal.ReleaseComObject(instance);
            return true;
        }
        catch { return false; }
    }
}
