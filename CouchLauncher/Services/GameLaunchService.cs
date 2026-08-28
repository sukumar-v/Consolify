using System.Diagnostics;
using System.IO;
using System.Text;
using CouchLauncher.Interop;
using CouchLauncher.Models;

namespace CouchLauncher.Services;

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

                    if (s.RepositionGameWindow && s.TvDeviceName is not null)
                    {
                        var current = tracked;
                        _ = Task.Run(() => RepositionOntoTv(current, s.TvDeviceName));
                    }

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

        if (!direct && game.LaunchUri is not null && game.Platform is "Steam" or "Epic")
        {
            Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
            return null; // real process found later via install dir
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

    /// <summary>For ~30s after launch, move any visible top-level window of the game onto the TV.</summary>
    private void RepositionOntoTv(Process game, string tvDeviceName)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        bool moved = false;

        while (!moved && DateTime.UtcNow < deadline && !game.HasExited)
        {
            var tv = _displays.GetDisplay(tvDeviceName);
            if (tv is null) return;

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != game.Id) return true;
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
                if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
                if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
                if (r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true; // splash/tooltip windows

                int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
                bool onTv = cx >= tv.X && cx < tv.X + tv.Width && cy >= tv.Y && cy < tv.Y + tv.Height;
                if (!onTv)
                {
                    int w = Math.Min(r.Right - r.Left, tv.Width);
                    int h = Math.Min(r.Bottom - r.Top, tv.Height);
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, tv.X, tv.Y, w, h,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
                    Log.Info($"Moved game window {hwnd} onto {tvDeviceName}");
                }
                moved = true;
                return false;
            }, IntPtr.Zero);

            if (!moved) Thread.Sleep(1000);
        }
    }
}
