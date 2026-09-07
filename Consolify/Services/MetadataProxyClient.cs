using System.Net.Http;
using System.Text.Json;

namespace Consolify.Services;

/// <summary>Somewhere facts can come from: our proxy, or the user's own IGDB credentials.</summary>
public interface IFactsProvider
{
    Task<IgdbGame?> FindAsync(string title, CancellationToken ct);
    /// <summary>True once this source has failed in a way that will repeat for every game, so the
    /// pass can stop asking rather than failing once per title.</summary>
    bool Unavailable { get; }
}

/// <summary>Somewhere art can come from: our proxy, or the user's own SteamGridDB key.</summary>
public interface IArtProvider
{
    Task<SteamGridArt?> FindArtAsync(string title, CancellationToken ct);
    bool Unavailable { get; }
}

/// <summary>
/// The default source for games that are not on Steam, and the reason installing Consolify does
/// not come with a signup form. The credentials live on the proxy; this end holds nothing.
///
/// The same arrangement Playnite uses -- its IGDB plugin ships no keys and points at
/// api2.playnite.link -- and for the same reason: a shipped binary cannot keep a secret.
///
/// Note that the strict title check still happens *here*, on every response, even though the proxy
/// applies one of its own. The proxy is a convenience, not an authority: if it is ever wrong,
/// stale or replaced, it still cannot put another game's art on a tile.
/// </summary>
public class MetadataProxyClient : IFactsProvider, IArtProvider
{
    /// <summary>
    /// Where a shipped build looks, and the reason installing Consolify comes with no setup. Set
    /// to empty to turn the proxy tier off entirely; a user can override it in Settings, and their
    /// own credentials take priority over it either way. See proxy/README.md.
    /// </summary>
    public const string DefaultEndpoint = "https://consolify-metadata.s-varmagt.workers.dev";

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public bool Unavailable { get; private set; }

    public MetadataProxyClient(HttpClient http, string endpoint)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
    }

    public static bool IsConfigured(string? endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint)
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    public async Task<IgdbGame?> FindAsync(string title, CancellationToken ct)
    {
        var d = await GetAsync("facts", title, ct);
        if (d is null) return null;

        using (d)
        {
            var root = d.RootElement;
            var name = Str(root, "name");
            // The proxy said this was the game. We do not take its word for it.
            if (!TitleMatch.IsConfident(title, name))
            {
                Log.Info($"Proxy: returned '{name}' for '{title}', which is not a confident match");
                return null;
            }

            DateTime? released = null;
            if (Str(root, "released") is { } iso && DateTime.TryParse(iso, out var parsed)) released = parsed;

            var genres = new List<string>();
            if (root.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
                genres = gs.EnumerateArray().Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();

            return new IgdbGame
            {
                Name = name ?? "",
                Summary = Str(root, "summary"),
                Developer = Str(root, "developer"),
                Publisher = Str(root, "publisher"),
                Genres = genres,
                Released = released,
                CriticScore = JsonNum.Int(root, "criticScore"),
                // The proxy hands back finished URLs rather than image ids, so these go straight
                // into the art slots without IgdbClient.ImageUrl in between.
                CoverUrl = Str(root, "cover"),
                ArtworkUrl = Str(root, "artwork"),
            };
        }
    }

    public async Task<SteamGridArt?> FindArtAsync(string title, CancellationToken ct)
    {
        var d = await GetAsync("art", title, ct);
        if (d is null) return null;

        using (d)
        {
            var root = d.RootElement;
            var name = Str(root, "name");
            if (!TitleMatch.IsConfident(title, name))
            {
                Log.Info($"Proxy: art for '{name}' does not confidently match '{title}'");
                return null;
            }

            return new SteamGridArt
            {
                Portrait = Str(root, "portrait"),
                Tile = Str(root, "tile"),
                Hero = Str(root, "hero"),
                Logo = Str(root, "logo"),
            };
        }
    }

    private async Task<JsonDocument?> GetAsync(string kind, string title, CancellationToken ct)
    {
        if (Unavailable) return null;
        try
        {
            var url = $"{_endpoint}/v1/{kind}?title={Uri.EscapeDataString(title)}";
            using var res = await _http.GetAsync(url, ct);

            // 404 is the service saying "no confident answer", which is an ordinary outcome.
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

            if ((int)res.StatusCode == 429)
            {
                // Backing off for the rest of the pass is the polite response, and the user loses
                // nothing they had: art already on disk stays.
                Unavailable = true;
                Log.Info("Proxy: rate limited, skipping the rest of this pass");
                return null;
            }

            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"Proxy: /v1/{kind} returned {(int)res.StatusCode}");
                return null;
            }

            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            // One dead connection is enough to conclude the service is not reachable right now.
            // Everything it would have supplied is optional, so this is a quiet downgrade.
            Unavailable = true;
            Log.Info($"Proxy: unreachable ({ex.Message}); metadata for non-Steam games is skipped");
            return null;
        }
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
