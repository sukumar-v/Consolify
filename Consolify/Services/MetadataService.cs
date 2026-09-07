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

    private DateTime _lastStoreCall = DateTime.MinValue;

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
    /// capsule_616x353 is the one that matters for a landscape tile: it has the logo burnt in, so a
    /// tile reads as the game at a glance from across a room. Not every app has one -- header.jpg
    /// (2.14:1) is the fallback and always exists, and is why tiles must never be cover-cropped.
    ///
    /// The hero is taken at 2x. The backdrop element is inset -80px, so on a 1920x1080 stage it is
    /// 2080x1240 -- and covering that from the 1920x620 hero meant a 2x upscale showing 54% of the
    /// width, which is exactly the "zoomed in and blurry" it looked like. library_hero_2x is
    /// 3840x1240: the same crop, but every pixel is now one pixel or better.
    /// </summary>
    /// Each carries its own file suffix rather than sharing one per slot, so a name on disk says
    /// which source it came from. That is what lets an existing install pick up a better source --
    /// the 2x hero did not exist here until recently -- while still skipping a download for art it
    /// already has. Sharing one name per slot meant either re-downloading the library every pass or
    /// never being able to improve it.
    private static readonly (string Remote, Slot Slot, string Suffix)[] SteamArt =
    {
        ("library_600x900_2x.jpg", Slot.Cover, "_hd"),       // the full-size portrait, 600x900 up
        ("capsule_616x353.jpg",    Slot.Tile,  "_hdtile"),   // 616x353, the landscape tile
        ("header.jpg",             Slot.Tile,  "_hdhead"),   // 460x215 fallback for the above
        ("library_hero_2x.jpg",    Slot.Hero,  "_hdhero2x"), // 3840x1240 -- see below
        ("library_hero.jpg",       Slot.Hero,  "_hdhero"),   // 1920x620, the fallback
        ("logo.png",               Slot.Logo,  "_hdlogo"),   // transparent wordmark
    };

    /// <summary>
    /// Brings every game that needs it up to date, in place. Returns the number actually touched
    /// so the caller can skip a UI push when there was nothing to do.
    /// </summary>
    public async Task<int> EnrichAsync(IReadOnlyList<Game> games, AppSettings settings,
        CancellationToken ct = default)
    {
        // The user's own credentials win over the shared service. Somebody who has gone to the
        // trouble of registering a Twitch application should not be silently routed through
        // somebody else's server, and it gives them a way out if the service is ever down.
        IFactsProvider? facts = IgdbClient.IsConfigured(settings.IgdbClientId, settings.IgdbClientSecret)
            ? new IgdbClient(Http, settings.IgdbClientId, settings.IgdbClientSecret)
            : null;
        IArtProvider? art = SteamGridDbClient.IsConfigured(settings.SteamGridDbKey)
            ? new SteamGridDbClient(Http, settings.SteamGridDbKey)
            : null;

        var endpoint = string.IsNullOrWhiteSpace(settings.MetadataEndpoint)
            ? MetadataProxyClient.DefaultEndpoint
            : settings.MetadataEndpoint;
        if ((facts is null || art is null) && MetadataProxyClient.IsConfigured(endpoint))
        {
            var proxy = new MetadataProxyClient(Http, endpoint);
            facts ??= proxy;
            art ??= proxy;
        }

        var search = new SteamSearchClient(Http);
        var due = games.Where(g => NeedsFetch(g)).ToList();
        if (due.Count == 0) return 0;

        Log.Info($"Metadata: {due.Count} game(s) to fetch" +
                 $", facts={Describe(facts)}, art={Describe(art)}");
        var changed = 0;

        foreach (var g in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var appId = SteamAppId(g);

                // A non-Steam game that Steam nonetheless sells. Resolved first even though Steam
                // is now the fallback, because an app id is worth having either way: the service
                // is asked by id rather than by title, which removes the matching from the whole
                // exchange, and Steam can then fill anything the service leaves empty.
                if (appId is null)
                {
                    await PaceStoreAsync(ct);
                    appId = await search.FindAppIdAsync(g.Title, ct);
                }

                // The service first, then Steam for whatever it did not answer. SteamGridDB's art
                // is often better shaped than Steam's own -- a proper landscape tile for a game
                // that only publishes a 2.14:1 header -- while Steam still holds the Metacritic
                // score and the controller-support flag, which IGDB has no equivalent of.
                var filled = new HashSet<Slot>();
                var (touched, serviceFacts) = await EnrichElsewhereAsync(g, facts, art, appId, filled, ct);

                // Steam runs after, as the fallback: it fills every art slot and every field the
                // service left empty, and it always supplies controller support, which IGDB has
                // no equivalent of.
                if (appId is not null) touched |= await EnrichSteamAsync(g, appId, filled, serviceFacts, ct);

                // Stamped even when nothing was found, so a game that genuinely has no metadata is
                // not looked up again on every launch -- but NOT when every source that could have
                // answered was unavailable. Otherwise a typo'd key or an outage would mark the
                // library "tried" and fixing it would appear to do nothing for a fortnight.
                if (appId is not null || !AllUnavailable(facts, art)) g.MetadataFetched = DateTime.UtcNow;
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

    private static string Describe(object? provider) => provider switch
    {
        null => "none",
        MetadataProxyClient => "proxy",
        IgdbClient => "your IGDB key",
        SteamGridDbClient => "your SteamGridDB key",
        _ => "on",
    };

    /// <summary>
    /// True when every source that could answer for a non-Steam game has given up for this pass --
    /// bad credentials, an outage, a rate limit -- or when there was never one configured.
    /// </summary>
    private static bool AllUnavailable(IFactsProvider? facts, IArtProvider? art)
    {
        if (facts is null && art is null) return true;
        return (facts is null || facts.Unavailable) && (art is null || art.Unavailable);
    }

    /// <summary>
    /// Steam's store endpoints are rate limited at roughly 200 requests per 5 minutes per IP, and
    /// both the search and appdetails count. Spacing every store call rather than every game keeps
    /// a library that leans on the search tier inside the same budget.
    /// </summary>
    private async Task PaceStoreAsync(CancellationToken ct)
    {
        var wait = StoreGapMs - (int)(DateTime.UtcNow - _lastStoreCall).TotalMilliseconds;
        if (wait > 0) await Task.Delay(wait, ct);
        _lastStoreCall = DateTime.UtcNow;
    }

    private static bool NeedsFetch(Game g)
    {
        // Art can go missing on its own -- a cleared covers folder, a half-finished first run --
        // so a game inside the freshness window is still due if its files are not there. Nothing
        // is excluded up front any more: with the keyless Steam tiers there is always something
        // that might answer, and the sources themselves decide whether they can.
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

    /// <summary>
    /// The Steam path, used both for a "steam:" entry and for a non-Steam game the search resolved
    /// to an app id. Identical either way: once there is an app id there is no guessing left.
    ///
    /// Facts come first because they carry a fallback the art step needs. Newer apps have stopped
    /// publishing art at the legacy cdn/steam/apps/&lt;id&gt;/&lt;name&gt; paths -- Forza Horizon 6 has only
    /// library_hero.jpg there, and 404s for the capsule and the header -- but appdetails always
    /// names a working header_image under store_item_assets, hashed per release.
    /// </summary>
    private async Task<bool> EnrichSteamAsync(Game g, string appId, HashSet<Slot> filled,
        bool serviceAnswered, CancellationToken ct)
    {
        await PaceStoreAsync(ct);
        var (gotFacts, headerImage) = await FetchSteamFactsAsync(g, appId, serviceAnswered, ct);
        if (gotFacts && g.MetadataSource is null) g.MetadataSource = "steam";

        var gotArt = await FetchSteamArtAsync(g, appId, headerImage, filled, ct);
        return gotArt || gotFacts;
    }

    private async Task<bool> FetchSteamArtAsync(Game g, string appId, string? headerImage,
        HashSet<Slot> filled, CancellationToken ct)
    {
        var any = false;

        // Which slots this pass has already filled. Several entries compete for one slot -- the
        // capsule then header.jpg, the 2x hero then the 1x -- and they are listed best first, so
        // the first to succeed wins and the rest are skipped for that slot.
        foreach (var (remote, slot, suffix) in SteamArt)
        {
            if (ct.IsCancellationRequested) break;
            if (filled.Contains(slot)) continue;

            var name = ArtPrefix(g) + suffix + Path.GetExtension(remote);
            var dest = Path.Combine(Paths.CoversDir, name);

            // Already have this exact asset, so nothing to fetch -- and because the name identifies
            // the source, this cannot mask a better one that has not been tried yet.
            if (File.Exists(dest)) { filled.Add(slot); any |= Assign(g, slot, name); continue; }

            var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{remote}";
            if (!await DownloadAsync(url, dest, ct)) continue;

            filled.Add(slot);
            any |= Assign(g, slot, name);
        }

        // Last resort for the tile, and the only art newer apps publish at all. Checked against
        // the game rather than a filename because the loop above may have written a tile from a
        // legacy path already, and that one is the better shape.
        if (!filled.Contains(Slot.Tile) && !string.IsNullOrWhiteSpace(headerImage))
            any |= await StoreRemoteAsync(g, Slot.Tile, headerImage, filled, ct);

        return any;
    }

    /// <summary>Facts, plus the header image URL appdetails names -- the art step needs it as a
    /// fallback for apps that no longer publish to the legacy CDN paths.</summary>
    private async Task<(bool Ok, string? HeaderImage)> FetchSteamFactsAsync(Game g, string appId,
        bool serviceAnswered, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english" +
                  "&filters=basic,genres,metacritic,release_date,developers,publishers,controller_support";

        using var res = await Http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return (false, null);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty(appId, out var entry)) return (false, null);
        if (!entry.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return (false, null);
        if (!entry.TryGetProperty("data", out var d)) return (false, null);

        string? Str(string key) =>
            d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        string? First(string key) =>
            d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().FirstOrDefault().GetString() : null;

        // Controller support is taken whatever else happened: IGDB has no equivalent of it, and on
        // a couch it is the most useful line on the detail page. This is the reason Steam is still
        // worth asking for a game the service has already answered.
        g.ControllerSupport = Str("controller_support");

        // Steam carries Metacritic's score for the games that have one -- most big releases and
        // almost no indies. Taken only when the service produced no score of its own, and always
        // labelled for whichever actually answered.
        var metacritic = d.TryGetProperty("metacritic", out var mc) ? JsonNum.Int(mc, "score") : null;
        if (g.CriticScore is null && metacritic is { } n)
        {
            g.CriticScore = n;
            g.CriticSource = "Metacritic";
        }

        // The rest is Steam's only when the service did not answer at all. Keyed on that rather
        // than on whether each field happens to be empty: a field left over from a previous run is
        // also non-empty, and testing emptiness would make stale values impossible to correct.
        if (serviceAnswered) return (true, Str("header_image"));

        g.Description = Clean(Str("short_description"));
        g.Developer = First("developers");
        g.Publisher = First("publishers");

        if (d.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            g.Genres = genres.EnumerateArray()
                .Select(x => x.TryGetProperty("description", out var n2) ? n2.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();

        if (d.TryGetProperty("release_date", out var rel)
            && rel.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String)
        {
            var text = date.GetString();
            g.ReleaseDate = string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return (true, Str("header_image"));
    }

    // ---------- Everything else ----------

    /// <summary>
    /// The shared service: IGDB for the facts, SteamGridDB for the art. Asked first, and asked by
    /// Steam app id whenever there is one, which is what makes asking it first safe -- an id
    /// lookup cannot come back with a different game the way a title search can.
    ///
    /// Either half may be absent, in which case that half is simply missing and Steam fills it.
    /// </summary>
    private async Task<(bool Touched, bool Facts)> EnrichElsewhereAsync(Game g,
        IFactsProvider? facts, IArtProvider? art, string? appId, HashSet<Slot> filled,
        CancellationToken ct)
    {
        var any = false;
        var gotFacts = false;

        if (facts is not null && !facts.Unavailable)
        {
            var hit = await facts.FindAsync(g.Title, appId, ct);
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
                gotFacts = true;

                // IGDB's art is the weakest of the three: its covers are portrait box art and its
                // artworks are wide key art, neither shaped like a tile. Written first so
                // SteamGridDB below, and Steam after that, can both improve on it.
                if (hit.CoverUrl is { } cover) any |= await StoreRemoteAsync(g, Slot.Cover, cover, filled, ct);
                if (hit.ArtworkUrl is { } wide)
                {
                    any |= await StoreRemoteAsync(g, Slot.Tile, wide, filled, ct);
                    any |= await StoreRemoteAsync(g, Slot.Hero, wide, filled, ct);
                }
            }
        }

        if (art is not null && !art.Unavailable)
        {
            var found = await art.FindArtAsync(g.Title, appId, ct);
            if (found is not null)
            {
                if (found.Portrait is { } p) any |= await StoreRemoteAsync(g, Slot.Cover, p, filled, ct);
                if (found.Tile is { } t) any |= await StoreRemoteAsync(g, Slot.Tile, t, filled, ct);
                if (found.Hero is { } h) any |= await StoreRemoteAsync(g, Slot.Hero, h, filled, ct);
                if (found.Logo is { } l) any |= await StoreRemoteAsync(g, Slot.Logo, l, filled, ct);
            }
        }

        return (any, gotFacts);
    }

    // ---------- Art plumbing ----------

    /// <summary>
    /// Downloads one picture into a slot, overwriting whatever was there. Callers run worst source
    /// first, so the last one to fill a slot wins it.
    /// </summary>
    private static async Task<bool> StoreRemoteAsync(Game g, Slot slot, string url,
        HashSet<Slot> filled, CancellationToken ct)
    {
        var name = ArtPrefix(g) + SuffixFor(slot, ExtensionOf(url));
        var dest = Path.Combine(Paths.CoversDir, name);
        if (!await DownloadAsync(url, dest, ct)) return false;
        // Assign returns false when the name has not changed, but the bytes on disk are new, so
        // the slot still counts as filled.
        Assign(g, slot, name);
        filled.Add(slot);
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
