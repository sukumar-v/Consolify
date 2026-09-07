using System.Net.Http;
using System.Text.Json;

namespace Consolify.Services;

/// <summary>
/// Resolves a title to a Steam app id, with no key and no account.
///
/// This is what keeps the proxy cheap and the launcher useful offline of it. Most PC games sold on
/// Epic, GOG and the Xbox app are also on Steam, so a Cyberpunk 2077 bought from GOG can use the
/// same full-resolution capsule, hero and logo as the Steam copy -- for free, at Steam's per-IP
/// rate limit rather than one shared credential's. Only what is genuinely not on Steam -- Epic and
/// Game Pass exclusives, GOG-only classics, ROMs, itch.io games -- has to reach the proxy at all.
///
/// The endpoint is the store's own search, and it is as undocumented as appdetails. It answers
/// with the exact title first and the DLC alongside, which is exactly the shape TitleMatch is
/// built to sort out.
/// </summary>
public class SteamSearchClient
{
    private readonly HttpClient _http;

    public SteamSearchClient(HttpClient http) => _http = http;

    /// <summary>The app id for this title, or null if Steam has nothing that matches confidently.</summary>
    public async Task<string?> FindAppIdAsync(string title, CancellationToken ct)
    {
        try
        {
            var url = "https://store.steampowered.com/api/storesearch/" +
                      $"?term={Uri.EscapeDataString(title)}&cc=us&l=en";
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array) return null;

            // Same rule as everywhere else: exact after normalising, or nothing. Steam's search is
            // fuzzy by design -- "Portal" returns the whole franchise -- so this is the only thing
            // standing between a GOG entry and the wrong game's capsule.
            if (!TitleMatch.TryBestMatch(title, items.EnumerateArray().ToList(),
                    e => e.TryGetProperty("name", out var n) ? n.GetString() : null, out var hit))
            {
                Log.Info($"Steam search: no confident match for '{title}'");
                return null;
            }

            if (JsonNum.Int(hit, "id") is not { } appId) return null;
            Log.Info($"Steam search: '{title}' resolved to app {appId}");
            return appId.ToString();
        }
        catch (Exception ex)
        {
            Log.Info($"Steam search for '{title}' failed: {ex.Message}");
            return null;
        }
    }
}
