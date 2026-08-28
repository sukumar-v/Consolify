using CouchLauncher.Interop;

namespace CouchLauncher.Services;

/// <summary>
/// Hides the mouse pointer while the user is navigating with the D-pad.
///
/// Inside the launcher the UI just applies `cursor: none`, which is free and safe. Hiding the
/// pointer for the REST of Windows requires replacing the system cursors, which is global state:
/// if this process dies without restoring them the user is left with invisible cursors until they
/// sign out. So it is opt-in (Settings -> "Hide pointer system-wide"), and Restore() is wired to
/// every exit path we can reach, including ProcessExit and unhandled exceptions.
/// </summary>
public class CursorService : IDisposable
{
    private readonly SettingsStore _settings;
    private bool _systemCursorsHidden;
    private readonly object _gate = new();

    public CursorService(SettingsStore settings)
    {
        _settings = settings;
        // Last-ditch restores — a blank system cursor must never outlive the process.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
    }

    /// <summary>True while the D-pad is driving navigation (pointer should be out of the way).</summary>
    public void SetPadMode(bool padMode)
    {
        if (padMode && _settings.Settings.HideCursorSystemWide) Hide();
        else Restore();
    }

    private void Hide()
    {
        lock (_gate)
        {
            if (_systemCursorsHidden) return;

            // A 32x32 cursor that is fully transparent: AND mask all 1s, XOR mask all 0s.
            var andMask = new byte[32 * 4];
            var xorMask = new byte[32 * 4];
            Array.Fill(andMask, (byte)0xFF);

            bool any = false;
            foreach (var id in NativeMethods.SystemCursorIds)
            {
                // SetSystemCursor takes ownership of the handle, so create one per call.
                var cursor = NativeMethods.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
                if (cursor == IntPtr.Zero) continue;
                if (NativeMethods.SetSystemCursor(cursor, id)) any = true;
                else NativeMethods.DestroyCursor(cursor);
            }

            if (any)
            {
                _systemCursorsHidden = true;
                Log.Info("System cursors hidden (pad mode)");
            }
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (!_systemCursorsHidden) return;
            _systemCursorsHidden = false;
            // Reloads every cursor from the user's current scheme.
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            Log.Info("System cursors restored");
        }
    }

    public void Dispose() => Restore();
}
