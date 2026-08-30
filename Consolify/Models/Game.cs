namespace Consolify.Models;

public class Game
{
    public string Id { get; set; } = "";            // e.g. "steam:1091500", "manual:<guid>"
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "Manual"; // Steam | Epic | GOG | Manual
    public string? ExePath { get; set; }             // direct executable (GOG / Manual / Epic)
    public string? Args { get; set; }
    public string? LaunchUri { get; set; }           // steam:// or com.epicgames.launcher:// URI
    public string? InstallDir { get; set; }          // used to find the game process after URI launches
    public string? CoverFile { get; set; }           // portrait art, file name inside <appdata>\covers
    public string? BannerFile { get; set; }          // landscape art (continue row / detail hero)
    public long SizeBytes { get; set; }
    public double PlaytimeMinutes { get; set; }      // tracked by Consolify sessions
    public int Sessions { get; set; }
    public DateTime? LastPlayed { get; set; }
    public bool Installed { get; set; } = true;
    public bool Manual { get; set; }
    public bool Favorite { get; set; }
    public bool Hidden { get; set; }                 // kept out of the library, listed under Hidden
    public bool PreferDirectLaunch { get; set; } // user chose an exe to bypass the store launcher
}
