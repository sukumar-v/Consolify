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
/// Steam only, deliberately. Its store endpoint needs no key, no account and no registration, and
/// it answers for any app id -- which the scanner already has, so there is no name matching and
/// therefore no chance of attaching the wrong game's art to an entry. Every other provider worth
/// having (IGDB, SteamGridDB) needs credentials the user has to go and create, and matching by
/// title, so they are a separate decision rather than something to switch on quietly.
///
/// Nothing here is required. Every field it writes is cosmetic, every failure is swallowed, and a
/// machine with no network simply keeps the art the scanner copied out of Steam's local cache.
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

    /// <summary>
    /// Steam serves these straight off its CDN, unauthenticated, for any app id. The sizes are the
    /// originals rather than the half-size copies the client keeps on disk.
    ///
    /// capsule_616x353 is the one that matters for a landscape tile: 1.75:1 is near enough 16:9 to
    /// crop invisibly, and it has the logo burnt in, so a tile reads as the game at a glance from
    /// across a room. Not every app has one -- header.jpg (2.14:1) is the fallback and always
    /// exists.
    /// </summary>
    private static readonly (string Remote, string Suffix, bool Required)[] SteamArt =
    {
        ("library_600x900_2x.jpg", "_hd.jpg",     false), // the full-size portrait, 600x900 up
        ("capsule_616x353.jpg",    "_hdtile.jpg", false), // 616x353, the landscape tile
        ("header.jpg",             "_hdtile.jpg", false), // fallback for the above
        ("library_hero.jpg",       "_hdhero.jpg", false), // 1920x620 backdrop
        ("logo.png",               "_hdlogo.png", false), // transparent wordmark
    };

    /// <summary>
    /// Brings every game that needs it up to date, in place. Returns the number actually touched
    /// so the caller can skip a UI push when there was nothing to do.
    /// </summary>
    public async Task<int> EnrichAsync(IReadOnlyList<Game> games, CancellationToken ct = default)
    {
        var due = games.Where(NeedsFetch).ToList();
        if (due.Count == 0) return 0;

        Log.Info($"Metadata: {due.Count} game(s) to fetch");
        var changed = 0;
        var hitNetwork = false;

        foreach (var g in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Space out only the store calls, and only between them -- the first game should
                // not sit waiting, and a run that fetches nothing but art should not crawl.
                if (hitNetwork) await Task.Delay(StoreGapMs, ct);
                hitNetwork = true;

                if (await EnrichSteamAsync(g, ct)) changed++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Leave MetadataFetched null so a later run tries again rather than treating a
                // dropped connection as "this game has no metadata".
                Log.Info($"Metadata: {g.Title} failed: {ex.Message}");
            }
        }

        Log.Info($"Metadata: updated {changed} game(s)");
        return changed;
    }

    private static bool NeedsFetch(Game g)
    {
        if (SteamAppId(g) is null) return false;
        // Art can go missing on its own -- a cleared covers folder, a half-finished first run --
        // so a game inside the freshness window is still due if its files are not there.
        if (g.MetadataFetched is { } at && DateTime.UtcNow - at < Freshness && HasHdArt(g)) return false;
        return true;
    }

    private static bool HasHdArt(Game g) =>
        g.BannerFile is { } b && File.Exists(Path.Combine(Paths.CoversDir, b)) && b.Contains("_hd");

    private static string? SteamAppId(Game g) =>
        g.Id.StartsWith("steam:", StringComparison.Ordinal) && g.Id.Length > 6 ? g.Id[6..] : null;

    private async Task<bool> EnrichSteamAsync(Game g, CancellationToken ct)
    {
        var appId = SteamAppId(g);
        if (appId is null) return false;

        var gotArt = await FetchSteamArtAsync(g, appId, ct);
        var gotFacts = await FetchSteamFactsAsync(g, appId, ct);

        // Stamped even when the store had nothing to say, so a game that genuinely has no
        // metadata is not re-fetched every fortnight... but only if we got that far without
        // throwing, which is what separates "no data" from "no network".
        g.MetadataFetched = DateTime.UtcNow;
        if (gotFacts) g.MetadataSource = "steam";
        return gotArt || gotFacts;
    }

    private async Task<bool> FetchSteamArtAsync(Game g, string appId, CancellationToken ct)
    {
        var any = false;
        foreach (var (remote, suffix, _) in SteamArt)
        {
            if (ct.IsCancellationRequested) break;
            var name = $"steam_{appId}{suffix}";
            var dest = Path.Combine(Paths.CoversDir, name);

            // header.jpg shares a suffix with the capsule and is only there to cover the apps that
            // have no capsule, so it must not overwrite one we already have.
            if (File.Exists(dest)) { any |= Assign(g, suffix, name); continue; }

            var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{remote}";
            if (!await DownloadAsync(url, dest, ct)) continue;
            any |= Assign(g, suffix, name);
        }
        return any;
    }

    /// <summary>Points the game at art we just wrote. Returns true when it actually changed.</summary>
    private static bool Assign(Game g, string suffix, string name)
    {
        switch (suffix)
        {
            // A cover the user picked by hand outranks anything we can download.
            case "_hd.jpg" when g.CoverFile != name && !IsCustom(g.CoverFile): g.CoverFile = name; return true;
            case "_hdtile.jpg" when g.BannerFile != name: g.BannerFile = name; return true;
            case "_hdhero.jpg" when g.HeroFile != name: g.HeroFile = name; return true;
            case "_hdlogo.png" when g.LogoFile != name: g.LogoFile = name; return true;
            default: return false;
        }
    }

    private static bool IsCustom(string? file) => file is not null && file.StartsWith("custom_", StringComparison.Ordinal);

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
