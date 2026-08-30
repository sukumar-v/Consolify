using System.Runtime.InteropServices;

namespace Consolify.Interop;


/// <summary>
/// Modern Connecting-and-Configuring-Displays API. Windows defines the primary display as the
/// source sitting at desktop position (0,0), so "make X primary" means "offset every source so
/// that X lands on 0,0".
/// </summary>
internal static class Ccd
{
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const uint SDC_APPLY = 0x80;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    public const uint SDC_SAVE_TO_DATABASE = 0x200;
    public const uint SDC_ALLOW_CHANGES = 0x400;
    public const uint MODE_INFO_TYPE_SOURCE = 1;
    public const uint PATH_MODE_IDX_INVALID = 0xffffffff;
    public const uint DEVICE_INFO_GET_SOURCE_NAME = 1;

    [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] public struct RATIONAL { public uint Num, Den; }
    [StructLayout(LayoutKind.Sequential)] public struct POINTL { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECTL { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct REGION { public uint cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET
    {
        public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE sourceInfo; public PATH_TARGET targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_SIGNAL
    {
        public ulong pixelRate; public RATIONAL hSync, vSync; public REGION activeSize, totalSize;
        public uint videoStandard, scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)] public struct TARGET_MODE { public VIDEO_SIGNAL signal; }
    [StructLayout(LayoutKind.Sequential)] public struct SOURCE_MODE { public uint width, height, pixelFormat; public POINTL position; }
    [StructLayout(LayoutKind.Sequential)] public struct DESKTOP_IMAGE { public POINTL size; public RECTL region, clip; }

    [StructLayout(LayoutKind.Explicit)]
    public struct MODE_UNION
    {
        [FieldOffset(0)] public TARGET_MODE targetMode;
        [FieldOffset(0)] public SOURCE_MODE sourceMode;
        [FieldOffset(0)] public DESKTOP_IMAGE desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MODE_INFO { public uint infoType, id; public LUID adapterId; public MODE_UNION mode; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] public static extern int SetDisplayConfig(uint numPaths, [In] PATH_INFO[] paths, uint numModes, [In] MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME req);

    public static string GdiName(LUID adapterId, uint sourceId)
    {
        var req = new SOURCE_DEVICE_NAME
        {
            header = new DEVICE_INFO_HEADER
            {
                type = DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId
            }
        };
        return DisplayConfigGetDeviceInfo(ref req) == 0 ? req.viewGdiDeviceName : "";
    }

    /// <summary>Make the given GDI display (\.\DISPLAYn) primary. Returns the Win32 result, 0 = ok.</summary>
    public static int SetPrimary(string gdiDeviceName)
    {
        int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint nPaths, out uint nModes);
        if (rc != 0) return rc;
        var paths = new PATH_INFO[nPaths];
        var modes = new MODE_INFO[nModes];
        rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPaths, paths, ref nModes, modes, IntPtr.Zero);
        if (rc != 0) return rc;

        // Locate the source mode belonging to the requested display.
        int dx = 0, dy = 0; bool found = false;
        for (int i = 0; i < nPaths && !found; i++)
        {
            var src = paths[i].sourceInfo;
            if (src.modeInfoIdx == PATH_MODE_IDX_INVALID || src.modeInfoIdx >= nModes) continue;
            if (!string.Equals(GdiName(src.adapterId, src.id), gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var m = modes[src.modeInfoIdx];
            if (m.infoType != MODE_INFO_TYPE_SOURCE) continue;
            dx = m.mode.sourceMode.position.x;
            dy = m.mode.sourceMode.position.y;
            found = true;
        }
        if (!found) return -1000;
        if (dx == 0 && dy == 0) return 0;   // already primary

        // Rebase every source so the target lands on (0,0).
        for (uint i = 0; i < nModes; i++)
        {
            if (modes[i].infoType != MODE_INFO_TYPE_SOURCE) continue;
            modes[i].mode.sourceMode.position.x -= dx;
            modes[i].mode.sourceMode.position.y -= dy;
        }

        return SetDisplayConfig(nPaths, paths, nModes, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
    }
}
