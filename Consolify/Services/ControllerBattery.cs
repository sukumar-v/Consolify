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
/// XInput only reports four coarse levels and, over Bluetooth, frequently reports the battery as
/// DISCONNECTED even while the pad is working — which is why the readout used to be blank on a
/// controller whose percentage Windows itself was happy to show. WinRT's
/// IGameControllerBatteryInfo is the source Settings uses, and it gives real capacity numbers, so
/// try that first and keep XInput as the fallback.
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

        if (ReadWinRt() is { } winrt)
            return new BatteryState(true, winrt.Percent, winrt.Charging, PercentToCoarse(winrt.Percent));

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
