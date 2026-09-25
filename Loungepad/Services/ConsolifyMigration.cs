using System.Diagnostics;
using System.IO;
using System.Text;
using Loungepad.Interop;

namespace Loungepad.Services;

/// <summary>
/// Carries an install over from the app's previous name. Loungepad was Consolify up to 1.4, and
/// everything that identifies an install was keyed on that name: the data folder, the WebView2
/// profiles, the startup entry, the single-instance mutex and the Vortex extension's folder.
/// Renaming without this would have started every existing user on an empty library, signed out
/// of every store, with the old copy still answering the pad beside the new one.
///
/// Everything here is best effort and runs before the log is open, so it reports what it did as
/// lines for the caller to log rather than logging them itself.
/// </summary>
public static class ConsolifyMigration
{
    public const string OldName = "Consolify";
    public const string OldMutexName = "Consolify_SingleInstance";
    public const string OldWakeEventName = "Consolify_ShowExisting";
    public const string OldLogName = "consolify.log";
    public const string OldPluginFolderName = "consolify-bridge";
    private const string CopiedMarker = ".migrated-from-consolify";

    /// <summary>
    /// Ask a running Consolify to exit, and wait for it. The usual upgrade is to unzip the new
    /// build and double-click it while the old one sits on the TV; under a different mutex name
    /// both would run, both would read the pad, and every press would land twice. It also has to
    /// be gone before its folder is moved, or its last save lands in a folder nobody reads.
    ///
    /// WM_CLOSE to its main window, which is what Alt+F4 sends: that runs its normal exit, which
    /// is what puts the system cursors back if it had hidden them. It is never killed.
    /// </summary>
    public static string? CloseRunningCopy()
    {
        // The mutex, not the process name, says it is really ours: anything can be called
        // Consolify.exe.
        try
        {
            if (!Mutex.TryOpenExisting(OldMutexName, out var mutex)) return null;
            mutex.Dispose();
        }
        catch { return null; }

        var procs = Process.GetProcessesByName(OldName);
        try
        {
            var pids = procs.Select(p => (uint)p.Id).ToHashSet();
            var asked = 0;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains(pid)) return true;
                var sb = new StringBuilder(64);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                // The main window is titled exactly this; the keyboard is "Consolify Keyboard".
                if (sb.ToString() != OldName) return true;
                NativeMethods.PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                asked++;
                return true;
            }, IntPtr.Zero);
            if (asked == 0) return "Consolify is running but its window was not found; it was left alone";

            var deadline = DateTime.UtcNow.AddSeconds(8);
            foreach (var p in procs)
            {
                var left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try { p.WaitForExit(left); } catch { /* already gone */ }
            }
            return procs.All(HasExited)
                ? "Closed the running copy of Consolify"
                : "Asked the running copy of Consolify to close, but it is still running";
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    /// <summary>
    /// Move <paramref name="oldDir"/> to <paramref name="newDir"/>, or copy what is missing when
    /// it cannot be moved.
    ///
    /// A move first: on one volume it is a single rename, so it either happens completely or not
    /// at all, it is instant however many hundreds of megabytes of art there are, and it leaves no
    /// second copy behind. The Couch Launcher migration copied instead, because an old build left
    /// running would go on writing to the old folder; <see cref="CloseRunningCopy"/> now runs
    /// first, so that is no longer the risk it was.
    ///
    /// The copy is only the fallback, for a move that is refused (something still holds a file)
    /// or a new folder that already exists. It skips files that are already there and writes a
    /// marker once nothing failed, so a later start neither repeats it nor overwrites anything
    /// made since. With <paramref name="copyFallback"/> off a refused move is simply left for
    /// the next start.
    /// </summary>
    public static string? MoveFolder(string oldDir, string newDir, bool copyFallback)
    {
        if (!Directory.Exists(oldDir)) return null;

        string? refused = null;
        if (!Directory.Exists(newDir))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
                Directory.Move(oldDir, newDir);
                return $"Moved {oldDir} to {newDir}";
            }
            catch (Exception ex) { refused = ex.Message; }
        }
        if (!copyFallback)
            return refused is null ? null : $"Could not move {oldDir}: {refused}; left for the next start";

        var marker = Path.Combine(newDir, CopiedMarker);
        if (File.Exists(marker)) return null;

        int copied = 0, failed = 0;
        foreach (var src in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(newDir, Path.GetRelativePath(oldDir, src));
            if (File.Exists(dst)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst);
                copied++;
            }
            catch { failed++; }
        }
        if (failed == 0)
        {
            try { File.WriteAllText(marker, DateTime.Now.ToString("O")); } catch { failed++; }
        }
        return $"Copied {copied} file(s) from {oldDir} to {newDir}" +
               (refused is null ? "" : $" (the move was refused: {refused})") +
               (failed == 0 ? "; the old folder was left in place" : $"; {failed} could not be copied and will be retried next start");
    }

    /// <summary>The log came along with the folder under its old name. Keep its history under the
    /// new one rather than starting a second file beside it.</summary>
    public static void RenameLog(string dataDir, string logFile)
    {
        try
        {
            var old = Path.Combine(dataDir, OldLogName);
            if (!File.Exists(old) || File.Exists(logFile)) return;
            File.Move(old, logFile);
            if (File.Exists(old + ".old") && !File.Exists(logFile + ".old")) File.Move(old + ".old", logFile + ".old");
        }
        catch { /* the old log keeps its name; nothing depends on it */ }
    }
}
