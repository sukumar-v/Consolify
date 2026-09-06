namespace Consolify.Models;

public class AppSettings
{
    // Display
    public string? TvDeviceName { get; set; }          // e.g. @"\\.\DISPLAY2"
    public bool SwitchPrimaryOnLaunch { get; set; } = true;
    public bool RepositionGameWindow { get; set; } = true;
    public bool KeepFocus { get; set; } = true;        // pull focus back when the desktop steals it
    public bool LaunchOnStartup { get; set; }

    // Gamepad → mouse
    public bool GamepadMouseEnabled { get; set; } = true;
    public bool GamepadMouseDuringGame { get; set; }   // off by default so it never fights native pad support
    public double Deadzone { get; set; } = 0.18;       // 0.05 .. 0.40
    public double Sensitivity { get; set; } = 1.0;     // 0.2 .. 3.0 (multiplier on max cursor speed)
    public double AccelExponent { get; set; } = 1.8;   // 1.0 linear .. 3.0 strongly curved
    /// <summary>Held to move the cursor and scroll faster. "Off" disables the boost.</summary>
    public string BoostButton { get; set; } = "RT";
    public double BoostMultiplier { get; set; } = 2.5; // 1.5 .. 5.0
    /// <summary>Replace Windows' cursors with blank ones while the D-pad drives navigation.
    /// Global state, so off by default — see CursorService.</summary>
    public bool HideCursorSystemWide { get; set; }

    // Button bindings (used outside the launcher UI; inside it A/B/Y/X/MENU follow the on-screen legend)
    public string LeftClickButton { get; set; } = "A";
    public string RightClickButton { get; set; } = "B";
    /// <summary>Gamepad combo that minimizes/restores the launcher, e.g. "LS + RS". "Off" disables it.</summary>
    public string MinimizeCombo { get; set; } = "LS + RS";
    /// <summary>Gamepad button or combo that taps the screenshot key. "Off" disables it.
    /// Evaluated even inside a focused game, which is the only place it is any use.</summary>
    public string ScreenshotCombo { get; set; } = "Off";

    // On-screen keyboard
    /// <summary>How long a D-pad direction must be held on the on-screen keyboard before the
    /// highlight starts repeating.</summary>
    public int KeyRepeatDelayMs { get; set; } = 350;
    /// <summary>Gap between repeats once it is moving; smaller is faster.</summary>
    public int KeyRepeatIntervalMs { get; set; } = 90;
    public string KeyboardToggleButton { get; set; } = "Start";
    public int KeyboardToggleHoldMs { get; set; } = 600;
    /// <summary>Builtin (the Consolify keyboard) | TabTip | Osk.</summary>
    public string KeyboardApp { get; set; } = "Builtin";
    /// <summary>Multiplier on the Consolify keyboard's key size, 0.6 .. 1.6. The base size is a
    /// fraction of the display height, so this only nudges it away from that.</summary>
    public double KeyboardScale { get; set; } = 1.0;
}
