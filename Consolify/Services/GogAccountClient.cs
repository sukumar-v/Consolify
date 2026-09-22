using System.Net;
using System.Net.Http;
using System.Text.Json;
using Consolify.Models;
using Microsoft.Win32;

namespace Consolify.Services;

/// <summary>
/// The GOG library, through the account's own web session.
///
/// GOG's OAuth client for third parties is GOG Galaxy's, and GOG has stopped accepting the
/// credentials every open-source client carried for it (the token endpoint answers
/// invalid_client to everything now). What still works is what Playnite does: the user signs
/// in on gog.com in a window of its own, and the session cookies that leaves behind are what
/// the account's own pages accept -- the same ones a browser would send. They are copied out of
/// the sign-in window, kept encrypted, and used until GOG stops honouring them, at which point
/// the row says to sign in again. A GOG session lasts weeks.
///
/// Box art comes from GOG's public games API, one request per game, kept in the cache by id.
/// </summary>
public class GogAccountClient : IStoreAccount
{
    private const string LoginUrl = "https://www.gog.com/account";
    private const string AccountUrl = "https://menu.gog.com/v1/account/basic";
    private const string ProductsUrl = "https://www.gog.com/account/getFilteredProducts?hiddenFlag=0&mediaType=1&sortBy=title&page={0}";
    private const string GameUrl = "https://api.gog.com/v2/games/{0}";
    private const string AuthCookie = "gog-al";

    public string Store => "gog";
    public string DisplayName => "GOG";
    public StoreStatus Status { get; } = new();

    private GogSession? _session;
    private OwnedCache? _cache;
    private bool _cacheLoaded;

    public GogAccountClient()
    {
        _session = AccountStore.LoadSecret<GogSession>(Store);
        Status.SignedIn = _session is not null;
        Status.User = _session?.Username;
    }

    public class CookieRecord
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Path { get; set; } = "/";
    }

    public class GogSession
    {
        public List<CookieRecord> Cookies { get; set; } = new();
        public string? Username { get; set; }
    }

    /// <summary>Galaxy's install route beats a web page, but only when Galaxy is here to take it.</summary>
    private static bool GalaxyInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\GalaxyClient\paths");
            return k?.GetValue("client") is string p && System.IO.Directory.Exists(p);
        }
        catch { return false; }
    }

    // ---------- sign in ----------

    public async Task<bool> SignInAsync(MainWindow owner)
    {
        GogSession? captured = null;
        var ok = await StoreLoginWindow.RunAsync(owner, "Sign in to GOG", AccountStore.ProfileDir(Store), LoginUrl,
            async core =>
            {
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.gog.com");
                if (!cookies.Any(c => c.Name == AuthCookie)) return false;
                var session = new GogSession
                {
                    Cookies = cookies.Select(c => new CookieRecord { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path }).ToList()
                };
                var user = await UsernameAsync(session, CancellationToken.None);
                if (user is null) return false;
                session.Username = user;
                captured = session;
                return true;
            });
        if (!ok || captured is null) return false;

        _session = captured;
        AccountStore.SaveSecret(Store, captured);
        Status.SignedIn = true;
        Status.User = captured.Username;
        Status.Error = null;
        Log.Info($"GOG: signed in as {captured.Username}");
        return true;
    }

    public void SignOut()
    {
        _session = null;
        _cache = null;
        AccountStore.DeleteSecret(Store);
        AccountStore.DeleteCache(Store);
        AccountStore.ClearProfile(Store);
        Status.SignedIn = false;
        Status.User = null;
        Status.Count = 0;
        Status.FetchedAt = null;
        Status.Error = null;
    }

    /// <summary>A client carrying the session, redirects left unfollowed so a bounce to the login
    /// page reads as "signed out" rather than as a page of HTML where JSON was expected.</summary>
    private static HttpClient ClientFor(GogSession session)
    {
        var jar = new CookieContainer();
        foreach (var c in session.Cookies)
        {
            try { jar.Add(new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain)); }
            catch { /* a cookie .NET will not model is not worth the sign-in */ }
        }
        var handler = new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Consolify/1.0");
        http.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        return http;
    }

    private static async Task<string?> UsernameAsync(GogSession session, CancellationToken ct)
    {
        try
        {
            using var http = ClientFor(session);
            using var res = await http.GetAsync(AccountUrl, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            if (!(r.TryGetProperty("isLoggedIn", out var li) && li.ValueKind == JsonValueKind.True)) return null;
            return Str(r, "username") ?? Str(r, "email") ?? "GOG user";
        }
        catch (Exception ex)
        {
            Log.Info($"GOG: account check failed ({ex.Message})");
            return null;
        }
    }

    // ---------- the library ----------

    public async Task<List<Game>> GetOwnedAsync(bool force, CancellationToken ct = default)
    {
        var cached = Cached();
        if (_session is null)
        {
            Status.SignedIn = false;
            return Report(null);
        }
        if (!force && cached is not null && DateTime.UtcNow - cached.FetchedAt < AccountStore.Freshness)
        {
            Status.Error = null;
            return Report(cached);
        }

        try
        {
            using var http = ClientFor(_session);
            var known = (cached?.Games ?? new()).ToDictionary(g => g.Id, g => g);
            var galaxy = GalaxyInstalled();
            var games = new List<Game>();
            var lookups = 0;

            for (var page = 1; page <= 100; page++)
            {
                ct.ThrowIfCancellationRequested();
                using var res = await http.GetAsync(string.Format(ProductsUrl, page), ct);
                if ((int)res.StatusCode is 301 or 302 or 401 or 403)
                {
                    Status.Error = "The GOG session has expired. Sign in again";
                    return Report(cached);
                }
                if (!res.IsSuccessStatusCode) throw new HttpRequestException($"account page {(int)res.StatusCode}");

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                var totalPages = JsonNum.Int(root, "totalPages") ?? 1;
                if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
                    foreach (var p in products.EnumerateArray())
                    {
                        var id = JsonNum.Long(p, "id");
                        var title = Str(p, "title");
                        if (id is null || string.IsNullOrWhiteSpace(title)) continue;
                        if (p.TryGetProperty("isGame", out var ig) && ig.ValueKind == JsonValueKind.False) continue;
                        // GOG sells Mac- and Linux-only games too; nothing here can install those.
                        if (p.TryGetProperty("worksOn", out var wo) && wo.ValueKind == JsonValueKind.Object
                            && wo.TryGetProperty("Windows", out var win) && win.ValueKind == JsonValueKind.False) continue;

                        var gameId = $"gog:{id}";
                        var url = Str(p, "url");
                        var game = known.TryGetValue(gameId, out var have) ? have : new Game { Id = gameId };
                        game.Title = title.Trim();
                        game.Platform = "GOG";
                        game.Installed = false;
                        game.InstallUri = galaxy
                            ? $"goggalaxy://openGameView/{id}"
                            : (url is { Length: > 0 } ? "https://www.gog.com" + url : null);
                        if (game.RemoteCoverUrl is null && !known.ContainsKey(gameId))
                        {
                            await ArtAsync(http, id.Value, game, ct);
                            if (++lookups % 10 == 0) await Task.Delay(300, ct);
                        }
                        games.Add(game);
                    }
                if (page >= totalPages) break;
            }

            var cache = new OwnedCache { FetchedAt = DateTime.UtcNow, Games = games };
            _cache = cache;
            AccountStore.SaveCache(Store, cache);
            Status.Error = null;
            Log.Info($"GOG: {games.Count} game(s) owned by {_session.Username} ({lookups} art lookups)");
            return Report(cache);
        }
        catch (Exception ex)
        {
            Status.Error = cached is not null
                ? $"Could not reach GOG; showing the library from {cached.FetchedAt.ToLocalTime():d MMM}"
                : "Could not reach GOG";
            Log.Info($"GOG: library fetch failed ({ex.Message})");
            return Report(cached);
        }
    }

    /// <summary>GOG's public games API names a real 2:3 box art and a wide background for every
    /// product; the account page only carries a thumbnail hash whose full-size variants are
    /// undocumented.</summary>
    private static async Task ArtAsync(HttpClient http, long id, Game game, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(string.Format(GameUrl, id), ct);
            if (!res.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("_links", out var links) || links.ValueKind != JsonValueKind.Object) return;
            string? Href(string key) =>
                links.TryGetProperty(key, out var l) && l.ValueKind == JsonValueKind.Object ? Str(l, "href") : null;
            game.RemoteCoverUrl = Href("boxArtImage");
            game.RemoteBackdropUrl = Href("backgroundImage") ?? Href("galaxyBackgroundImage");
        }
        catch (Exception ex) { Log.Info($"GOG: art for {id} failed ({ex.Message})"); }
    }

    private List<Game> Report(OwnedCache? cache)
    {
        Status.Count = cache?.Games.Count ?? 0;
        Status.FetchedAt = cache?.FetchedAt;
        return (cache?.Games ?? new()).Select(EpicAccountClient.Clone).ToList();
    }

    private OwnedCache? Cached()
    {
        if (!_cacheLoaded) { _cacheLoaded = true; _cache = AccountStore.LoadCache(Store); }
        return _cache;
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
