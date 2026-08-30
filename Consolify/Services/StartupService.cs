using System.Diagnostics;
using Microsoft.Win32;

namespace Consolify.Services;

/// <summary>Registers/unregisters Consolify to auto-start on user login (HKCU Run key).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Consolify";
    private const string OldValueName = "CouchLauncher";

    /// <summary>
    /// Carry a pre-rename startup entry over. The old value points at CouchLauncher.exe, which no
    /// longer exists, so leaving it behind means a broken entry firing at every login.
    /// </summary>
    public static void MigrateOldEntry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(OldValueName) is null) return;
            k.DeleteValue(OldValueName, throwOnMissingValue: false);
            SetRegistered(true);
            Log.Info("Migrated the startup entry from CouchLauncher to Consolify");
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
