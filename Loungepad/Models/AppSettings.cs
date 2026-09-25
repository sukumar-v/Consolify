using System.Text.Json;

namespace Loungepad.Models;

public class AppSettings
{
    // Appearance
    /// <summary>
    /// The one colour the UI is built around -- focus rings, active tabs, sliders. "#RRGGBB";
    /// the UI overrides its --accent token with it and derives every tint from there.
    /// </summary>
    public string AccentColor { get; set; } = "#F0A253";
    /// <summary>Folder name under %APPDATA%\Loungepad\themes, or "" for the built-in look.
    /// A theme that has been deleted falls back to the built-in look rather than failing. Polish
    /// ships with the app, so a fresh install opens on it rather than on Classic.</summary>
    public string Theme { get; set; } = "polish";
    /// <summary>Hide the button-hint bar along the bottom of every screen. Off by default: it is
    /// the only thing telling a new player what A and Y do, so it is opt-out, not opt-in.</summary>
    public bool HideLegend { get; set; }
    /// <summary>Screens, menus and the Power Wheel move into place rather than appearing. Off
    /// makes every change instant: the page does it with one CSS multiplier (--motion), so
    /// nothing here has to know which animation is which.</summary>
    public bool AnimationsEnabled { get; set; } = true;
    /// <summary>Multiplier on the speed of every animation, 0.5 .. 2.0. 1.0 is the design's own timing.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    /// <summary>
    /// The themes' own options, keyed by theme id and then by option id: the values behind the
    /// rows a theme declares in its theme.json (see the README's Themes section). Kept here rather
    /// than in the theme's folder so updating a bundled theme, which replaces the folder, keeps
    /// them, and per theme so switching away and back finds them as they were. Only the page reads
    /// them, as CSS, and it checks each value against the theme's definition first; the host keeps
    /// them to plain values (ThemeService.CleanSettingValues) and nothing more.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>> ThemeSettings { get; set; } = new();

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

    // Steam account
    /// <summary>
    /// Bring in the whole Steam library, not just the part on disk. The account is read off the
    /// Steam client's own login file, so there is nothing to sign into; the list comes from the
    /// Web API through the shared service, or through the key below. Off by default because it is
    /// the one feature that sends something identifying -- the SteamID -- anywhere, and the README
    /// promises "nothing phoned anywhere" for the plain scan.
    /// </summary>
    public bool SteamShowOwned { get; set; }
    /// <summary>The user's own Steam Web API key, free from steamcommunity.com/dev/apikey. Only
    /// needed when the profile keeps its game details private, which the shared key cannot read;
    /// a key issued to the profile's owner can. Plain text in settings.json, like the rest.</summary>
    public string SteamApiKey { get; set; } = "";

    // Other stores
    // Epic, GOG and Xbox are not settings at all: each is a sign-in, kept encrypted under
    // %APPDATA%\Loungepad\accounts, and being signed in is what turns the store's library on.
    /// <summary>Every game included with PC Game Pass, from Microsoft's public catalogue, each
    /// installable from here. A catalogue rather than a library, hence its own switch.</summary>
    public bool GamePassCatalog { get; set; }
    /// <summary>Optional. The client id of an Azure app registration of the user's own, for the
    /// Xbox sign-in. Empty means the Xbox app's own client, which needs nothing; this is the way
    /// out if Microsoft ever stops accepting that. See the README.</summary>
    public string XboxClientId { get; set; } = "";

    // Mods
    /// <summary>Optional. Where Vortex is, for a portable copy or one installed somewhere its
    /// installer does not put it. Empty means look in the usual places: the per-user Programs
    /// folder, the uninstall entries, Program Files.</summary>
    public string VortexPath { get; set; } = "";

    // Emulation
    /// <summary>Look for installed emulators and for ROMs on every scan -- RetroArch's playlists,
    /// an Emulation\roms layout, folders named after a system -- and add what is found. On by
    /// default for the same reason the store scans are: nobody wants to go and set up what the
    /// machine already knows. Everything found can be removed, and stays removed.</summary>
    public bool DetectEmulators { get; set; } = true;

    // Display
    public string? TvDeviceName { get; set; }          // e.g. @"\\.\DISPLAY2"
    public bool SwitchPrimaryOnLaunch { get; set; } = true;
    public bool RepositionGameWindow { get; set; } = true;
    public bool KeepFocus { get; set; } = true;        // pull focus back when the desktop steals it
    /// <summary>On by default: a launcher you have to go and find on the desktop is not a
    /// launcher anyone uses from a sofa.</summary>
    public bool LaunchOnStartup { get; set; } = true;
    /// <summary>Check GitHub for a new release every few hours, download it in the background and
    /// install it the next time the app starts. Off, nothing is fetched until Settings or the tray
    /// asks. See UpdateService.</summary>
    public bool AutoUpdate { get; set; } = true;

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
    /// <summary>The touchpad on a DualSense or DualShock 4 as a trackpad: swipe to move the
    /// pointer, press the pad to click, press with two fingers for a right click.</summary>
    public bool TouchpadMouse { get; set; } = true;
    /// <summary>Scales the touchpad's gain. The gain itself follows the finger's speed -- see the
    /// touchpad section of GamepadService -- and 1.0 is a laptop-like feel.</summary>
    public double TouchpadSensitivity { get; set; } = 1.0;   // 0.25 .. 4.0
    /// <summary>A short, still touch is a click, and a two-finger one a right click. The pad's own
    /// press always clicks regardless.</summary>
    public bool TouchpadTapToClick { get; set; } = true;
    /// <summary>Tap, then touch and hold: the left button stays down while the finger moves, to drag a
    /// window or select text. Costs every tap a short wait, which is why it can be turned off.</summary>
    public bool TouchpadTapDrag { get; set; } = true;
    /// <summary>Two-finger scrolling moves the content with the fingers, as Windows' touchpads do by default.</summary>
    public bool TouchpadNaturalScroll { get; set; } = true;
    /// <summary>Scales two-finger scrolling. 1.0 scrolls about twelve notches over the pad's height.</summary>
    public double TouchpadScrollSpeed { get; set; } = 1.0;   // 0.25 .. 4.0

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
    /// <summary>Builtin (the Loungepad keyboard) | TabTip | Osk.</summary>
    public string KeyboardApp { get; set; } = "TabTip";
    /// <summary>Multiplier on the Loungepad keyboard's key size, 0.6 .. 1.6. The base size is a
    /// fraction of the display height, so this only nudges it away from that.</summary>
    public double KeyboardScale { get; set; } = 1.0;
}
