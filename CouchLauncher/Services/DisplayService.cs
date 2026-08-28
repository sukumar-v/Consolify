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

        // Preferred path: the modern CCD API. ChangeDisplaySettingsEx below still exists as a
        // fallback, but on current Windows 11 it answers CDS_SET_PRIMARY with
        // DISP_CHANGE_RESTART and applies nothing, so it can no longer be relied on.
        int ccd = Ccd.SetPrimary(deviceName);
        if (ccd == 0)
        {
            Log.Info($"SetPrimary({deviceName}) via CCD -> ok");
            return true;
        }
        Log.Info($"SetPrimary({deviceName}) via CCD failed ({ccd}); falling back to ChangeDisplaySettingsEx");

        int dx = target.X, dy = target.Y;
        bool allOk = true;
        foreach (var d in displays)
        {
            var dm = NewDevMode();
            if (!NativeMethods.EnumDisplaySettings(d.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm))
            {
                Log.Info($"SetPrimary: EnumDisplaySettings failed for {d.DeviceName}");
                allOk = false;
                continue;
            }
            dm.dmPositionX = d.X - dx;
            dm.dmPositionY = d.Y - dy;
            // Position ONLY. Carrying over the mode bits EnumDisplaySettings returns (resolution,
            // bit depth, refresh, fixed output) asks the driver for a full mode set.
            dm.dmFields = NativeMethods.DM_POSITION;

            uint flags = NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET;
            if (d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                flags |= NativeMethods.CDS_SET_PRIMARY;

            int one = NativeMethods.ChangeDisplaySettingsEx(d.DeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (one != 0)
            {
                Log.Info($"SetPrimary: {d.DeviceName} -> {DispChangeName(one)}");
                allOk = false;
            }
        }

        int rc = NativeMethods.ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        Log.Info($"SetPrimary({deviceName}) legacy apply -> {DispChangeName(rc)}, perDisplayOk={allOk}");

        // Trust what the OS actually reports, not the return code.
        return GetDisplay(deviceName)?.IsPrimary == true;
    }

    private static string DispChangeName(int code) => code switch
    {
        0 => "SUCCESSFUL",
        -1 => "RESTART",
        -2 => "FAILED",
        -3 => "BADMODE",
        -4 => "NOTUPDATED",
        -5 => "BADFLAGS",
        -6 => "BADPARAM",
        1 => "BADDUALVIEW",
        _ => $"UNKNOWN({code})"
    };


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
