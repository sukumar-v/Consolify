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
    public string? CoverImageId { get; init; }
    public string? ArtworkImageId { get; init; }
}

/// <summary>
/// IGDB, which is where Playnite gets its metadata too. Free, but not keyless: it lives behind
/// Twitch's developer programme, so the user registers an application and gives us its client id
/// and secret. Free for non-commercial use under the Twitch Developer Service Agreement.
///
/// Everything is opt-in on those credentials being present. With no keys configured this class is
/// never constructed and the launcher behaves exactly as it did before.
/// </summary>
public class IgdbClient
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

    public static bool IsConfigured(string? id, string? secret) =>
        !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret);

    /// <summary>
    /// Art URL for one of IGDB's image ids. "t_cover_big_2x" is 528x748 -- the largest portrait
    /// they serve -- and "t_1080p" is the full-width artwork used for backdrops.
    /// </summary>
    public static string ImageUrl(string imageId, string size) =>
        $"https://images.igdb.com/igdb/image/upload/t_{size}/{imageId}.jpg";

    /// <summary>
    /// Searches by title and returns only a confidently matching game, or null. The confidence
    /// rule lives in TitleMatch and is deliberately unforgiving -- see the note there about why a
    /// near miss is worse than nothing.
    /// </summary>
    public async Task<IgdbGame?> FindAsync(string title, CancellationToken ct)
    {
        if (!await EnsureTokenAsync(ct)) return null;

        // APIcalypse. The quotes around the search term are part of the syntax, so a title
        // containing one has to lose it or the whole query is rejected.
        var term = title.Replace("\"", " ").Trim();
        var body =
            $"search \"{term}\"; " +
            "fields name, summary, first_release_date, aggregated_rating, category, " +
            "genres.name, cover.image_id, artworks.image_id, " +
            "involved_companies.developer, involved_companies.publisher, involved_companies.company.name; " +
            "limit 20;";

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        req.Headers.Add("Client-ID", _clientId);
        req.Headers.Add("Authorization", $"Bearer {_token}");

        await ThrottleAsync(ct);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            Log.Info($"IGDB: search for '{title}' returned {(int)res.StatusCode}");
            return null;
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        // category 0 is a main game; the rest are DLC, bundles, episodes and ports, which share
        // their parent's title and would otherwise win the match on a coin toss.
        var games = doc.RootElement.EnumerateArray()
            .Where(e => !e.TryGetProperty("category", out var c) || !c.TryGetInt32(out var n) || n == 0)
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

    private static IgdbGame Parse(JsonElement e)
    {
        string? Str(string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        DateTime? released = null;
        if (e.TryGetProperty("first_release_date", out var rd) && rd.TryGetInt64(out var unix))
            released = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        int? score = null;
        if (e.TryGetProperty("aggregated_rating", out var ar) && ar.TryGetDouble(out var rating))
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
            CoverImageId = Image("cover"),
            ArtworkImageId = firstArtwork,
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

            var seconds = doc.RootElement.TryGetProperty("expires_in", out var ex) && ex.TryGetInt64(out var s)
                ? s : 3600;
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
