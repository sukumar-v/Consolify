using System.Diagnostics;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>Registers/unregisters Loungepad to auto-start on user login (HKCU Run key).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Loungepad";
    /// <summary>The app's earlier names, oldest first.</summary>
    private static readonly string[] OldValueNames = { "CouchLauncher", ConsolifyMigration.OldName };

    /// <summary>
    /// Carry a pre-rename startup entry over. The old value points at the old exe, which an
    /// upgrade leaves behind or deletes, so keeping it means the old app -- or a broken entry --
    /// firing at every login instead of this one.
    /// </summary>
    public static void MigrateOldEntry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k is null) return;
            foreach (var old in OldValueNames)
            {
                if (k.GetValue(old) is null) continue;
                k.DeleteValue(old, throwOnMissingValue: false);
                SetRegistered(true);
                Log.Info($"Migrated the startup entry from {old} to Loungepad");
            }
        }
        catch (Exception ex) { Log.Info($"Startup entry migration failed: {ex.Message}"); }
    }

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is not null;
    }

    public static void SetRegistered(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null) k.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            k.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
