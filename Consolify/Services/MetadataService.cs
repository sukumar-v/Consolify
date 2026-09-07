using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Consolify.Models;

namespace Consolify.Services;

/// <summary>
/// Fills in the things a filesystem scan cannot know: a description, who made it, when it came
/// out, what it scored, and art at a resolution worth putting on a television.
///
/// Three sources, in descending order of trust:
///
///   Steam        keyed by app id, which the scanner already has. No key, no account, and no
///                title matching, so a Steam game can never be given another game's art. Always
///                used for "steam:" entries, and the others never override it.
///   IGDB         everything else's facts. Needs the user's Twitch credentials.
///   SteamGridDB  everything else's art, which is what it exists for. Needs the user's API key.
///
/// The two keyed providers match by title, so everything they return goes through TitleMatch,
/// which declines anything short of an exact match. See the note there: a wrong cover is worse
/// than a missing one, because nothing about it looks wrong.
///
/// Nothing here is required. Every field it writes is cosmetic, every failure is swallowed, and a
/// machine with no network -- or no credentials -- simply keeps the art the scanner copied out of
/// Steam's local cache.
/// </summary>
public class MetadataService
{
    // Steam's store endpoint is undocumented and rate limited at roughly 200 requests per 5
    // minutes per IP. A library big enough to matter is fetched once and then not again for a
    // fortnight, and the gap below keeps even a 200-game first run inside the limit.
    private const int StoreGapMs = 1500;
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(14);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        c.DefaultRequestHeaders.Add("User-Agent", "Consolify/1.0 (+https://github.com/consolify)");
        return c;
    }

    /// <summary>Which picture a file is, independent of what it ends up called on disk.</summary>
    private enum Slot { Cover, Tile, Hero, Logo }

    /// <summary>
    /// Steam serves these straight off its CDN, unauthenticated, for any app id. The sizes are the
    /// originals rather than the half-size copies the client keeps on disk.
    ///
    /// capsule_616x353 is the one that matters for a landscape tile: 1.75:1 is near enough 16:9 to
    /// crop invisibly, and it has the logo burnt in, so a tile reads as the game at a glance from
    /// across a room. Not every app has one -- header.jpg (2.14:1) is the fallback and always
    /// exists.
    /// </summary>
    private static readonly (string Remote, Slot Slot)[] SteamArt =
    {
        ("library_600x900_2x.jpg", Slot.Cover), // the full-size portrait, 600x900 up
        ("capsule_616x353.jpg",    Slot.Tile),  // 616x353, the landscape tile
        ("header.jpg",             Slot.Tile),  // fallback for the above
        ("library_hero.jpg",       Slot.Hero),  // 1920x620 backdrop
        ("logo.png",               Slot.Logo),  // transparent wordmark
    };

    /// <summary>
    /// Brings every game that needs it up to date, in place. Returns the number actually touched
    /// so the caller can skip a UI push when there was nothing to do.
    /// </summary>
    public async Task<int> EnrichAsync(IReadOnlyList<Game> games, AppSettings settings,
        CancellationToken ct = default)
    {
        var igdb = IgdbClient.IsConfigured(settings.IgdbClientId, settings.IgdbClientSecret)
            ? new IgdbClient(Http, settings.IgdbClientId, settings.IgdbClientSecret)
            : null;
        var grid = SteamGridDbClient.IsConfigured(settings.SteamGridDbKey)
            ? new SteamGridDbClient(Http, settings.SteamGridDbKey)
            : null;

        var due = games.Where(g => NeedsFetch(g, igdb is not null || grid is not null)).ToList();
        if (due.Count == 0) return 0;

        Log.Info($"Metadata: {due.Count} game(s) to fetch" +
                 (igdb is not null ? ", IGDB on" : "") + (grid is not null ? ", SteamGridDB on" : ""));
        var changed = 0;
        var pacedCall = false;

        foreach (var g in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var steam = SteamAppId(g) is not null;

                // Space out the store calls, and only between them -- the first game should not
                // sit waiting, and the keyed providers pace themselves.
                if (steam && pacedCall) await Task.Delay(StoreGapMs, ct);
                if (steam) pacedCall = true;

                var touched = steam
                    ? await EnrichSteamAsync(g, ct)
                    : await EnrichElsewhereAsync(g, igdb, grid, ct);

                // Stamped even when nothing was found, so a game that genuinely has no metadata is
                // not looked up again on every launch -- but NOT when every provider that could
                // have answered was refusing our credentials. Otherwise a typo'd key would mark
                // the whole library "tried" and fixing the key would appear to do nothing for a
                // fortnight.
                if (steam || !AllProvidersRejected(igdb, grid)) g.MetadataFetched = DateTime.UtcNow;
                if (touched) changed++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Left unstamped, so a later run tries again rather than treating a dropped
                // connection as "this game has no metadata".
                Log.Info($"Metadata: {g.Title} failed: {ex.Message}");
            }
        }

        Log.Info($"Metadata: updated {changed} game(s)");
        return changed;
    }

    /// <summary>
    /// True when every configured provider has had its credentials refused, so there is nothing
    /// left that could answer for a non-Steam game this pass.
    /// </summary>
    private static bool AllProvidersRejected(IgdbClient? igdb, SteamGridDbClient? grid)
    {
        if (igdb is null && grid is null) return false;
        return (igdb is null || igdb.CredentialsRejected) && (grid is null || grid.KeyRejected);
    }

    private static bool NeedsFetch(Game g, bool haveKeyedProviders)
    {
        // Without credentials nothing can answer for a non-Steam game, so it is not "due" -- and
        // the moment keys are added its null timestamp makes it due immediately.
        if (SteamAppId(g) is null && !haveKeyedProviders) return false;

        // Art can go missing on its own -- a cleared covers folder, a half-finished first run --
        // so a game inside the freshness window is still due if its files are not there.
        if (g.MetadataFetched is { } at && DateTime.UtcNow - at < Freshness && HasFetchedArt(g)) return false;
        return true;
    }

    private static bool HasFetchedArt(Game g) =>
        g.BannerFile is { } b && b.Contains("_hd") && File.Exists(Path.Combine(Paths.CoversDir, b));

    private static string? SteamAppId(Game g) =>
        g.Id.StartsWith("steam:", StringComparison.Ordinal) && g.Id.Length > 6 ? g.Id[6..] : null;

    /// <summary>Base name for this game's downloaded art. "steam:367520" -> "steam_367520".</summary>
    private static string ArtPrefix(Game g) => g.Id.Replace(':', '_');

    // ---------- Steam ----------

    private async Task<bool> EnrichSteamAsync(Game g, CancellationToken ct)
    {
        var appId = SteamAppId(g)!;
        var gotArt = await FetchSteamArtAsync(g, appId, ct);
        var gotFacts = await FetchSteamFactsAsync(g, appId, ct);
        if (gotFacts) g.MetadataSource = "steam";
        return gotArt || gotFacts;
    }

    private async Task<bool> FetchSteamArtAsync(Game g, string appId, CancellationToken ct)
    {
        var any = false;
        foreach (var (remote, slot) in SteamArt)
        {
            if (ct.IsCancellationRequested) break;
            var name = ArtPrefix(g) + SuffixFor(slot, Path.GetExtension(remote));
            var dest = Path.Combine(Paths.CoversDir, name);

            // header.jpg fills the same slot as the capsule and is only there for the apps that
            // have no capsule, so it must not overwrite one we already have.
            if (File.Exists(dest)) { any |= Assign(g, slot, name); continue; }

            var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{remote}";
            if (!await DownloadAsync(url, dest, ct)) continue;
            any |= Assign(g, slot, name);
        }
        return any;
    }

    private async Task<bool> FetchSteamFactsAsync(Game g, string appId, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english" +
                  "&filters=basic,genres,metacritic,release_date,developers,publishers,controller_support";

        using var res = await Http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return false;

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty(appId, out var entry)) return false;
        if (!entry.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return false;
        if (!entry.TryGetProperty("data", out var d)) return false;

        string? Str(string key) =>
            d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        string? First(string key) =>
            d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().FirstOrDefault().GetString() : null;

        g.Description = Clean(Str("short_description"));
        g.Developer = First("developers");
        g.Publisher = First("publishers");
        g.ControllerSupport = Str("controller_support");

        if (d.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            g.Genres = genres.EnumerateArray()
                .Select(x => x.TryGetProperty("description", out var n) ? n.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();

        if (d.TryGetProperty("release_date", out var rel)
            && rel.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String)
        {
            var text = date.GetString();
            g.ReleaseDate = string.IsNullOrWhiteSpace(text) ? null : text;
        }

        // Steam carries Metacritic's score for the games that have one, which is most big
        // releases and almost no indies. Absent is the normal case, not a failure.
        if (d.TryGetProperty("metacritic", out var mc)
            && mc.TryGetProperty("score", out var score) && score.TryGetInt32(out var n))
        {
            g.CriticScore = n;
            g.CriticSource = "Metacritic";
        }
        else
        {
            g.CriticScore = null;
            g.CriticSource = null;
        }

        return true;
    }

    // ---------- Everything else ----------

    /// <summary>
    /// Epic, GOG, Xbox and manually added games. IGDB answers for the facts, SteamGridDB for the
    /// art, and either may be absent -- a user who configured only one gets only that half.
    /// </summary>
    private async Task<bool> EnrichElsewhereAsync(Game g, IgdbClient? igdb, SteamGridDbClient? grid,
        CancellationToken ct)
    {
        var any = false;

        if (igdb is not null)
        {
            var hit = await igdb.FindAsync(g.Title, ct);
            if (hit is not null)
            {
                g.Description = Clean(hit.Summary);
                g.Developer = hit.Developer;
                g.Publisher = hit.Publisher;
                if (hit.Genres.Count > 0) g.Genres = hit.Genres;
                g.ReleaseDate = hit.Released?.ToString("MMM d, yyyy");
                g.CriticScore = hit.CriticScore;
                // Named for what it is. IGDB aggregates external reviews itself, so calling this
                // Metacritic would put one publication's name on another's number.
                g.CriticSource = hit.CriticScore is null ? null : "IGDB critics";
                g.MetadataSource = "igdb";
                any = true;

                // IGDB's art is the fallback: its covers are portrait box art and its "artworks"
                // are wide key art, neither shaped like a tile. SteamGridDB below beats both when
                // it has anything, which is why these are fetched first.
                if (hit.CoverImageId is { } cover)
                    any |= await StoreRemoteAsync(g, Slot.Cover, IgdbClient.ImageUrl(cover, "cover_big_2x"), ct);
                if (hit.ArtworkImageId is { } art)
                {
                    any |= await StoreRemoteAsync(g, Slot.Tile, IgdbClient.ImageUrl(art, "720p"), ct);
                    any |= await StoreRemoteAsync(g, Slot.Hero, IgdbClient.ImageUrl(art, "1080p"), ct);
                }
            }
        }

        if (grid is not null)
        {
            var art = await grid.FindArtAsync(g.Title, ct);
            if (art is not null)
            {
                if (art.Portrait is { } p) any |= await StoreRemoteAsync(g, Slot.Cover, p, ct);
                if (art.Tile is { } t) any |= await StoreRemoteAsync(g, Slot.Tile, t, ct);
                if (art.Hero is { } h) any |= await StoreRemoteAsync(g, Slot.Hero, h, ct);
                if (art.Logo is { } l) any |= await StoreRemoteAsync(g, Slot.Logo, l, ct);
            }
        }

        return any;
    }

    // ---------- Art plumbing ----------

    /// <summary>
    /// Downloads one picture into a slot, overwriting whatever was there. Callers run worst source
    /// first, so the last one to fill a slot wins it.
    /// </summary>
    private static async Task<bool> StoreRemoteAsync(Game g, Slot slot, string url, CancellationToken ct)
    {
        var name = ArtPrefix(g) + SuffixFor(slot, ExtensionOf(url));
        var dest = Path.Combine(Paths.CoversDir, name);
        if (!await DownloadAsync(url, dest, ct)) return false;
        // Assign returns false when the name has not changed, but the bytes on disk are new, so
        // the slot still counts as filled.
        Assign(g, slot, name);
        return true;
    }

    private static string SuffixFor(Slot slot, string extension) => slot switch
    {
        Slot.Tile => "_hdtile" + extension,
        Slot.Hero => "_hdhero" + extension,
        Slot.Logo => "_hdlogo" + extension,
        _ => "_hd" + extension,
    };

    /// <summary>
    /// The file extension a URL implies. SteamGridDB serves .png, .jpg and .webp from the same
    /// endpoint, and the name has to match the bytes for WebView2 to decode it.
    /// </summary>
    private static string ExtensionOf(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" ? ext : ".jpg";
    }

    /// <summary>Points the game at art we just wrote. Returns true when it actually changed.</summary>
    private static bool Assign(Game g, Slot slot, string name)
    {
        switch (slot)
        {
            // A cover the user picked by hand outranks anything we can download.
            case Slot.Cover when g.CoverFile != name && !IsCustom(g.CoverFile):
                g.CoverFile = name; return true;
            case Slot.Tile when g.BannerFile != name: g.BannerFile = name; return true;
            case Slot.Hero when g.HeroFile != name: g.HeroFile = name; return true;
            case Slot.Logo when g.LogoFile != name: g.LogoFile = name; return true;
            default: return false;
        }
    }

    private static bool IsCustom(string? file) =>
        file is not null && file.StartsWith("custom_", StringComparison.Ordinal);

    private static async Task<bool> DownloadAsync(string url, string dest, CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return false;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 1024) return false;   // Steam answers 200 with a placeholder for some ids

            // Write beside the target and move into place: a download interrupted halfway would
            // otherwise leave a truncated file that looks present and renders as a broken tile.
            var tmp = dest + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Metadata: {Path.GetFileName(dest)} <- {url} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Store descriptions are HTML: entities throughout and the occasional tag. Both render as
    /// literal noise in a text node, so strip the tags and decode the entities.
    /// </summary>
    private static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }
}
