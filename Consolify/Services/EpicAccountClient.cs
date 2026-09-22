using System.Net;
using System.Net.Http;
using System.Text.Json;
using Consolify.Models;
using Microsoft.Web.WebView2.Core;

namespace Consolify.Services;

/// <summary>
/// The Epic Games Store library, through the same sign-in the Epic Games Launcher uses.
///
/// The launcher's own OAuth client ("launcherAppClient2") is what every third-party Epic tool
/// signs in as -- Legendary, Heroic, Playnite -- because Epic publishes no other. The flow: the
/// store's login page in a window of its own; once signed in, Epic's redirect endpoint hands the
/// page an authorization code, which is swapped for an access token and a refresh token that
/// lasts weeks. Every scan refreshes the access token when it is near expiry and asks the library
/// service for the account's items, then the catalogue for each item's title, kind and art.
///
/// Catalogue answers are kept in the cache file by item id: a game's title and box art do not
/// change, and the library service answers in one page but the catalogue is one request per game.
/// A 300-game library is therefore 300 requests once and a handful ever after.
/// </summary>
public class EpicAccountClient : IStoreAccount
{
    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string BasicAuth = "basic MzRhMDJjZjhmNDQxNGUyOWIxNTkyMTg3NmRhMzZmOWE6ZGFhZmJjY2M3Mzc3NDUwMzlkZmZlNTNkOTRmYzc2Y2Y=";
    private const string RedirectApi = "https://www.epicgames.com/id/api/redirect?clientId=" + ClientId + "&responseType=code";
    private static readonly string LoginUrl = "https://www.epicgames.com/id/login?redirectUrl=" + Uri.EscapeDataString(RedirectApi);
    private const string TokenUrl = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
    // Not library-service.prod.epicgames.com, which Playnite still names: that host no longer
    // resolves. This is the one the launcher and Legendary use now.
    private const string LibraryUrl = "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true&platform=Windows";
    private const string CatalogUrl = "https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{0}/bulk/items?id={1}&country=US&locale=en-US&includeMainGameDetails=true";

    private static readonly HttpClient Http = MakeClient();

    public string Store => "epic";
    public string DisplayName => "Epic Games";
    public StoreStatus Status { get; } = new();

    private EpicTokens? _tokens;
    private OwnedCache? _cache;
    private bool _cacheLoaded;

    public EpicAccountClient()
    {
        _tokens = AccountStore.LoadSecret<EpicTokens>(Store);
        Status.SignedIn = _tokens is not null;
        Status.User = _tokens?.DisplayName;
    }

    private static HttpClient MakeClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        c.DefaultRequestHeaders.Add("User-Agent", "Consolify/1.0 (+https://github.com/consolify)");
        return c;
    }

    public class EpicTokens
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public DateTime RefreshExpiresAt { get; set; }
        public string AccountId { get; set; } = "";
        public string? DisplayName { get; set; }
    }

    // ---------- sign in ----------

    public async Task<bool> SignInAsync(MainWindow owner)
    {
        string? code = null;
        var ok = await StoreLoginWindow.RunAsync(owner, "Sign in to Epic Games", AccountStore.ProfileDir(Store), LoginUrl,
            async core =>
            {
                var url = StoreLoginWindow.SourceOf(core);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase))
                    return false;
                // Epic's redirect endpoint answers with the code once the session is signed in, and
                // with a null once it is not. Asked from inside the page, so its cookies go along.
                var script = "(function(){try{var x=new XMLHttpRequest();x.open('GET','" + RedirectApi + "',false);" +
                             "x.withCredentials=true;x.send(null);var j=JSON.parse(x.responseText);return j.authorizationCode||'';}catch(e){return '';}})()";
                var got = await StoreLoginWindow.EvalStringAsync(core, script);
                if (string.IsNullOrWhiteSpace(got)) return false;
                code = got;
                return true;
            });
        if (!ok || code is null) return false;

        var tokens = await ExchangeAsync($"grant_type=authorization_code&code={Uri.EscapeDataString(code)}&token_type=eg1", CancellationToken.None);
        if (tokens is null)
        {
            Status.Error = "Epic did not accept the sign-in";
            return false;
        }
        _tokens = tokens;
        AccountStore.SaveSecret(Store, tokens);
        Status.SignedIn = true;
        Status.User = tokens.DisplayName;
        Status.Error = null;
        Log.Info($"Epic: signed in as {tokens.DisplayName}");
        return true;
    }

    public void SignOut()
    {
        _tokens = null;
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

    private async Task<EpicTokens?> ExchangeAsync(string form, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(form, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            req.Headers.TryAddWithoutValidation("Authorization", BasicAuth);
            using var res = await Http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"Epic: token endpoint {(int)res.StatusCode}: {Trim(body)}");
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var t = new EpicTokens
            {
                AccessToken = Str(r, "access_token") ?? "",
                RefreshToken = Str(r, "refresh_token") ?? "",
                AccountId = Str(r, "account_id") ?? "",
                DisplayName = Str(r, "displayName"),
                ExpiresAt = ParseTime(Str(r, "expires_at")) ?? DateTime.UtcNow.AddHours(1),
                RefreshExpiresAt = ParseTime(Str(r, "refresh_expires_at")) ?? DateTime.UtcNow.AddDays(7),
            };
            return t.AccessToken.Length > 0 ? t : null;
        }
        catch (Exception ex)
        {
            Log.Info($"Epic: token exchange failed ({ex.Message})");
            return null;
        }
    }

    /// <summary>A usable access token, refreshed when within ten minutes of expiry, or null when
    /// the refresh token has gone too -- which means signing in again.</summary>
    private async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        if (_tokens is null) return null;
        if (DateTime.UtcNow < _tokens.ExpiresAt.AddMinutes(-10)) return _tokens.AccessToken;

        var fresh = await ExchangeAsync($"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(_tokens.RefreshToken)}&token_type=eg1", ct);
        if (fresh is null) return null;
        fresh.DisplayName ??= _tokens.DisplayName;
        _tokens = fresh;
        AccountStore.SaveSecret(Store, fresh);
        return fresh.AccessToken;
    }

    // ---------- the library ----------

    public async Task<List<Game>> GetOwnedAsync(bool force, CancellationToken ct = default)
    {
        var cached = Cached();
        if (_tokens is null)
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
            var token = await AccessTokenAsync(ct);
            if (token is null)
            {
                Status.Error = "The sign-in has expired. Sign in again";
                return Report(cached);
            }

            var records = await LibraryRecordsAsync(token, ct);
            var known = (cached?.Games ?? new()).ToDictionary(g => g.Id, g => g);
            var games = new List<Game>();
            var fetched = 0;
            foreach (var rec in records)
            {
                ct.ThrowIfCancellationRequested();
                var id = $"epic:{rec.AppName}";
                // The catalogue is the slow part, and its answer for a given item never changes.
                if (known.TryGetValue(id, out var have)) { games.Add(have); continue; }

                var item = await CatalogItemAsync(token, rec.Namespace, rec.CatalogItemId, ct);
                fetched++;
                if (item is null || !IsGame(item.Value)) continue;
                games.Add(new Game
                {
                    Id = id,
                    Title = (Str(item.Value, "title") ?? rec.AppName).Trim(),
                    Platform = "Epic",
                    Installed = false,
                    LaunchUri = $"com.epicgames.launcher://apps/{rec.AppName}?action=launch&silent=true",
                    InstallUri = $"com.epicgames.launcher://apps/{rec.AppName}?action=install",
                    RemoteCoverUrl = KeyImage(item.Value, "DieselGameBoxTall") ?? KeyImage(item.Value, "OfferImageTall"),
                    RemoteBackdropUrl = KeyImage(item.Value, "DieselGameBox") ?? KeyImage(item.Value, "OfferImageWide"),
                });
                // Gentle with the catalogue on a first run: it is a public endpoint shared by
                // every launcher install in the world, and nothing here is urgent.
                if (fetched % 10 == 0) await Task.Delay(300, ct);
            }

            var cache = new OwnedCache { FetchedAt = DateTime.UtcNow, Games = games };
            _cache = cache;
            AccountStore.SaveCache(Store, cache);
            Status.Error = null;
            Log.Info($"Epic: {games.Count} game(s) owned by {_tokens.DisplayName} ({fetched} catalogue lookups)");
            return Report(cache);
        }
        catch (Exception ex)
        {
            Status.Error = cached is not null
                ? $"Could not reach Epic; showing the library from {cached.FetchedAt.ToLocalTime():d MMM}"
                : "Could not reach Epic";
            Log.Info($"Epic: library fetch failed ({ex.Message})");
            return Report(cached);
        }
    }

    private record LibraryRecord(string Namespace, string CatalogItemId, string AppName);

    private static async Task<List<LibraryRecord>> LibraryRecordsAsync(string token, CancellationToken ct)
    {
        var list = new List<LibraryRecord>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var url = LibraryUrl + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"library service {(int)res.StatusCode}");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                foreach (var r in records.EnumerateArray())
                {
                    var ns = Str(r, "namespace");
                    var item = Str(r, "catalogItemId");
                    var app = Str(r, "appName");
                    // "ue" is Unreal Engine's namespace: engines, plugins and marketplace assets.
                    if (ns is null || item is null || string.IsNullOrWhiteSpace(app) || ns == "ue") continue;
                    list.Add(new LibraryRecord(ns, item, app));
                }
            cursor = root.TryGetProperty("responseMetadata", out var meta) ? Str(meta, "nextCursor") : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return list;
    }

    private static async Task<JsonElement?> CatalogItemAsync(string token, string ns, string itemId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(CatalogUrl, ns, itemId));
        req.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            Log.Info($"Epic: catalogue {(int)res.StatusCode} for {ns}/{itemId}");
            return null;
        }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty(itemId, out var item) ? item.Clone() : null;
    }

    /// <summary>
    /// A game, as opposed to the other things a library holds: DLC ("addons"), soundtracks and
    /// art books ("digitalextras"), engine plugins, and the engines themselves. Epic labels each
    /// with a category path, which is the same test the installed-game scanner already relies on.
    /// </summary>
    private static bool IsGame(JsonElement item)
    {
        if (!item.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array) return false;
        var paths = cats.EnumerateArray().Select(c => Str(c, "path") ?? "").ToList();
        if (!paths.Any(p => p.Equals("games", StringComparison.OrdinalIgnoreCase))) return false;
        return !paths.Any(p => p.StartsWith("addons", StringComparison.OrdinalIgnoreCase)
                            || p.StartsWith("digitalextras", StringComparison.OrdinalIgnoreCase)
                            || p.StartsWith("plugins", StringComparison.OrdinalIgnoreCase)
                            || p.StartsWith("engines", StringComparison.OrdinalIgnoreCase));
    }

    private static string? KeyImage(JsonElement item, string type)
    {
        if (!item.TryGetProperty("keyImages", out var imgs) || imgs.ValueKind != JsonValueKind.Array) return null;
        foreach (var img in imgs.EnumerateArray())
            if (Str(img, "type") == type && Str(img, "url") is { Length: > 0 } url) return url;
        return null;
    }

    private List<Game> Report(OwnedCache? cache)
    {
        Status.Count = cache?.Games.Count ?? 0;
        Status.FetchedAt = cache?.FetchedAt;
        // A fresh list every time: MergeScanned mutates what it is given.
        return (cache?.Games ?? new()).Select(Clone).ToList();
    }

    private OwnedCache? Cached()
    {
        if (!_cacheLoaded) { _cacheLoaded = true; _cache = AccountStore.LoadCache(Store); }
        return _cache;
    }

    internal static Game Clone(Game g) => new()
    {
        Id = g.Id, Title = g.Title, Platform = g.Platform, Installed = false,
        LaunchUri = g.LaunchUri, InstallUri = g.InstallUri, PackageFamilyName = g.PackageFamilyName,
        RemoteCoverUrl = g.RemoteCoverUrl, RemoteBackdropUrl = g.RemoteBackdropUrl, LastPlayed = g.LastPlayed,
    };

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? ParseTime(string? iso) =>
        iso is not null && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
