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

    /// <summary>
    /// What this build knows how to fetch. Bump it whenever a field is added or a picture starts
    /// being chosen differently, and every entry stamped with an older number is fetched again on
    /// the next pass.
    ///
    /// Without this a library only picks up a change after the freshness window runs out, which
    /// is a fortnight of the app knowing about PEGI ratings and never asking for one. Freshness is
    /// about not re-hitting the network for the same answer; it was never meant to pin a library
    /// to whatever the app happened to know the day it first scanned.
    /// </summary>
    private const int FetchVersion = 4;

    /// <summary>
    /// Which source wrote a file, as part of its name.
    ///
    /// This is the whole reason the capsule fix did not work the first time. A slot's file was
    /// named for the slot and the extension only -- "_hdtile" + ".jpg" -- so Steam's 616x353
    /// capsule and SteamGridDB's 920x430 grid, which is also served as .jpg, resolved to the
    /// SAME PATH. The service ran, overwrote the capsule in place, and every later pass found a
    /// file that was named like a capsule, skipped the download because it already existed, and
    /// pointed the tile at 2.14:1 art. Every tile in the library had a blurred mat under it and
    /// nothing in the code said why.
    ///
    /// With the source in the name the two can coexist on disk, and which one a game uses is
    /// decided by the code that runs rather than by whichever provider wrote last.
    /// </summary>
    private const string Steam = "_st", Service = "_sv";

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
    private enum Slot { Cover, Tile, Hero, Backdrop, Logo }

    // Steam serves all of these straight off its CDN, unauthenticated, for any app id, at their
    // original sizes rather than the half-size copies the client keeps on disk.
    //
    // capsule_616x353 is the one that matters for a landscape tile: it has the logo burnt in, so a
    // tile reads as the game at a glance from across a room. Not every app has one -- header.jpg
    // (2.14:1) is the fallback and always exists, and is why tiles must never be cover-cropped.
    //
    // The hero is taken at 2x. The backdrop element is inset -80px, so on a 1920x1080 stage it is
    // 2080x1240 -- and covering that from the 1920x620 hero meant a 2x upscale showing 54% of the
    // width, which is exactly the "zoomed in and blurry" it looked like. library_hero_2x is
    // 3840x1240: the same crop, but every pixel is now one pixel or better.

    /// <summary>
    /// Steam's official library assets, asked for BEFORE the service and given first refusal on
    /// their slots. Each is published at a fixed size that is exactly what the app's boxes are cut
    /// to, and "fixed size" is the whole point: a tile is 1.75:1 because capsule_616x353 is, and a
    /// hero is 3.1:1 because library_hero is.
    ///
    /// The service is still asked first for facts, and it still owns every slot Steam has nothing
    /// for. But its ART is community-uploaded and comes in whatever shape somebody made it --
    /// SteamGridDB's landscape grids are all 2.14:1, IGDB's artworks run from 0.75:1 to 3.1:1 --
    /// so letting it win a slot Steam publishes properly meant the library's tiles changed shape
    /// depending on which pass ran last. That is what "the artwork keeps changing" was.
    /// </summary>
    private static readonly (string Remote, Slot Slot, string Suffix)[] SteamPreferred =
    {
        ("capsule_616x353.jpg",    Slot.Tile,  Steam + "_cap"),    // 616x353, the landscape tile
        ("library_600x900_2x.jpg", Slot.Cover, Steam + "_cover"),  // 600x900, portrait box art
        ("library_hero_2x.jpg",    Slot.Hero,  Steam + "_hero2x"), // 3840x1240 -- see below
    };

    /// <summary>
    /// The rest of Steam's art, asked for after the service as the last fallback. These are the
    /// ones that are either the wrong shape (header.jpg is 2.14:1) or a lower-resolution copy of
    /// something above, so anything the service has beats them.
    /// </summary>
    private static readonly (string Remote, Slot Slot, string Suffix)[] SteamFallback =
    {
        ("header.jpg",       Slot.Tile, Steam + "_head"),   // 460x215, for apps with no capsule
        ("library_hero.jpg", Slot.Hero, Steam + "_hero"),   // 1920x620, the 1x hero
        ("logo.png",         Slot.Logo, Steam + "_logo"),   // transparent wordmark
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

                // A slot holding art the user chose by hand is already settled: counting it as
                // filled means nothing is downloaded for it at all, rather than fetched and then
                // discarded by the guard in Assign.
                var filled = CustomSlots(g);
                var touched = false;

                // One priority order for art, best source first, and the first to fill a slot
                // keeps it. Steam's own library assets lead because they are published at fixed
                // sizes that are exactly the shapes this app's boxes are cut to.
                if (appId is not null) touched |= await PreferSteamArtAsync(g, appId, filled, ct);

                // Then the service, for the slots Steam has nothing for -- a landscape tile for a
                // game with no capsule, a wordmark, 16:9 key art -- and for every non-Steam game,
                // where it is the only source there is. Facts come from here first regardless;
                // Steam still holds the Metacritic score and the controller-support flag.
                var (elsewhere, serviceFacts) = await EnrichElsewhereAsync(g, facts, art, appId, filled, ct);
                touched |= elsewhere;

                // Steam runs after, as the fallback: it fills every art slot and every field the
                // service left empty, and it always supplies controller support, which IGDB has
                // no equivalent of.
                if (appId is not null) touched |= await EnrichSteamAsync(g, appId, filled, serviceFacts, ct);

                // Stamped even when nothing was found, so a game that genuinely has no metadata is
                // not looked up again on every launch -- but NOT when every source that could have
                // answered was unavailable. Otherwise a typo'd key or an outage would mark the
                // library "tried" and fixing it would appear to do nothing for a fortnight.
                if (appId is not null || !AllUnavailable(facts, art))
                {
                    g.MetadataFetched = DateTime.UtcNow;
                    g.MetadataVersion = FetchVersion;
                }
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
        // Filled in by a build that fetched less than this one does, so the answers on file are
        // not wrong, just short. Checked before freshness on purpose: the window is there to stop
        // us asking the same question twice, not to stop us asking a new one.
        if (g.MetadataVersion != FetchVersion) return true;

        // Art can go missing on its own -- a cleared covers folder, a half-finished first run --
        // so a game inside the freshness window is still due if its files are not there. Nothing
        // is excluded up front any more: with the keyless Steam tiers there is always something
        // that might answer, and the sources themselves decide whether they can.
        if (g.MetadataFetched is { } at && DateTime.UtcNow - at < Freshness && HasFetchedArt(g)) return false;
        return true;
    }

    /// <summary>
    /// True when the tile is art this pass would not improve on: something downloaded, or
    /// something the user chose. Without the second half a hand-picked tile reads as "no fetched
    /// art" forever and puts its game back in the queue on every single start.
    /// </summary>
    private static bool HasFetchedArt(Game g) =>
        g.BannerFile is { } b && (b.Contains(Steam) || b.Contains(Service) || IsCustom(b))
        && File.Exists(Path.Combine(Paths.CoversDir, b));

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

    /// <summary>
    /// Steam's own art, before the service. A game that publishes none of it -- REANIMAL and
    /// Forza Horizon 6 both 404 for the capsule -- falls through untouched and the service fills
    /// the slot instead, which is what it is there for.
    /// </summary>
    private async Task<bool> PreferSteamArtAsync(Game g, string appId, HashSet<Slot> filled,
        CancellationToken ct)
    {
        var any = false;
        foreach (var entry in SteamPreferred)
        {
            if (ct.IsCancellationRequested) break;
            any |= await FetchSteamEntryAsync(g, appId, entry, filled, ct);
        }
        return any;
    }

    private async Task<bool> FetchSteamArtAsync(Game g, string appId, string? headerImage,
        HashSet<Slot> filled, CancellationToken ct)
    {
        var any = false;

        // Whatever is still empty after the preferred assets and the service. Listed best first,
        // and a slot already filled is skipped.
        foreach (var entry in SteamFallback)
        {
            if (ct.IsCancellationRequested) break;
            any |= await FetchSteamEntryAsync(g, appId, entry, filled, ct);
        }

        // Last resort for the tile, and the only art newer apps publish at all. Checked against
        // the game rather than a filename because the loop above may have written a tile from a
        // legacy path already, and that one is the better shape.
        if (!filled.Contains(Slot.Tile) && !string.IsNullOrWhiteSpace(headerImage))
            any |= await StoreRemoteAsync(g, Slot.Tile, headerImage, filled, ct, Steam);

        return any;
    }

    /// <summary>One entry off the CDN, skipped when its slot is already taken.</summary>
    private async Task<bool> FetchSteamEntryAsync(Game g, string appId,
        (string Remote, Slot Slot, string Suffix) entry, HashSet<Slot> filled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || filled.Contains(entry.Slot)) return false;

        var name = ArtPrefix(g) + entry.Suffix + Path.GetExtension(entry.Remote);
        var dest = Path.Combine(Paths.CoversDir, name);

        // Already have this exact asset, so nothing to fetch. The name says which remote file it
        // is AND which source wrote it, so unlike the old scheme this cannot be some other
        // provider's picture sitting under a name that claims to be Steam's.
        if (File.Exists(dest))
        {
            if (!Fits(entry.Slot, dest)) return false;
            filled.Add(entry.Slot);
            return Assign(g, entry.Slot, name);
        }

        var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{entry.Remote}";
        if (!await DownloadAsync(url, dest, ct)) return false;
        if (!Fits(entry.Slot, dest))
        {
            try { File.Delete(dest); } catch { }
            return false;
        }

        filled.Add(entry.Slot);
        return Assign(g, entry.Slot, name);
    }

    /// <summary>Facts, plus the header image URL appdetails names -- the art step needs it as a
    /// fallback for apps that no longer publish to the legacy CDN paths.</summary>
    private async Task<(bool Ok, string? HeaderImage)> FetchSteamFactsAsync(Game g, string appId,
        bool serviceAnswered, CancellationToken ct)
    {
        // "ratings" is the one that carries the age boards, and it has to be asked for by name --
        // the filter list is exhaustive, so leaving it out drops the whole block silently.
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english" +
                  "&filters=basic,genres,metacritic,release_date,developers,publishers," +
                  "controller_support,ratings";

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

        // Steam carries the age boards itself, PEGI among them, keyed by app id and with no key
        // and no title matching -- which makes it a better source for this than IGDB ever was.
        // Read before the early return below, for the same reason as the two above: it is worth
        // having whether or not the service answered.
        if (g.PegiRating is null && SteamPegi(d) is { } pegi) g.PegiRating = pegi;

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

    /// <summary>
    /// The PEGI age out of Steam's own ratings block, or null.
    ///
    /// `ratings` holds one entry per board -- esrb, pegi, usk, cero, oflc and a dozen more -- so
    /// the board is named rather than guessed at, and only PEGI's own ages are accepted. The
    /// rating arrives as a STRING ("18"), and a few boards put letters there ("m", "r18", "z"),
    /// so anything that is not one of PEGI's five numbers is not a PEGI age and is dropped.
    ///
    /// Plenty of games have no entry at all: a European rating only exists if somebody paid for
    /// one, which most indies have not. That is a fact about the game, not a failure here.
    /// </summary>
    private static int? SteamPegi(JsonElement data)
    {
        if (!data.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Object)
            return null;
        if (!ratings.TryGetProperty("pegi", out var pegi) || pegi.ValueKind != JsonValueKind.Object)
            return null;
        if (!pegi.TryGetProperty("rating", out var r) || r.ValueKind != JsonValueKind.String)
            return null;

        return int.TryParse(r.GetString(), out var age) && age is 3 or 7 or 12 or 16 or 18
            ? age : null;
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
        IgdbGame? factsHit = null;

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
                g.PegiRating = hit.PegiRating;
                // Named for what it is. IGDB aggregates external reviews itself, so calling this
                // Metacritic would put one publication's name on another's number.
                g.CriticSource = hit.CriticScore is null ? null : "IGDB critics";
                g.MetadataSource = "igdb";
                factsHit = hit;
                any = true;
                gotFacts = true;
            }
        }

        // The art provider before IGDB's own pictures, because every source is now first-wins and
        // the order therefore has to run best to worst. SteamGridDB publishes art in the shapes a
        // launcher asks for; IGDB's cover is an afterthought and its artworks are whatever was
        // uploaded, which is why they are last.
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

        if (factsHit is { } igdb)
        {
            if (igdb.CoverUrl is { } cover) any |= await StoreRemoteAsync(g, Slot.Cover, cover, filled, ct);
            if (igdb.ArtworkUrl is { } wide)
            {
                // Only when there is no hero. Fits can tell that an artwork is 16:9 and cannot
                // tell that it is any good, and IGDB's artworks are user uploads in no particular
                // order -- Persona 3's first one is a blue diagonal, two floating leaves and 31 KB
                // of JPEG, against 873 KB of key art in Steam's hero. Steam's library_hero is
                // curated and is the picture the store itself shows, so it wins whenever it
                // exists; this slot is for the games that have nothing else.
                if (!filled.Contains(Slot.Hero))
                    any |= await StoreRemoteAsync(g, Slot.Backdrop, wide, filled, ct);
                // And as the tile of last resort, for a game with no capsule and nothing from
                // SteamGridDB. Fits keeps anything squarer than 1.3:1 out of a landscape box.
                any |= await StoreRemoteAsync(g, Slot.Tile, wide, filled, ct);
            }
        }

        return (any, gotFacts);
    }

    // ---------- Art plumbing ----------

    /// <summary>
    /// Downloads one picture into a slot, unless that slot is already settled. Refused, too, if
    /// the picture turns out to be the wrong shape for it -- see Fits.
    ///
    /// The `filled` check is not a detail. Without it this method wrote its slot unconditionally
    /// while only the Steam CDN loop consulted the set, so "Steam's capsule gets first refusal"
    /// was true right up until the service ran two lines later and overwrote it. Every caller now
    /// goes through the same gate, in one priority order, and the first source to fill a slot
    /// keeps it -- which is also what makes the art stop changing between passes.
    /// </summary>
    private static async Task<bool> StoreRemoteAsync(Game g, Slot slot, string url,
        HashSet<Slot> filled, CancellationToken ct, string tag = Service)
    {
        if (filled.Contains(slot)) return false;

        var name = ArtPrefix(g) + tag + SlotSuffix(slot) + ExtensionOf(url);
        var dest = Path.Combine(Paths.CoversDir, name);
        if (!await DownloadAsync(url, dest, ct)) return false;

        if (!Fits(slot, dest))
        {
            try { File.Delete(dest); } catch { /* a cache file; leaving it costs nothing */ }
            Unassign(g, slot, name);
            return false;
        }

        // Assign returns false when the name has not changed, but the bytes on disk are new, so
        // the slot still counts as filled.
        Assign(g, slot, name);
        filled.Add(slot);
        return true;
    }

    private static string SlotSuffix(Slot slot) => slot switch
    {
        Slot.Tile => "_tile",
        Slot.Hero => "_hero",
        Slot.Backdrop => "_bg",
        Slot.Logo => "_logo",
        _ => "_cover",
    };

    /// <summary>
    /// The shapes a slot will accept, as (min, max) aspect.
    ///
    /// Written because IGDB's artworks are whatever somebody uploaded: taking the first of them
    /// for the backdrop put a 1080x1080 square behind DREDGE's whole screen and an 810x1080
    /// portrait behind Hollow Knight's. The slot is defined as 16:9 key art and the launcher was
    /// treating it as "a wide picture, probably". A square is not key art, and the fix is to say
    /// so here rather than to add another fallback in the page.
    ///
    /// Generous at the edges on purpose. These reject art that is the wrong KIND of picture, not
    /// art that is a few percent off -- a 2:1 promotional still is still a backdrop.
    /// </summary>
    private static (double Min, double Max) Bounds(Slot slot) => slot switch
    {
        Slot.Cover => (0.0, 0.95),      // portrait box art; a landscape one is somebody else's slot
        Slot.Tile => (1.30, 2.60),      // 1.75 capsule through 2.14 header, and nothing squarer
        Slot.Hero => (2.40, 5.00),      // the 3.1:1 band
        Slot.Backdrop => (1.60, 2.10),  // 16:9 key art, which is the only thing this slot is for
        _ => (0.0, double.MaxValue),    // a wordmark is whatever shape the wordmark is
    };

    /// <summary>
    /// True when the file is a shape this slot can use. Art we cannot measure is accepted: a
    /// format we do not decode is not evidence of a bad picture, and refusing it would throw away
    /// every .webp SteamGridDB serves.
    /// </summary>
    private static bool Fits(Slot slot, string path)
    {
        if (ImageAspect(path) is not { } aspect) return true;
        var (min, max) = Bounds(slot);
        if (aspect >= min && aspect <= max) return true;
        Log.Info($"Metadata: {Path.GetFileName(path)} is {aspect:0.00}:1, " +
                 $"which is not a {slot} ({min:0.00}-{max:0.00}); discarded");
        return false;
    }

    /// <summary>Width over height, or null when the file cannot be decoded here.</summary>
    private static double? ImageAspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            return frame.PixelHeight > 0 ? (double)frame.PixelWidth / frame.PixelHeight : null;
        }
        catch { return null; }
    }

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

    /// <summary>
    /// Points the game at art we just wrote. Returns true when it actually changed.
    ///
    /// Art the user picked by hand outranks anything we can download, in every slot -- the cover
    /// was the only one that could be picked when this was written, and the guard was on that one
    /// alone. Now that a tile can be chosen too, an enrich that moved it back would make the
    /// option look like it had not worked.
    /// </summary>
    private static bool Assign(Game g, Slot slot, string name)
    {
        switch (slot)
        {
            case Slot.Cover when g.CoverFile != name && !IsCustom(g.CoverFile):
                g.CoverFile = name; return true;
            case Slot.Tile when g.BannerFile != name && !IsCustom(g.BannerFile):
                g.BannerFile = name; return true;
            case Slot.Hero when g.HeroFile != name && !IsCustom(g.HeroFile):
                g.HeroFile = name; return true;
            case Slot.Backdrop when g.BackdropFile != name && !IsCustom(g.BackdropFile):
                g.BackdropFile = name; return true;
            case Slot.Logo when g.LogoFile != name && !IsCustom(g.LogoFile):
                g.LogoFile = name; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Let go of a file we just deleted, if this game was pointing at it.
    ///
    /// A rejected download is removed from disk, and a previous pass may already have written its
    /// name into the game -- the same source, the same slot, the same filename, accepted back when
    /// nothing checked the shape. Left alone that is a library entry naming a file that is not
    /// there, which renders as no picture at all and survives every refresh, because a name only
    /// ever gets replaced by a download that succeeds.
    /// </summary>
    private static void Unassign(Game g, Slot slot, string name)
    {
        switch (slot)
        {
            case Slot.Cover when g.CoverFile == name: g.CoverFile = null; break;
            case Slot.Tile when g.BannerFile == name: g.BannerFile = null; break;
            case Slot.Hero when g.HeroFile == name: g.HeroFile = null; break;
            case Slot.Backdrop when g.BackdropFile == name: g.BackdropFile = null; break;
            case Slot.Logo when g.LogoFile == name: g.LogoFile = null; break;
        }
    }

    private static bool IsCustom(string? file) =>
        file is not null && file.StartsWith("custom_", StringComparison.Ordinal);

    /// <summary>The slots this game already has hand-picked art in.</summary>
    private static HashSet<Slot> CustomSlots(Game g)
    {
        var set = new HashSet<Slot>();
        if (IsCustom(g.CoverFile)) set.Add(Slot.Cover);
        if (IsCustom(g.BannerFile)) set.Add(Slot.Tile);
        if (IsCustom(g.HeroFile)) set.Add(Slot.Hero);
        if (IsCustom(g.BackdropFile)) set.Add(Slot.Backdrop);
        if (IsCustom(g.LogoFile)) set.Add(Slot.Logo);
        return set;
    }

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
