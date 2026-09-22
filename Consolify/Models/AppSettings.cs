namespace Consolify.Models;

public class AppSettings
{
    // Appearance
    /// <summary>
    /// The one colour the UI is built around -- focus rings, active tabs, sliders. "#RRGGBB";
    /// the UI overrides its --accent token with it and derives every tint from there.
    /// </summary>
    public string AccentColor { get; set; } = "#F0A253";
    /// <summary>Folder name under %APPDATA%\Consolify\themes, or "" for the built-in look.
    /// A theme that has been deleted falls back to the default rather than failing. Marquee ships
    /// with the app, so a fresh install opens on it rather than on the plain built-in look.</summary>
    public string Theme { get; set; } = "marquee";
    /// <summary>Hide the button-hint bar along the bottom of every screen. Off by default: it is
    /// the only thing telling a new player what A and Y do, so it is opt-out, not opt-in.</summary>
    public bool HideLegend { get; set; }

    // Metadata providers
    // Nothing here needs setting. Steam games are keyed by app id, non-Steam games are looked up
    // against Steam by title, and anything left over goes through the shared metadata service --
    // none of which asks the user for anything.
    //
    // The three below are an override for people who would rather use their own credentials than
    // someone else's server. When set they take priority over the service. Stored in plain text in
    // settings.json, which is what Playnite does too, but worth knowing before pasting a secret in.
    /// <summary>Twitch application client id, for IGDB. Free, non-commercial use only.</summary>
    public string IgdbClientId { get; set; } = "";
    /// <summary>Twitch application client secret, for IGDB.</summary>
    public string IgdbClientSecret { get; set; } = "";
    /// <summary>SteamGridDB API key. Art only, and the best source of it for non-Steam games.</summary>
    public string SteamGridDbKey { get; set; } = "";
    /// <summary>Overrides the shipped metadata service endpoint. Empty means use the built-in one;
    /// this exists for self-hosting and for testing, not as something anyone need ever set.</summary>
    public string MetadataEndpoint { get; set; } = "";

    // Display
    public string? TvDeviceName { get; set; }          // e.g. @"\\.\DISPLAY2"
    public bool SwitchPrimaryOnLaunch { get; set; } = true;
    public bool RepositionGameWindow { get; set; } = true;
    public bool KeepFocus { get; set; } = true;        // pull focus back when the desktop steals it
    /// <summary>On by default: a launcher you have to go and find on the desktop is not a
    /// launcher anyone uses from a sofa.</summary>
    public bool LaunchOnStartup { get; set; } = true;

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
    /// <summary>Gamepad combo that minimizes/restores the launcher, e.g. "LS + RS". "Off" disables
    /// it. Guide is the button a console player already reaches for, so that is the default --
    /// Windows and Steam both grab it, and the Settings row says so and how to free it.</summary>
    public string MinimizeCombo { get; set; } = "Guide";
    /// <summary>Gamepad button or combo that taps the screenshot key. "Off" disables it.
    /// Evaluated even inside a focused game, which is the only place it is any use.</summary>
    public string ScreenshotCombo { get; set; } = "Off";

    // On-screen keyboard
    /// <summary>How long a D-pad direction must be held on the on-screen keyboard before the
    /// highlight starts repeating.</summary>
    public int KeyRepeatDelayMs { get; set; } = 350;
    /// <summary>Gap between repeats once it is moving; smaller is faster.</summary>
    public int KeyRepeatIntervalMs { get; set; } = 90;
    /// <summary>Shows and hides the on-screen keyboard. RB rather than Start because Start is the
    /// launcher's own Menu button, and in Press mode the keyboard takes the press outright.</summary>
    public string KeyboardToggleButton { get; set; } = "RB";
    /// <summary>"Press" (a tap) or "Hold". A tap is the quicker of the two and is the default;
    /// Hold is for anyone whose toggle button also has a job inside the launcher.</summary>
    public string KeyboardToggleMode { get; set; } = "Press";
    /// <summary>How long the button is held in Hold mode. Not used in Press mode.</summary>
    public int KeyboardToggleHoldMs { get; set; } = 400;
    /// <summary>
    /// Whether the toggle button reaches the keyboard while a game holds the foreground.
    ///
    /// Off by default: inside a game every button belongs to the game, and a keyboard sliding up
    /// over one mid-fight is a surprise nobody asked for. Closing a keyboard that is already up is
    /// always allowed regardless, so this can never strand one on screen.
    /// </summary>
    public bool KeyboardInGame { get; set; }
    /// <summary>Builtin (the Consolify keyboard) | TabTip | Osk.</summary>
    public string KeyboardApp { get; set; } = "TabTip";
    /// <summary>Multiplier on the Consolify keyboard's key size, 0.6 .. 1.6. The base size is a
    /// fraction of the display height, so this only nudges it away from that.</summary>
    public double KeyboardScale { get; set; } = 1.0;
}
