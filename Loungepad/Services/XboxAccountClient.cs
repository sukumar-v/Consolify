using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// The PC games on an Xbox profile, through a Microsoft account sign-in.
///
/// Xbox Live has no "owned games" list a third party can read. What it has is the title
/// history -- every game the account has played, on any device, with the package family name
/// for the ones that exist on PC -- and that is what Playnite imports as the Xbox library, so it
/// is what this does. A game bought and never launched is not in it; the Game Pass catalogue
/// covers the rest of what a subscriber can install.
///
/// Signing in is a chain of four tokens. The Microsoft account sign-in yields an OAuth access
/// token; user.auth.xboxlive.com turns that into an Xbox user token; xsts.auth.xboxlive.com
/// turns that into an XSTS token, which is the one every Xbox Live service accepts and which
/// carries the profile's xuid; titlehub.xboxlive.com is asked with it. The OAuth token is the
/// only one that outlives a day, through its refresh token.
///
/// Which OAuth client signs in is the whole question. Playnite registered an application of its
/// own with Microsoft -- the "Let this app access your info?" prompt is that registration's
/// consent screen -- and Xbox Live accepts tickets from any such registration. The alternative is
/// to sign in as one of Microsoft's own first-party clients, the way every third-party tool did
/// for years; Xbox Live has been withdrawing that, and the Xbox app's own id (0000000048093EE3)
/// is now answered 403 by the user-token service. <see cref="DefaultClientId"/> is where a
/// Loungepad registration goes; until it is filled in, the built-in client is the last
/// first-party id still reported working, and the Settings row takes a registration of the
/// user's own. Either registration uses the modern code flow; the first-party id uses the
/// implicit one. Everything after the OAuth step is the same.
/// </summary>
public class XboxAccountClient : IStoreAccount
{
    /// <summary>
    /// The client id of Loungepad's own Azure app registration, once the project has one. Five
    /// minutes in the Azure portal (see the README), free, no secret, and it is what makes the
    /// Xbox sign-in need nothing from the user -- exactly what Playnite ships. Empty means "not
    /// registered yet", and the first-party client below is used instead.
    /// </summary>
    private const string DefaultClientId = "";

    /// <summary>
    /// A first-party Microsoft client that Xbox Live's user-token service still accepts tickets
    /// from, with the "t=" prefix. 0000000048093EE3, the Xbox app's own id, no longer is: its
    /// tickets come back 403 whatever the prefix. This one is what @xboxreplay/xboxlive-auth
    /// signs in as, and it can stop working the same way at any time -- which is why a
    /// registration of one's own is the real answer.
    /// </summary>
    private const string LegacyClientId = "000000004C12AE6F";
    private const string LegacyScope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string ModernScope = "Xboxlive.signin Xboxlive.offline_access";
    private const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
    private const string AuthorizeUrl = "https://login.live.com/oauth20_authorize.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";
    private const string UserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string TitleHistoryUrl = "https://titlehub.xboxlive.com/users/xuid({0})/titles/titlehistory/decoration/{1}";

    private static readonly HttpClient Http = MakeClient();
    private readonly Func<string> _customClientId;

    public string Store => "xbox";
    public string DisplayName => "Xbox";
    public StoreStatus Status { get; } = new();

    private XboxTokens? _tokens;
    private OwnedCache? _cache;
    private bool _cacheLoaded;

    public XboxAccountClient(Func<string> customClientId)
    {
        _customClientId = customClientId;
        _tokens = AccountStore.LoadSecret<XboxTokens>(Store);
        Status.SignedIn = _tokens is not null;
        Status.User = _tokens?.Gamertag;
    }

    private static HttpClient MakeClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        c.DefaultRequestHeaders.Add("User-Agent", "Loungepad/1.0 (+https://github.com/sukumar-v/Loungepad)");
        return c;
    }

    public class XboxTokens
    {
        public string ClientId { get; set; } = LegacyClientId;
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        /// <summary>The XSTS token and its claims, kept because minting one is two round trips and
        /// it lasts about sixteen hours.</summary>
        public string? XstsToken { get; set; }
        public string? UserHash { get; set; }
        public string? Xuid { get; set; }
        public string? Gamertag { get; set; }
        public DateTime XstsExpiresAt { get; set; }
    }

    /// <summary>The user's own registration first, then Loungepad's, then the first-party client.</summary>
    private string ClientId =>
        !string.IsNullOrWhiteSpace(_customClientId()) ? _customClientId().Trim()
        : DefaultClientId.Length > 0 ? DefaultClientId
        : LegacyClientId;

    // ---------- sign in ----------

    public async Task<bool> SignInAsync(MainWindow owner)
    {
        var clientId = ClientId;
        var modern = clientId != LegacyClientId;
        var url = AuthorizeUrl +
                  $"?client_id={clientId}&response_type={(modern ? "code" : "token")}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                  $"&scope={Uri.EscapeDataString(modern ? ModernScope : LegacyScope)}&display=touch&locale=en";

        Dictionary<string, string>? landed = null;
        var ok = await StoreLoginWindow.RunAsync(owner, "Sign in to Xbox", AccountStore.ProfileDir(Store), url,
            core =>
            {
                var source = StoreLoginWindow.SourceOf(core);
                if (!source.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(false);
                var parsed = ParseRedirect(source);
                if (!parsed.ContainsKey("access_token") && !parsed.ContainsKey("code")) return Task.FromResult(false);
                landed = parsed;
                return Task.FromResult(true);
            });
        if (!ok || landed is null) return false;

        XboxTokens? tokens;
        if (landed.TryGetValue("code", out var code))
            tokens = await OAuthAsync(clientId, $"client_id={clientId}&grant_type=authorization_code&code={Uri.EscapeDataString(code)}" +
                                                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(ModernScope)}", CancellationToken.None);
        else
            tokens = new XboxTokens
            {
                ClientId = clientId,
                AccessToken = landed["access_token"],
                RefreshToken = landed.GetValueOrDefault("refresh_token", ""),
                ExpiresAt = DateTime.UtcNow.AddSeconds(int.TryParse(landed.GetValueOrDefault("expires_in"), out var s) ? s : 3600),
            };
        if (tokens is null)
        {
            Status.Error = "Microsoft did not accept the sign-in";
            return false;
        }

        try
        {
            await XstsAsync(tokens, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A rejected ticket from the first-party client is Microsoft withdrawing that client,
            // not a wrong password; the fix is a registration, and the message has to say so.
            Status.Error = modern
                ? $"Xbox Live did not accept the sign-in ({ex.Message})"
                : $"Xbox Live no longer accepts sign-ins from the built-in client ({ex.Message}). " +
                  "Register an app id of your own — see the README — and paste it into “Xbox sign-in app id”";
            Log.Info($"Xbox: {ex.Message} (client {clientId})");
            return false;
        }

        _tokens = tokens;
        AccountStore.SaveSecret(Store, tokens);
        Status.SignedIn = true;
        Status.User = tokens.Gamertag;
        Status.Error = null;
        Log.Info($"Xbox: signed in as {tokens.Gamertag} ({(modern ? "own app" : "Xbox app client")})");
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

    /// <summary>The query and the fragment of the redirect, as one bag: the implicit flow puts
    /// the token after '#', the code flow puts the code after '?'.</summary>
    private static Dictionary<string, string> ParseRedirect(string url)
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = url.IndexOf('?');
        var h = url.IndexOf('#');
        foreach (var part in new[] { q, h }.Where(i => i >= 0))
        {
            var end = part == q && h > q ? h : url.Length;
            foreach (var kv in url[(part + 1)..end].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                bag[Uri.UnescapeDataString(kv[..eq])] = Uri.UnescapeDataString(kv[(eq + 1)..]);
            }
        }
        return bag;
    }

    private static async Task<XboxTokens?> OAuthAsync(string clientId, string form, CancellationToken ct)
    {
        try
        {
            using var res = await Http.PostAsync(TokenUrl,
                new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"Xbox: token endpoint {(int)res.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var access = Str(r, "access_token");
            if (string.IsNullOrEmpty(access)) return null;
            return new XboxTokens
            {
                ClientId = clientId,
                AccessToken = access,
                RefreshToken = Str(r, "refresh_token") ?? "",
                ExpiresAt = DateTime.UtcNow.AddSeconds(JsonNum.Int(r, "expires_in") ?? 3600),
            };
        }
        catch (Exception ex)
        {
            Log.Info($"Xbox: token request failed ({ex.Message})");
            return null;
        }
    }

    /// <summary>Access token good for at least ten minutes, refreshed if need be; null means the
    /// refresh token is gone too and the user has to sign in again.</summary>
    private async Task<bool> EnsureAccessAsync(CancellationToken ct)
    {
        if (_tokens is null) return false;
        if (DateTime.UtcNow < _tokens.ExpiresAt.AddMinutes(-10)) return true;
        if (string.IsNullOrEmpty(_tokens.RefreshToken)) return false;

        var modern = _tokens.ClientId != LegacyClientId;
        var form = $"client_id={_tokens.ClientId}&grant_type=refresh_token&refresh_token={Uri.EscapeDataString(_tokens.RefreshToken)}" +
                   $"&scope={Uri.EscapeDataString(modern ? ModernScope : LegacyScope)}" +
                   (modern ? $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" : "");
        var fresh = await OAuthAsync(_tokens.ClientId, form, ct);
        if (fresh is null) return false;
        fresh.Gamertag = _tokens.Gamertag;
        fresh.Xuid = _tokens.Xuid;
        fresh.RefreshToken = string.IsNullOrEmpty(fresh.RefreshToken) ? _tokens.RefreshToken : fresh.RefreshToken;
        _tokens = fresh;
        AccountStore.SaveSecret(Store, fresh);
        return true;
    }

    /// <summary>
    /// The user token and then the XSTS token, written back into <paramref name="t"/>.
    ///
    /// The RPS ticket's prefix depends on which kind of OAuth client issued the access token:
    /// "d=" for an application registered in Azure, "t=" for a first-party client -- and some
    /// first-party answers are accepted bare. All three are tried in turn on any rejection (400,
    /// 401 or 403: the service is not consistent about which it uses for "not this"), and the
    /// last status is what the error carries when none of them worked.
    /// </summary>
    private static async Task XstsAsync(XboxTokens t, CancellationToken ct)
    {
        var modern = t.ClientId != LegacyClientId;
        var prefixes = modern ? new[] { "d=" } : new[] { "t=", "", "d=" };
        string? userToken = null;
        var last = 0;
        foreach (var prefix in prefixes)
        {
            var body = JsonSerializer.Serialize(new
            {
                Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = prefix + t.AccessToken },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT",
            });
            var (status, json) = await PostJsonAsync(UserAuthUrl, body, 1, ct);
            last = status;
            if (status == 200)
            {
                using var doc = JsonDocument.Parse(json);
                userToken = Str(doc.RootElement, "Token");
                if (userToken is not null)
                {
                    Log.Info($"Xbox: user token issued (ticket prefix '{prefix}')");
                    break;
                }
            }
            Log.Info($"Xbox: user token with prefix '{prefix}' answered {status}");
            if (status is not (400 or 401 or 403)) throw new InvalidOperationException($"user token {status}");
        }
        if (userToken is null) throw new InvalidOperationException($"user token {last}");

        var xstsBody = JsonSerializer.Serialize(new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { userToken } },
            RelyingParty = "http://xboxlive.com",
            TokenType = "JWT",
        });
        var (xs, xjson) = await PostJsonAsync(XstsUrl, xstsBody, 1, ct);
        if (xs != 200)
        {
            // 2148916233 is "no Xbox profile on this Microsoft account", worth saying plainly.
            var err = xjson.Contains("2148916233") ? "this Microsoft account has no Xbox profile" : $"XSTS {xs}";
            throw new InvalidOperationException(err);
        }
        using var xdoc = JsonDocument.Parse(xjson);
        var root = xdoc.RootElement;
        t.XstsToken = Str(root, "Token");
        t.XstsExpiresAt = ParseTime(Str(root, "NotAfter")) ?? DateTime.UtcNow.AddHours(8);
        if (root.TryGetProperty("DisplayClaims", out var dc) && dc.TryGetProperty("xui", out var xui)
            && xui.ValueKind == JsonValueKind.Array && xui.GetArrayLength() > 0)
        {
            var claims = xui[0];
            t.UserHash = Str(claims, "uhs");
            t.Xuid = Str(claims, "xid");
            t.Gamertag = Str(claims, "gtg") ?? t.Gamertag;
        }
        if (t.XstsToken is null || t.UserHash is null || t.Xuid is null)
            throw new InvalidOperationException("XSTS answer incomplete");
    }

    private static async Task<(int Status, string Body)> PostJsonAsync(string url, string body, int contractVersion, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("x-xbl-contract-version", contractVersion.ToString());
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var res = await Http.SendAsync(req, ct);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
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
            if (!await EnsureAccessAsync(ct))
            {
                Status.Error = "The sign-in has expired. Sign in again";
                return Report(cached);
            }
            if (_tokens.XstsToken is null || DateTime.UtcNow > _tokens.XstsExpiresAt.AddMinutes(-10))
            {
                await XstsAsync(_tokens, ct);
                AccountStore.SaveSecret(Store, _tokens);
            }

            var games = await TitleHistoryAsync(_tokens, ct);
            var cache = new OwnedCache { FetchedAt = DateTime.UtcNow, Games = games };
            _cache = cache;
            AccountStore.SaveCache(Store, cache);
            Status.Error = null;
            Log.Info($"Xbox: {games.Count} PC game(s) in {_tokens.Gamertag}'s title history");
            return Report(cache);
        }
        catch (Exception ex)
        {
            Status.Error = cached is not null
                ? $"Could not reach Xbox Live; showing the library from {cached.FetchedAt.ToLocalTime():d MMM}"
                : $"Could not reach Xbox Live ({ex.Message})";
            Log.Info($"Xbox: library fetch failed ({ex.Message})");
            return Report(cached);
        }
    }

    private static async Task<List<Game>> TitleHistoryAsync(XboxTokens t, CancellationToken ct)
    {
        // "image" is not a decoration every deployment knows; "detail" alone still carries the
        // display image, so it is the fallback.
        string? json = null;
        foreach (var decoration in new[] { "detail,image", "detail" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(TitleHistoryUrl, t.Xuid, decoration));
            req.Headers.TryAddWithoutValidation("Authorization", $"XBL3.0 x={t.UserHash};{t.XstsToken}");
            req.Headers.TryAddWithoutValidation("x-xbl-contract-version", "2");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var res = await Http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) { json = await res.Content.ReadAsStringAsync(ct); break; }
            if ((int)res.StatusCode == 401) throw new InvalidOperationException("title history 401");
            Log.Info($"Xbox: title history with '{decoration}' answered {(int)res.StatusCode}");
        }
        if (json is null) throw new InvalidOperationException("title history unavailable");

        var games = new List<Game>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) return games;
        foreach (var title in titles.EnumerateArray())
        {
            var pfn = Str(title, "pfn");
            var name = Str(title, "name");
            if (string.IsNullOrWhiteSpace(pfn) || string.IsNullOrWhiteSpace(name)) continue;
            if (Str(title, "type") is { } type && !type.Equals("Game", StringComparison.OrdinalIgnoreCase)) continue;
            if (title.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array
                && !devices.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.String && d.GetString() == "PC")) continue;

            string? poster = null, hero = null;
            if (title.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                foreach (var img in images.EnumerateArray())
                {
                    var kind = Str(img, "type");
                    var url = Str(img, "url");
                    if (url is null) continue;
                    if (kind == "Poster") poster ??= url;
                    else if (kind is "SuperHeroArt" or "TitledHeroArt") hero ??= url;
                }

            DateTime? lastPlayed = null;
            if (title.TryGetProperty("titleHistory", out var th) && ParseTime(Str(th, "lastTimePlayed")) is { } lp)
                lastPlayed = lp.ToLocalTime();

            games.Add(new Game
            {
                Id = $"xbox:pfn:{pfn}",
                Title = name.Trim(),
                Platform = "Xbox",
                Installed = false,
                PackageFamilyName = pfn,
                InstallUri = $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(pfn)}",
                RemoteCoverUrl = poster,
                RemoteBackdropUrl = hero,
                LastPlayed = lastPlayed,
            });
        }
        return games;
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

    private static DateTime? ParseTime(string? iso) =>
        iso is not null && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
