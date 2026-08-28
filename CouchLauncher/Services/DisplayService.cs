using CouchLauncher.Interop;

namespace CouchLauncher.Services;

public record DisplayInfo(
    string DeviceName,      // \\.\DISPLAY1
    string FriendlyName,    // monitor model string
    int X, int Y, int Width, int Height,
    bool IsPrimary);

/// <summary>
/// Enumerates monitors and switches the Windows primary display via ChangeDisplaySettingsEx.
/// Most games open on the primary display, so before a launch we make the TV primary and
/// restore the original arrangement when the game exits.
/// </summary>
public class DisplayService
{
    private string? _savedPrimaryDevice;

    public List<DisplayInfo> GetDisplays()
    {
        var result = new List<DisplayInfo>();
        var adapter = new NativeMethods.DISPLAY_DEVICE { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };

        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            if ((adapter.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((adapter.StateFlags & NativeMethods.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;

            var dm = NewDevMode();
            if (!NativeMethods.EnumDisplaySettings(adapter.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm)) continue;

            // Monitor name lives one level down (adapter -> monitor)
            var monitor = new NativeMethods.DISPLAY_DEVICE { cb = adapter.cb };
            string friendly = adapter.DeviceString;
            if (NativeMethods.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString))
                friendly = monitor.DeviceString;

            result.Add(new DisplayInfo(
                adapter.DeviceName, friendly,
                dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight,
                (adapter.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
        }
        return result;
    }

    public DisplayInfo? GetDisplay(string deviceName) =>
        GetDisplays().FirstOrDefault(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

    public string? CurrentPrimaryDevice() => GetDisplays().FirstOrDefault(d => d.IsPrimary)?.DeviceName;

    /// <summary>Make the given display primary. Remembers the previous primary for RestorePrimary().</summary>
    public bool SetPrimary(string deviceName)
    {
        var displays = GetDisplays();
        var target = displays.FirstOrDefault(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        if (target is null) { Log.Info($"SetPrimary: display {deviceName} not found"); return false; }
        if (target.IsPrimary) return true;

        _savedPrimaryDevice ??= displays.FirstOrDefault(d => d.IsPrimary)?.DeviceName;

        int dx = target.X, dy = target.Y;
        foreach (var d in displays)
        {
            var dm = NewDevMode();
            if (!NativeMethods.EnumDisplaySettings(d.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm)) continue;
            dm.dmPositionX -= dx;
            dm.dmPositionY -= dy;
            dm.dmFields |= NativeMethods.DM_POSITION;

            uint flags = NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET;
            if (d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                flags |= NativeMethods.CDS_SET_PRIMARY;

            NativeMethods.ChangeDisplaySettingsEx(d.DeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
        }

        int rc = NativeMethods.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Info($"SetPrimary({deviceName}) -> {rc}");
        return rc == 0; // DISP_CHANGE_SUCCESSFUL
    }

    /// <summary>Restore the primary display saved by the last SetPrimary call.</summary>
    public void RestorePrimary()
    {
        if (_savedPrimaryDevice is null) return;
        var saved = _savedPrimaryDevice;
        _savedPrimaryDevice = null;
        SetPrimary(saved);
    }

    private static NativeMethods.DEVMODE NewDevMode() => new()
    {
        dmDeviceName = new string(' ', 32),
        dmFormName = new string(' ', 32),
        dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEVMODE>()
    };
}
