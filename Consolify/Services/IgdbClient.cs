using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Consolify.Services;

/// <summary>Facts and art for one game, as IGDB knows it. Any field may be null.</summary>
public class IgdbGame
{
    public string Name { get; init; } = "";
    public string? Summary { get; init; }
    public string? Developer { get; init; }
    public string? Publisher { get; init; }
    public List<string> Genres { get; init; } = new();
    public DateTime? Released { get; init; }
    /// <summary>IGDB's aggregate of external critic scores, 0-100. Not Metacritic, and must not
    /// be labelled as such.</summary>
    public int? CriticScore { get; init; }
    /// <summary>PEGI age -- 3, 7, 12, 16 or 18 -- or null when the game carries no PEGI rating.</summary>
    public int? PegiRating { get; init; }
    /// <summary>Finished URLs rather than IGDB image ids, so a proxy that has already resolved
    /// them and a direct call can hand back the same object.</summary>
    public string? CoverUrl { get; init; }
    public string? ArtworkUrl { get; init; }
}

/// <summary>
/// IGDB, which is where Playnite gets its metadata too. Free, but not keyless: it lives behind
/// Twitch's developer programme, so the user registers an application and gives us its client id
/// and secret. Free for non-commercial use under the Twitch Developer Service Agreement.
///
/// Everything is opt-in on those credentials being present. With no keys configured this class is
/// never constructed and the launcher behaves exactly as it did before.
/// </summary>
public class IgdbClient : IFactsProvider
{
    // IGDB allows 4 requests a second. One at a time with a gap well inside that is plenty for a
    // library scan, and means a big library cannot trip the limit even with art requests mixed in.
    private const int GapMs = 300;

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;

    private string? _token;
    private DateTime _tokenExpires = DateTime.MinValue;
    private DateTime _lastCall = DateTime.MinValue;

    public IgdbClient(HttpClient http, string clientId, string clientSecret)
    {
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    /// <summary>
    /// True once Twitch has refused these credentials. Bad keys fail identically for every game,
    /// so the first refusal stands for the whole pass: without this, a 200-game library sends 200
    /// doomed auth requests and writes 200 identical lines into the log. A new pass builds a new
    /// client, so fixing the keys and rescanning tries again.
    /// </summary>
    public bool CredentialsRejected { get; private set; }

    public bool Unavailable => CredentialsRejected;

    public static bool IsConfigured(string? id, string? secret) =>
        !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret);

    /// <summary>
    /// Art URL for one of IGDB's image ids. "t_cover_big_2x" is 528x748 -- the largest portrait
    /// they serve -- and "t_1080p" is the full-width artwork used for backdrops.
    /// </summary>
    public static string ImageUrl(string imageId, string size) =>
        $"https://images.igdb.com/igdb/image/upload/t_{size}/{imageId}.jpg";

    /// <summary>
    /// The game, or null.
    ///
    /// By Steam app id first, which IGDB records in external_games (category 1 is Steam). That is
    /// an exact lookup with no title in it, so it cannot answer with a different game -- the same
    /// guarantee Steam's own endpoints give, and the reason it is safe to ask this source before
    /// Steam at all. The shared proxy has always worked this way; this client did not, so a user
    /// who supplied their own credentials was quietly getting the weaker path.
    ///
    /// Falling back to a title search for anything IGDB does not index under that id, and for
    /// everything that was never on Steam. That result goes through TitleMatch, whose confidence
    /// rule is deliberately unforgiving -- see the note there about why a near miss is worse than
    /// nothing at all.
    /// </summary>
    public async Task<IgdbGame?> FindAsync(string title, string? steamAppId, CancellationToken ct)
    {
        if (!await EnsureTokenAsync(ct)) return null;

        if (steamAppId is not null && await ByAppIdAsync(steamAppId, ct) is { } byId) return byId;

        // APIcalypse. The quotes around the search term are part of the syntax, so a title
        // containing one has to lose it or the whole query is rejected.
        var term = title.Replace("\"", " ").Trim();

        using var doc = await SearchAsync(term, title, ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        // category 0 is a main game; the rest are DLC, bundles, episodes and ports, which share
        // their parent's title and would otherwise win the match on a coin toss. version_parent
        // marks an edition or regional variant, which inherits its parent's title exactly.
        //
        // Ordered by popularity because an exact-title tie is real and not harmless: IGDB carries
        // two entries named exactly "DOOM" (1993 and 2016) and more than one named "Fortnite".
        // TitleMatch cannot separate those -- the names are identical -- so the order they arrive
        // in decides, and most-followed is the canonical one every time.
        var games = doc.RootElement.EnumerateArray()
            .Where(e => JsonNum.Int(e, "category") is null or 0)
            .Where(e => !e.TryGetProperty("version_parent", out _))
            .OrderByDescending(Popularity)
            .ToList();
        if (games.Count == 0) return null;

        if (!TitleMatch.TryBestMatch(title, games,
                e => e.TryGetProperty("name", out var n) ? n.GetString() : null, out var hit))
        {
            Log.Info($"IGDB: no confident match for '{title}'");
            return null;
        }

        return Parse(hit);
    }

    private const string BaseFields =
        "name, summary, first_release_date, aggregated_rating, category, " +
        "follows, total_rating_count, version_parent, " +
        "genres.name, cover.image_id, artworks.image_id, " +
        "involved_companies.developer, involved_companies.publisher, involved_companies.company.name";

    /// <summary>
    /// Age-rating fields, in the shapes IGDB has used, newest first.
    ///
    /// IGDB moved these from numeric enums (category/rating) to references
    /// (organization/rating_category), and APIcalypse fails the WHOLE query with a 400 for one
    /// unknown field -- so guessing wrong would cost the description and the score too, not just
    /// the rating. The shapes are tried in order, a 400 steps down, and the one that worked is
    /// kept for the life of this client. The last asks for no age fields and always works.
    /// </summary>
    private static readonly string[] AgeShapes =
    {
        ", age_ratings.organization.name, age_ratings.rating_category.rating",
        ", age_ratings.category, age_ratings.rating",
        "",
    };
    private static int _ageShape;

    /// <summary>
    /// The game IGDB files under this Steam app id, or null. external_games holds a game's
    /// storefront ids and category 1 is Steam, so this is a direct lookup rather than a search.
    ///
    /// Several rows can come back -- an edition or a regional variant carries its parent's ids --
    /// so version_parent entries are dropped and the most followed of what is left wins, exactly
    /// as in the title path.
    /// </summary>
    private async Task<IgdbGame?> ByAppIdAsync(string steamAppId, CancellationToken ct)
    {
        // Straight into an APIcalypse string literal, so anything but digits is refused rather
        // than escaped. Every id we hold is numeric; one that is not is a bug, not a query.
        if (!steamAppId.All(char.IsAsciiDigit)) return null;

        using var doc = await QueryAsync(
            $"where external_games.category = 1 & external_games.uid = \"{steamAppId}\";", 5,
            $"app {steamAppId}", ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var hit = doc.RootElement.EnumerateArray()
            .Where(e => !e.TryGetProperty("version_parent", out _))
            .OrderByDescending(Popularity)
            .Select(e => (JsonElement?)e)
            .FirstOrDefault();
        return hit is null ? null : Parse(hit.Value);
    }

    private Task<JsonDocument?> SearchAsync(string term, string title, CancellationToken ct) =>
        QueryAsync($"search \"{term}\";", 20, $"'{title}'", ct);

    /// <summary>One query, retried down the age-rating shapes when IGDB rejects a field name.</summary>
    private async Task<JsonDocument?> QueryAsync(string clause, int limit, string label,
        CancellationToken ct)
    {
        for (var i = _ageShape; i < AgeShapes.Length; i++)
        {
            var body = $"{clause} fields {BaseFields}{AgeShapes[i]}; limit {limit};";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            };
            req.Headers.Add("Client-ID", _clientId);
            req.Headers.Add("Authorization", $"Bearer {_token}");

            await ThrottleAsync(ct);
            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                if (i != _ageShape)
                {
                    Log.Info($"IGDB: age-rating fields fell back to shape {i}");
                    _ageShape = i;
                }
                return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            }

            // 400 is "I do not know that field", the one failure another shape can fix.
            if (res.StatusCode != System.Net.HttpStatusCode.BadRequest)
            {
                Log.Info($"IGDB: query for {label} returned {(int)res.StatusCode}");
                return null;
            }
        }
        Log.Info($"IGDB: query for {label} was rejected in every field shape");
        return null;
    }

    /// <summary>
    /// The PEGI age -- 3, 7, 12, 16 or 18 -- or null. A game can carry ratings from half a dozen
    /// boards, so the board has to be identified before the number means anything: an ESRB "M" and
    /// a PEGI "16" sit in the same list, and their enums overlap.
    /// </summary>
    private static int? Pegi(JsonElement e)
    {
        if (!e.TryGetProperty("age_ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var r in ratings.EnumerateArray())
        {
            // Modern: named, so it survives IGDB renumbering its enums.
            var org = r.TryGetProperty("organization", out var o) && o.TryGetProperty("name", out var on)
                ? on.GetString() : null;
            if (org is not null)
            {
                if (!org.Contains("PEGI", StringComparison.OrdinalIgnoreCase)) continue;
                var name = r.TryGetProperty("rating_category", out var rc) && rc.TryGetProperty("rating", out var rn)
                    ? rn.GetString() : null;
                var age = name?.ToLowerInvariant() switch
                {
                    "three" => 3, "seven" => 7, "twelve" => 12, "sixteen" => 16, "eighteen" => 18,
                    _ => (int?)null,
                };
                if (age is not null) return age;
            }
            // Legacy: category 2 is PEGI, and ratings 1..5 are Three, Seven, Twelve, Sixteen, Eighteen.
            else if (JsonNum.Int(r, "category") == 2)
            {
                var age = JsonNum.Int(r, "rating") switch
                {
                    1 => 3, 2 => 7, 3 => 12, 4 => 16, 5 => 18, _ => (int?)null,
                };
                if (age is not null) return age;
            }
        }
        return null;
    }

    /// <summary>How well known an entry is, used only to break an exact-title tie. Follows are the
    /// stronger signal, so they outweigh rating counts rather than being added to them.</summary>
    private static long Popularity(JsonElement e)
    {
        long Get(string key) =>
            JsonNum.Long(e, key) ?? 0;
        return Get("follows") * 10 + Get("total_rating_count");
    }

    private static IgdbGame Parse(JsonElement e)
    {
        string? Str(string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        DateTime? released = null;
        if (JsonNum.Long(e, "first_release_date") is { } unix)
            released = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        int? score = null;
        if (JsonNum.Double(e, "aggregated_rating") is { } rating)
            score = (int)Math.Round(rating);

        var genres = new List<string>();
        if (e.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
            genres = gs.EnumerateArray()
                .Select(g => g.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();

        // A company can be credited as developer, publisher or both, so each flag is read on its
        // own rather than assuming the first entry is the developer.
        string? developer = null, publisher = null;
        if (e.TryGetProperty("involved_companies", out var ics) && ics.ValueKind == JsonValueKind.Array)
            foreach (var ic in ics.EnumerateArray())
            {
                var name = ic.TryGetProperty("company", out var co) && co.TryGetProperty("name", out var cn)
                    ? cn.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (developer is null && ic.TryGetProperty("developer", out var d) && d.ValueKind == JsonValueKind.True)
                    developer = name;
                if (publisher is null && ic.TryGetProperty("publisher", out var p) && p.ValueKind == JsonValueKind.True)
                    publisher = name;
            }

        string? Image(string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("image_id", out var id) ? id.GetString() : null;

        string? firstArtwork = null;
        if (e.TryGetProperty("artworks", out var aws) && aws.ValueKind == JsonValueKind.Array)
            firstArtwork = aws.EnumerateArray()
                .Select(a => a.TryGetProperty("image_id", out var id) ? id.GetString() : null)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        return new IgdbGame
        {
            Name = Str("name") ?? "",
            Summary = Str("summary"),
            Developer = developer,
            Publisher = publisher,
            Genres = genres,
            Released = released,
            CriticScore = score,
            PegiRating = Pegi(e),
            CoverUrl = Image("cover") is { } c ? ImageUrl(c, "cover_big_2x") : null,
            ArtworkUrl = firstArtwork is { } a ? ImageUrl(a, "1080p") : null,
        };
    }

    /// <summary>
    /// Client-credentials token from Twitch. Good for about two months, but it is cached against
    /// its own stated lifetime rather than a guess, and re-requested a minute early.
    /// </summary>
    private async Task<bool> EnsureTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpires) return true;
        if (CredentialsRejected) return false;

        var url = "https://id.twitch.tv/oauth2/token" +
                  $"?client_id={Uri.EscapeDataString(_clientId)}" +
                  $"&client_secret={Uri.EscapeDataString(_clientSecret)}" +
                  "&grant_type=client_credentials";
        try
        {
            using var res = await _http.PostAsync(url, null, ct);
            if (!res.IsSuccessStatusCode)
            {
                // The overwhelmingly likely cause is a typo in the id or secret, and it will fail
                // identically for every game, so say so once and clearly.
                CredentialsRejected = true;
                Log.Info($"IGDB: Twitch rejected the credentials ({(int)res.StatusCode}). " +
                         "Check the client id and secret in Settings.");
                return false;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(token)) return false;

            var seconds = JsonNum.Long(doc.RootElement, "expires_in") ?? 3600;
            _token = token;
            _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60));
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"IGDB: token request failed: {ex.Message}");
            return false;
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - _lastCall;
        var wait = GapMs - (int)since.TotalMilliseconds;
        if (wait > 0) await Task.Delay(wait, ct);
        _lastCall = DateTime.UtcNow;
    }
}
