using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Consolify.Models;
using Microsoft.Win32;

namespace Consolify.Services;

/// <summary>A Steam login found on this PC, read from the client's own loginusers.vdf.</summary>
public record SteamAccount(string SteamId, string PersonaName);

/// <summary>One entry of the account's library, as the Web API reports it.</summary>
public class OwnedGame
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public double PlaytimeMinutes { get; set; }
    public DateTime? LastPlayed { get; set; }
}

/// <summary>The last successful answer, kept on disk so an offline start keeps the library.</summary>
public class OwnedLibrary
{
    public string SteamId { get; set; } = "";
    public DateTime FetchedAt { get; set; }
    public List<OwnedGame> Games { get; set; } = new();
}

/// <summary>What the Settings screen is told about the account, on every state push.</summary>
public class SteamAccountStatus
{
    public string? SteamId { get; set; }
    public string? PersonaName { get; set; }
    public int OwnedCount { get; set; }
    public DateTime? FetchedAt { get; set; }
    /// <summary>Why the last fetch did not answer, in words meant for the Settings row. Null when
    /// it did, or when nothing has been asked for yet.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// The whole of a Steam account's library, not just the part on disk.
///
/// The scanner can only see what is installed. Everything else a person owns lives on Steam's
/// side, and the only way to ask is the Web API's GetOwnedGames, which needs a key. This is the
/// arrangement Playnite settled on: a shared key on a server for profiles whose game details are
/// public, and the user's own key -- free from steamcommunity.com/dev/apikey -- for private ones,
/// because a profile's own key can read it whatever the privacy setting says.
///
/// The community profile page used to be the keyless way in (games?tab=all, and before that the
/// ?xml=1 feed). Both now redirect to a login page for every profile, public or not, so there is
/// no keyless route left. Nothing local helps either: the client's per-account librarycache holds
/// achievement pointers rather than names, and licensecache is encrypted.
///
/// The account itself needs no login. Steam writes every account that has signed in on this PC
/// to config/loginusers.vdf, and the most recent one is the person on the sofa.
///
/// Every failure is soft. The last good answer is cached in steam-owned.json and served whenever a
/// fetch fails, so a start with no network keeps the library it had; and a fetch that succeeds is
/// held for six hours, because a library changes by the week and the scan runs on every start.
/// </summary>
public class SteamAccountService
{
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly HttpClient Http = CreateClient();
    private OwnedLibrary? _cache;
    private bool _cacheLoaded;

    public SteamAccountStatus Status { get; } = new();

    public SteamAccountService()
    {
        // So the Settings row can name the account before the first fetch has happened.
        var account = DetectAccount();
        Status.SteamId = account?.SteamId;
        Status.PersonaName = account?.PersonaName;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        c.DefaultRequestHeaders.Add("User-Agent", "Consolify/1.0 (+https://github.com/consolify)");
        return c;
    }

    public static string? SteamPath()
    {
        var p = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)
            ?.Replace('/', '\\');
        return p is not null && Directory.Exists(p) ? p : null;
    }

    /// <summary>
    /// The account Steam would sign in as. loginusers.vdf lists every login this PC has seen;
    /// "MostRecent" marks the current one on most installs, and the newest "Timestamp" is the
    /// same answer on the ones that omit it.
    /// </summary>
    public static SteamAccount? DetectAccount()
    {
        try
        {
            var steam = SteamPath();
            if (steam is null) return null;
            var file = Path.Combine(steam, "config", "loginusers.vdf");
            if (!File.Exists(file)) return null;

            SteamAccount? best = null;
            long bestStamp = -1;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"(7656\\d{13})\"\\s*\\{([^}]*)\\}"))
            {
                var body = m.Groups[2].Value;
                string? Get(string key) =>
                    Regex.Match(body, $"\"{key}\"\\s+\"([^\"]*)\"").Groups[1].Value is { Length: > 0 } v ? v : null;

                long.TryParse(Get("Timestamp"), out var stamp);
                if (Get("MostRecent") == "1") stamp = long.MaxValue;
                if (stamp <= bestStamp) continue;
                bestStamp = stamp;
                best = new SteamAccount(m.Groups[1].Value, Get("PersonaName") ?? Get("AccountName") ?? m.Groups[1].Value);
            }
            return best;
        }
        catch (Exception ex)
        {
            Log.Info($"Steam account detection failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The account's library. <paramref name="force"/> ignores the six-hour hold, for a rescan the
    /// user asked for by hand. Never throws; an empty list with <see cref="Status"/>.Error set is
    /// what failure looks like, and the last good answer is returned in preference to nothing.
    /// </summary>
    public async Task<IReadOnlyList<OwnedGame>> GetOwnedAsync(AppSettings settings, bool force,
        CancellationToken ct = default)
    {
        var account = DetectAccount();
        Status.SteamId = account?.SteamId;
        Status.PersonaName = account?.PersonaName;
        if (account is null)
        {
            Status.Error = "No Steam login was found on this PC";
            return Report(null);
        }

        var key = settings.SteamApiKey.Trim();
        var endpoint = string.IsNullOrWhiteSpace(settings.MetadataEndpoint)
            ? MetadataProxyClient.DefaultEndpoint
            : settings.MetadataEndpoint.Trim();
        var viaProxy = key.Length == 0;
        var cached = Cached(account.SteamId);

        if (viaProxy && !MetadataProxyClient.IsConfigured(endpoint))
        {
            Status.Error = "No metadata service is set, so a Steam Web API key is needed";
            return Report(cached);
        }

        if (!force && cached is not null && DateTime.UtcNow - cached.FetchedAt < Freshness)
        {
            Status.Error = null;
            return Report(cached);
        }

        const string Private =
            "Steam keeps this profile's game details private. Make them public under Steam's " +
            "privacy settings, or add your own Web API key below";

        try
        {
            var url = viaProxy
                ? $"{endpoint.TrimEnd('/')}/v1/owned?steamid={account.SteamId}"
                : "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                  $"?key={Uri.EscapeDataString(key)}&steamid={account.SteamId}" +
                  "&include_appinfo=1&include_played_free_games=1&format=json";
            using var res = await Http.GetAsync(url, ct);

            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The proxy answers 403 when Steam gave it nothing, which is what a private profile
                // looks like from outside. A rejected key is the direct route's 401.
                Status.Error = viaProxy ? Private : "Steam rejected the Web API key";
                Log.Info($"Steam library: {(int)res.StatusCode} from {(viaProxy ? "the service" : "Steam")}");
                return Report(cached);
            }
            // 501 is the service saying it has no Steam key. 400 and 404 are a service that has not
            // been deployed with this route at all, which from here is the same fact.
            if (viaProxy && (int)res.StatusCode is 501 or 404 or 400)
            {
                Status.Error = "The metadata service does not offer Steam library lookups yet. Add your own Web API key below";
                Log.Info($"Steam library: the service answered {(int)res.StatusCode} for /v1/owned");
                return Report(cached);
            }
            if (!res.IsSuccessStatusCode)
            {
                Status.Error = $"Could not fetch the library ({(int)res.StatusCode})";
                Log.Info($"Steam library: {(int)res.StatusCode} from {(viaProxy ? "the service" : "Steam")}");
                return Report(cached);
            }

            var games = Parse(await res.Content.ReadAsStringAsync(ct));
            if (games is null)
            {
                // Steam's own answer to a profile it will not show: a 200 with no games array at
                // all. An empty library, by contrast, arrives as an empty array.
                Status.Error = Private;
                return Report(cached);
            }

            var lib = new OwnedLibrary { SteamId = account.SteamId, FetchedAt = DateTime.UtcNow, Games = games };
            SaveCache(lib);
            Status.Error = null;
            Log.Info($"Steam library: {games.Count} game(s) owned by {account.PersonaName}");
            return Report(lib);
        }
        catch (Exception ex)
        {
            Status.Error = cached is not null
                ? $"Could not reach Steam; showing the library from {cached.FetchedAt.ToLocalTime():d MMM}"
                : "Could not reach Steam";
            Log.Info($"Steam library fetch failed: {ex.Message}");
            return Report(cached);
        }
    }

    private IReadOnlyList<OwnedGame> Report(OwnedLibrary? lib)
    {
        Status.OwnedCount = lib?.Games.Count ?? 0;
        Status.FetchedAt = lib?.FetchedAt;
        return lib?.Games ?? new List<OwnedGame>();
    }

    /// <summary>
    /// Both routes answer in Steam's own shape -- the proxy passes GetOwnedGames through, trimmed
    /// to the four fields used here -- so one parser serves both.
    /// </summary>
    private static List<OwnedGame>? Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("games", out var games)
            || games.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<OwnedGame>();
        foreach (var g in games.EnumerateArray())
        {
            var appId = JsonNum.Long(g, "appid");
            var name = g.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (appId is null or <= 0 || string.IsNullOrWhiteSpace(name)) continue;

            DateTime? last = null;
            if (JsonNum.Long(g, "rtime_last_played") is { } rt && rt > 0)
                last = DateTimeOffset.FromUnixTimeSeconds(rt).LocalDateTime;

            list.Add(new OwnedGame
            {
                AppId = appId.Value.ToString(),
                Name = name!.Trim(),
                PlaytimeMinutes = JsonNum.Double(g, "playtime_forever") ?? 0,
                LastPlayed = last,
            });
        }
        return list;
    }

    private OwnedLibrary? Cached(string steamId)
    {
        if (!_cacheLoaded)
        {
            _cacheLoaded = true;
            try
            {
                if (File.Exists(Paths.SteamOwnedFile))
                    _cache = JsonSerializer.Deserialize<OwnedLibrary>(File.ReadAllText(Paths.SteamOwnedFile));
            }
            catch (Exception ex) { Log.Info($"Steam library cache unreadable: {ex.Message}"); }
        }
        // Another account signing in on the same PC must not inherit the first one's library.
        return _cache is not null && _cache.SteamId == steamId ? _cache : null;
    }

    private void SaveCache(OwnedLibrary lib)
    {
        _cache = lib;
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.SteamOwnedFile, JsonSerializer.Serialize(lib, JsonOpts));
        }
        catch (Exception ex) { Log.Info($"Steam library cache not written: {ex.Message}"); }
    }
}
