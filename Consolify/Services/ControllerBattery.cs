using Windows.Gaming.Input;
using Windows.System.Power;

namespace Consolify.Services;

/// <summary>How much charge the pad has left, and where the number came from.</summary>
/// <param name="Present">A controller is attached at all.</param>
/// <param name="Percent">0-100, or null when only a coarse level is available.</param>
/// <param name="Charging">On the cable or a charging dock.</param>
/// <param name="CoarseLevel">XInput's 0 (empty) .. 3 (full), used when Percent is null.</param>
public readonly record struct BatteryState(bool Present, int? Percent, bool Charging, int CoarseLevel);

/// <summary>
/// Reads the controller's charge.
///
/// Three sources, none of them good on its own:
///
/// - XInput reports four coarse levels, and over Bluetooth calls the battery DISCONNECTED even
///   while the pad is working.
/// - WinRT's IGameControllerBatteryInfo looks better -- it hands back milliwatt-hours -- but for an
///   Xbox pad those are the same coarse level in disguise, and the same wrong one: 100 of 1000
///   (10%) for a pad Windows itself was showing at 97%. It is still the only one that knows about
///   the cable, so it is kept for the charging flag.
/// - The Bluetooth device node carries the real percentage, the one on the Settings page.
///
/// So: charging from WinRT, the number from Bluetooth, and the coarse levels only as a last resort.
/// </summary>
public static class ControllerBattery
{
    private static bool _subscribed;

    /// <summary>
    /// WinRT populates its gamepad list lazily. Touching the events once gets the subsystem
    /// running so the list is filled by the time the first poll asks for it.
    /// </summary>
    public static void Prime()
    {
        if (_subscribed) return;
        _subscribed = true;
        try
        {
            Gamepad.GamepadAdded += (_, _) => { };
            Gamepad.GamepadRemoved += (_, _) => { };
            _ = Gamepad.Gamepads.Count;
        }
        catch (Exception ex) { Log.Info($"Battery: WinRT prime failed: {ex.Message}"); }
    }

    /// <summary>Percentage and charging state from WinRT, or null if it cannot say.</summary>
    private static (int Percent, bool Charging)? ReadWinRt()
    {
        try
        {
            foreach (var pad in Gamepad.Gamepads)
            {
                if (pad is not IGameControllerBatteryInfo info) continue;
                var report = info.TryGetBatteryReport();
                if (report is null) continue;

                bool charging = report.Status == BatteryStatus.Charging;
                if (report.RemainingCapacityInMilliwattHours is not int remaining ||
                    report.FullChargeCapacityInMilliwattHours is not int full || full <= 0)
                {
                    // A pad on the cable often reports a status but no capacity at all.
                    if (charging) return (100, true);
                    continue;
                }
                return ((int)Math.Clamp(Math.Round(remaining * 100.0 / full), 0, 100), charging);
            }
        }
        catch (Exception ex) { Log.Info($"Battery: WinRT read failed: {ex.Message}"); }
        return null;
    }

    public static BatteryState Read(int userIndex, bool connected)
    {
        if (!connected) return new BatteryState(false, null, false, 0);

        var winrt = ReadWinRt();

        // A pad on the cable reports no percentage anywhere; take WinRT's word that it is charging.
        if (winrt is { Charging: true } charging)
            return new BatteryState(true, charging.Percent, true, PercentToCoarse(charging.Percent));

        // Bluetooth first: it is the only source that gives the pad's real charge. WinRT and XInput
        // both quantise an Xbox pad on Bluetooth to a coarse level, and get that level wrong.
        if (Interop.NativeMethods.TryGetBluetoothBatteryPercent(out int bt))
            return new BatteryState(true, bt, false, PercentToCoarse(bt));

        if (winrt is { } w)
            return new BatteryState(true, w.Percent, w.Charging, PercentToCoarse(w.Percent));

        // XInput fallback: four levels, and a wired pad has no battery to report.
        if (Interop.NativeMethods.TryGetBattery(userIndex, out var info))
        {
            if (info.BatteryType == Interop.NativeMethods.BATTERY_TYPE_WIRED)
                return new BatteryState(true, null, true, 3);
            if (info.BatteryType is Interop.NativeMethods.BATTERY_TYPE_ALKALINE
                                 or Interop.NativeMethods.BATTERY_TYPE_NIMH)
                return new BatteryState(true, null, false, info.BatteryLevel);
        }

        // Connected, but nothing will tell us the charge.
        return new BatteryState(true, null, false, -1);
    }

    private static int PercentToCoarse(int percent) =>
        percent >= 70 ? 3 : percent >= 40 ? 2 : percent >= 15 ? 1 : 0;
}
