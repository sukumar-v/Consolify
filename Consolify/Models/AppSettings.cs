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
    public string KeyboardToggleButton { get; set; } = "Start";
    public int KeyboardToggleHoldMs { get; set; } = 600;
    /// <summary>Builtin (the gamepad-driven on-screen keyboard) | TabTip | Osk.</summary>
    public string KeyboardApp { get; set; } = "Builtin";
}
