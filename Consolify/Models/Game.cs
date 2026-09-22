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
    public string? BannerFile { get; set; }          // ~16:9 tile art, the shape a landscape tile wants
    /// <summary>Wide backdrop art (~3:1). Deliberately separate from BannerFile: a hero has its
    /// subject off-centre with empty space either side, so centre-cropping one into a tile shows a
    /// slice of background rather than the game.</summary>
    public string? HeroFile { get; set; }
    /// <summary>16:9 key art, for anything that fills a whole screen. The hero is 3.1:1 and a
    /// screen is not, so one of the two has to be cropped or banded; this is the one that
    /// needs neither. Only IGDB publishes art of this shape.</summary>
    public string? BackdropFile { get; set; }
    /// <summary>Transparent wordmark, for a theme that wants the title as art rather than text.</summary>
    public string? LogoFile { get; set; }
    public long SizeBytes { get; set; }
    public double PlaytimeMinutes { get; set; }      // tracked by Consolify sessions
    public int Sessions { get; set; }
    public DateTime? LastPlayed { get; set; }
    public bool Installed { get; set; } = true;
    public bool Manual { get; set; }
    public bool Favorite { get; set; }
    public bool Hidden { get; set; }                 // kept out of the library, listed under Hidden
    public bool PreferDirectLaunch { get; set; } // user chose an exe to bypass the store launcher

    // ---- Fetched metadata ----
    // Everything below is filled in by MetadataService and is purely cosmetic: the launcher works
    // exactly the same with all of it null, which is what an offline first run looks like.

    /// <summary>One or two sentences. The store's short pitch, not the full description -- the
    /// detail page has room for about three lines and nobody reads a wall of text from a sofa.</summary>
    public string? Description { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public List<string> Genres { get; set; } = new();
    /// <summary>Display string as the source gave it, e.g. "Feb 24, 2017". Not parsed: sources
    /// disagree on format and precision, and it is only ever shown, never sorted on.</summary>
    public string? ReleaseDate { get; set; }
    /// <summary>Metacritic score, 0-100, or null when the game has none. Plenty of games -- most
    /// indies and anything recent -- simply are not scored, so null is the normal case.</summary>
    public int? CriticScore { get; set; }
    /// <summary>Where the score came from, so the UI can attribute it rather than implying we
    /// computed it.</summary>
    public string? CriticSource { get; set; }
    /// <summary>PEGI age, one of 3, 7, 12, 16, 18, or null. Only IGDB carries this -- Steam's own
    /// ratings block is per-storefront-region and is missing more often than not.</summary>
    public int? PegiRating { get; set; }
    /// <summary>"full", "partial" or null. Worth surfacing in a couch launcher above almost
    /// anything else: it answers "can I actually play this from the sofa".</summary>
    public string? ControllerSupport { get; set; }
    /// <summary>Which provider answered, e.g. "steam". Also the flag for "we have tried this one".</summary>
    public string? MetadataSource { get; set; }
    /// <summary>When it was fetched, so a rescan does not re-hit the network for everything.
    /// Null means never tried.</summary>
    public DateTime? MetadataFetched { get; set; }
}
