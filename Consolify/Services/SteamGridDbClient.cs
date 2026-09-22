using System.Net.Http;
using System.Text.Json;

namespace Consolify.Services;

/// <summary>The art SteamGridDB has for one game. Any of these may be null.</summary>
public class SteamGridArt
{
    public string? Portrait { get; init; }  // 600x900, for portrait tiles
    public string? Tile { get; init; }      // 920x430 or 460x215, for landscape tiles
    public string? Hero { get; init; }      // ~1920x620, for backdrops
    public string? Logo { get; init; }      // transparent wordmark
}

/// <summary>
/// SteamGridDB: a community art database, and the reason a launcher can show a decent tile for a
/// game that was never on Steam. Art only -- no descriptions, no scores -- which is why it pairs
/// with IGDB rather than replacing it.
///
/// Needs a free API key from a SteamGridDB account. With no key configured this class is never
/// constructed.
/// </summary>
public class SteamGridDbClient : IArtProvider
{
    private const string Root = "https://www.steamgriddb.com/api/v2";
    private const int GapMs = 250;

    private readonly HttpClient _http;
    private readonly string _key;
    private DateTime _lastCall = DateTime.MinValue;

    public SteamGridDbClient(HttpClient http, string key)
    {
        _http = http;
        _key = key;
    }

    /// <summary>
    /// True once SteamGridDB has refused this key. A bad key fails identically for every request,
    /// so the first refusal stands for the whole pass rather than sending one doomed request per
    /// game per shape. A new pass builds a new client, so a corrected key is tried again.
    /// </summary>
    public bool KeyRejected { get; private set; }

    public bool Unavailable => KeyRejected;

    public static bool IsConfigured(string? key) => !string.IsNullOrWhiteSpace(key);

    /// <summary>
    /// Collects one image of each shape, or null if nothing matched confidently.
    ///
    /// By Steam app id when there is one: SteamGridDB indexes by it directly, so that lookup
    /// cannot come back with a different game and there is no matching to adjudicate at all. The
    /// title search is the fallback, for games SteamGridDB does not hold under that id and for
    /// everything that was never on Steam -- and what it returns still goes through TitleMatch.
    ///
    /// This is the same route the shared proxy takes. Without it, a user who supplied their own
    /// key got the weaker of the two paths for every game, which is backwards.
    /// </summary>
    public async Task<SteamGridArt?> FindArtAsync(string title, string? steamAppId, CancellationToken ct)
    {
        var id = steamAppId is null ? null : await ByAppIdAsync(steamAppId, ct);
        id ??= await SearchAsync(title, ct);
        if (id is null) return null;

        // Asked for in the order a tile wants them. Dimensions are a filter, not a promise: a game
        // with no art at a given size simply comes back empty, which is why each is independent.
        var portrait = await FirstUrlAsync($"{Root}/grids/game/{id}?dimensions=600x900", ct);
        var tile = await FirstUrlAsync($"{Root}/grids/game/{id}?dimensions=920x430,460x215", ct);
        var hero = await FirstUrlAsync($"{Root}/heroes/game/{id}", ct);
        var logo = await FirstUrlAsync($"{Root}/logos/game/{id}", ct);

        if (portrait is null && tile is null && hero is null && logo is null) return null;
        return new SteamGridArt { Portrait = portrait, Tile = tile, Hero = hero, Logo = logo };
    }

    /// <summary>SteamGridDB's own game id for a Steam app id, or null if it does not hold one.</summary>
    private async Task<int?> ByAppIdAsync(string steamAppId, CancellationToken ct)
    {
        var doc = await GetAsync($"{Root}/games/steam/{Uri.EscapeDataString(steamAppId)}", ct);
        if (doc is null) return null;
        using (doc)
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? JsonNum.Int(data, "id") : null;
    }

    private async Task<int?> SearchAsync(string title, CancellationToken ct)
    {
        var url = $"{Root}/search/autocomplete/{Uri.EscapeDataString(title)}";
        var doc = await GetAsync(url, ct);
        if (doc is null) return null;

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            if (!TitleMatch.TryBestMatch(title, data.EnumerateArray().ToList(),
                    e => e.TryGetProperty("name", out var n) ? n.GetString() : null, out var hit))
            {
                Log.Info($"SteamGridDB: no confident match for '{title}'");
                return null;
            }
            return JsonNum.Int(hit, "id");
        }
    }

    private async Task<string?> FirstUrlAsync(string url, CancellationToken ct)
    {
        var doc = await GetAsync(url, ct);
        if (doc is null) return null;
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    return u.GetString();
            return null;
        }
    }

    private async Task<JsonDocument?> GetAsync(string url, CancellationToken ct)
    {
        if (KeyRejected) return null;
        try
        {
            await ThrottleAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {_key}");
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                KeyRejected = true;
                Log.Info("SteamGridDB: the API key was rejected. Check it in Settings.");
                return null;
            }
            // 404 is the normal answer for "this game has no art of that shape", not a failure.
            if (!res.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            Log.Info($"SteamGridDB: {url} failed: {ex.Message}");
            return null;
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
