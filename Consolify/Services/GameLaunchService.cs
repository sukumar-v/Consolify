using System.Diagnostics;
using System.IO;
using System.Text;
using Consolify.Interop;
using Consolify.Models;

namespace Consolify.Services;

/// <summary>
/// Orchestrates a game session:
///  1. optionally switch the TV to be the Windows primary display,
///  2. start the game (direct exe, or steam:// / Epic URI),
///  3. find the actual game process (URI launches go through the store client),
///  4. nudge the game window onto the TV if it opened elsewhere,
///  5. wait for exit, restore the primary display, record playtime, hand focus back.
/// </summary>
public class GameLaunchService
{
    private readonly DisplayService _displays;
    private readonly SettingsStore _settings;
    private readonly LibraryStore _library;

    public bool GameRunning { get; private set; }
    public string? RunningGameId { get; private set; }

    private string? _runningInstallDir;
    private readonly Dictionary<uint, bool> _pidCache = new();
    private readonly object _pidGate = new();

    public event Action<Game>? GameStarted;
    public event Action<Game>? GameExited;

    public GameLaunchService(DisplayService displays, SettingsStore settings, LibraryStore library)
    {
        _displays = displays;
        _settings = settings;
        _library = library;
    }

    public void Launch(Game game)
    {
        if (GameRunning) return;
        GameRunning = true;
        RunningGameId = game.Id;
        _ = Task.Run(() => RunSession(game));
    }

    private async Task RunSession(Game game)
    {
        var s = _settings.Settings;
        bool switchedPrimary = false;
        var started = DateTime.Now;
        var monitorCts = new CancellationTokenSource();

        _runningInstallDir = game.InstallDir;
        lock (_pidGate) _pidCache.Clear();

        try
        {
            if (s.SwitchPrimaryOnLaunch && s.TvDeviceName is not null)
            {
                var primary = _displays.CurrentPrimaryDevice();
                if (!string.Equals(primary, s.TvDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    switchedPrimary = _displays.SetPrimary(s.TvDeviceName);
                    if (switchedPrimary) await Task.Delay(1200); // let the desktop settle before the game probes displays
                }
            }

            Process? tracked = StartGame(game);
            GameStarted?.Invoke(game);

            // One monitor for the whole session, covering every process the game spawns.
            if (s.RepositionGameWindow && s.TvDeviceName is not null)
                _ = Task.Run(() => MonitorGameWindows(s.TvDeviceName, monitorCts.Token));


            // URI launches (Steam/Epic) return the store client, not the game — find the real process.
            if (tracked is null && game.InstallDir is not null)
                tracked = await WaitForProcessFromDir(game.InstallDir, TimeSpan.FromSeconds(120));

            if (tracked is not null)
            {
                // Many games chain through their own pre-launcher (e.g. REDprelauncher ->
                // REDlauncher -> Cyberpunk2077.exe). Keep the session alive as long as ANY
                // process from the install dir is running, re-attaching to each successor.
                while (tracked is not null)
                {
                    Log.Info($"Tracking game process {tracked.ProcessName} (pid {tracked.Id}) for {game.Title}");

                    await tracked.WaitForExitAsync();
                    tracked = game.InstallDir is not null
                        ? await WaitForProcessFromDir(game.InstallDir, TimeSpan.FromSeconds(15))
                        : null;
                }
            }
            else
            {
                Log.Info($"Could not find a process for {game.Title}; assuming it exited after grace period");
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Launch session for {game.Title} failed: {ex}");
        }
        finally
        {
            monitorCts.Cancel();
            monitorCts.Dispose();
            _runningInstallDir = null;
            lock (_pidGate) _pidCache.Clear();

            if (switchedPrimary) _displays.RestorePrimary();

            var minutes = (DateTime.Now - started).TotalMinutes;
            var g = _library.Find(game.Id);
            if (g is not null)
            {
                if (minutes > 0.5) { g.PlaytimeMinutes += minutes; g.Sessions++; }
                g.LastPlayed = DateTime.Now;
                _library.Save();
            }

            GameRunning = false;
            RunningGameId = null;
            GameExited?.Invoke(game);
        }
    }

    private static Process? StartGame(Game game)
    {
        // "Prefer direct launch" (user picked an exe in Manage) bypasses the store client.
        bool direct = game.ExePath is not null && File.Exists(game.ExePath)
                      && (game.Platform is "GOG" or "Manual" || game.PreferDirectLaunch);

        if (!direct && game.LaunchUri is not null)
        {
            if (game.LaunchUri.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            {
                // Packaged (Xbox / Microsoft Store) apps are activated through the shell.
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{game.LaunchUri}\"") { UseShellExecute = true });
                return null;
            }
            if (game.Platform is "Steam" or "Epic")
            {
                Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
                return null; // real process found later via install dir
            }
        }

        if (game.ExePath is null || !File.Exists(game.ExePath))
            throw new FileNotFoundException($"Executable not found: {game.ExePath}");

        var psi = new ProcessStartInfo(game.ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = game.InstallDir ?? Path.GetDirectoryName(game.ExePath) ?? ""
        };
        if (!string.IsNullOrWhiteSpace(game.Args)) psi.Arguments = game.Args;
        return Process.Start(psi);
    }

    /// <summary>Poll running processes until one's image path is inside the game's install dir.</summary>
    private static async Task<Process?> WaitForProcessFromDir(string installDir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var prefix = installDir.TrimEnd('\\') + "\\";

        while (DateTime.UtcNow < deadline)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = GetProcessPath(p.Id);
                    if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
                catch { /* process may have exited */ }
            }
            await Task.Delay(1500);
        }
        return null;
    }

    private static string? GetProcessPath(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString(0, (int)len) : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>
    /// True when the foreground window belongs to the running game. Used to gate the gamepad
    /// mouse: it must keep working when the game is merely running in the background.
    /// </summary>
    public bool IsGameForeground()
    {
        if (!GameRunning) return false;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return PidBelongsToGame(pid);
    }

    /// <summary>Does this window belong to the running game? Used by the in-game menu.</summary>
    public bool OwnsWindow(IntPtr hwnd)
    {
        if (!GameRunning || hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return PidBelongsToGame(pid);
    }

    /// <summary>
    /// Ask every process in the running game's install directory to quit. This backs up the
    /// window-by-window close: a game in exclusive fullscreen (or one whose only window is
    /// owned by a child process) may not appear in the alt-tab enumeration at all, in which case
    /// closing "every window the game owns" closes nothing and the menu looks like it did nothing.
    /// Returns how many processes were asked.
    /// </summary>
    public int RequestClose()
    {
        if (!GameRunning) return 0;
        int n = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (PidBelongsToGame((uint)p.Id) && p.CloseMainWindow()) n++;
            }
            catch { /* protected, or exited between the enumeration and the call */ }
            finally { p.Dispose(); }
        }
        Log.Info($"Close game: asked {n} process(es) to quit");
        return n;
    }

    /// <summary>
    /// Wait for the current session to finish, up to a timeout. False means it is still running,
    /// which happens when a game ignores the close request or puts up its own "really quit?".
    /// </summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (GameRunning && DateTime.UtcNow < until) await Task.Delay(200);
        return !GameRunning;
    }

    /// <summary>Is this pid one of the game's own processes (launcher, chained exe, game)?</summary>
    private bool PidBelongsToGame(uint pid)
    {
        var dir = _runningInstallDir;
        if (dir is null || pid == 0) return false;
        lock (_pidGate)
        {
            if (_pidCache.TryGetValue(pid, out var known)) return known;
            var path = GetProcessPath((int)pid);
            var prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            bool belongs = path is not null
                && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            _pidCache[pid] = belongs;
            return belongs;
        }
    }

    /// <summary>
    /// Keeps the game on the TV for the whole session, not just the first window it opens.
    /// Games routinely put a launcher or config dialog on another display, and once the user
    /// clicks it the game itself then opens there, so a one-shot nudge at startup is not enough.
    /// Only windows whose centre has drifted off the TV are touched, so a game already sitting
    /// correctly (including exclusive fullscreen) is left alone.
    /// </summary>
    private async Task MonitorGameWindows(string tvDeviceName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tv = _displays.GetDisplay(tvDeviceName);
                if (tv is not null) EnforceOnTv(tv);
            }
            catch (Exception ex) { Log.Info($"Window monitor: {ex.Message}"); }

            try { await Task.Delay(1500, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private void EnforceOnTv(DisplayInfo tv)
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!PidBelongsToGame(pid)) return true;

            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;

            int w0 = r.Right - r.Left, h0 = r.Bottom - r.Top;
            if (w0 < 200 || h0 < 150) return true;                  // splash / tooltip
            if (r.Left <= -30000 || r.Top <= -30000) return true;   // minimized

            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            bool onTv = cx >= tv.X && cx < tv.X + tv.Width && cy >= tv.Y && cy < tv.Y + tv.Height;
            if (onTv) return true;

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, tv.X, tv.Y,
                Math.Min(w0, tv.Width), Math.Min(h0, tv.Height),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
            Log.Info($"Moved game window {hwnd} (pid {pid}) onto {tv.DeviceName}");
            return true;   // keep scanning: a game can own more than one stray window
        }, IntPtr.Zero);
    }
}
