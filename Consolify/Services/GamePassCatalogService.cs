using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Consolify.Models;

namespace Consolify.Services;

public class GamePassStatus
{
    public int Count { get; set; }
    public DateTime? FetchedAt { get; set; }
    public string? Error { get; set; }
}

public class GamePassEntry
{
    public string ProductId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? PackageFamilyName { get; set; }
    public string? CoverUrl { get; set; }
    public string? BackdropUrl { get; set; }
}

public class GamePassCache
{
    public DateTime FetchedAt { get; set; }
    public List<GamePassEntry> Entries { get; set; } = new();
}

/// <summary>
/// Every game included with PC Game Pass, from Microsoft's own catalogue, with no key and no
/// account.
///
/// Two endpoints, both public and both used by the Xbox app itself: a "sigl" (a curated list --
/// this one is "All PC Games") that answers with product ids, and the display catalogue that
/// turns product ids into titles, package family names and art. The catalogue is asked in
/// batches of twenty and answers with everything it knows, screenshots included, so the whole
/// list is a few dozen requests and some tens of megabytes; it is held for a week on disk,
/// because the list changes a handful of times a month.
///
/// This is a catalogue, not a library. A subscriber "owns" all of it in the sense that matters
/// from a sofa -- any of it can be installed and played -- which is why it sits behind its own
/// toggle rather than the ownership one. Games the account has actually played arrive through
/// Galaxy's Xbox connection under the same ids, so the two lists merge cleanly.
/// </summary>
public class GamePassCatalogService
{
    private const string PcGamesSigl = "fdd9e2a7-0fee-49f6-ad69-4354098401ff";
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly HttpClient Http = CreateClient();

    private GamePassCache? _cache;
    private bool _cacheLoaded;

    public GamePassStatus Status { get; } = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        c.DefaultRequestHeaders.Add("User-Agent", "Consolify/1.0 (+https://github.com/consolify)");
        return c;
    }

    public async Task<List<Game>> GetAsync(bool force, CancellationToken ct = default)
    {
        var cached = Cached();
        if (!force && cached is not null && DateTime.UtcNow - cached.FetchedAt < Freshness)
        {
            Status.Error = null;
            return Report(cached);
        }

        try
        {
            var ids = await FetchIdsAsync(ct);
            var entries = new List<GamePassEntry>();
            foreach (var chunk in ids.Chunk(20))
            {
                ct.ThrowIfCancellationRequested();
                entries.AddRange(await FetchProductsAsync(chunk, ct));
            }
            if (entries.Count == 0) throw new InvalidOperationException("the catalogue came back empty");

            var cache = new GamePassCache { FetchedAt = DateTime.UtcNow, Entries = entries };
            SaveCache(cache);
            Status.Error = null;
            Log.Info($"Game Pass: {entries.Count} PC game(s) in the catalogue");
            return Report(cache);
        }
        catch (Exception ex)
        {
            Status.Error = cached is not null
                ? $"Could not reach the Game Pass catalogue; showing the list from {cached.FetchedAt.ToLocalTime():d MMM}"
                : "Could not reach the Game Pass catalogue";
            Log.Info($"Game Pass: {ex.Message}");
            return Report(cached);
        }
    }

    private List<Game> Report(GamePassCache? cache)
    {
        Status.Count = cache?.Entries.Count ?? 0;
        Status.FetchedAt = cache?.FetchedAt;
        if (cache is null) return new List<Game>();
        return cache.Entries.Select(e => new Game
        {
            Id = $"xbox:store:{e.ProductId}",
            Title = e.Title,
            Platform = "Xbox",
            Installed = false,
            PackageFamilyName = e.PackageFamilyName,
            InstallUri = $"ms-windows-store://pdp/?productid={e.ProductId}",
            RemoteCoverUrl = e.CoverUrl,
            RemoteBackdropUrl = e.BackdropUrl,
        }).ToList();
    }

    private static async Task<List<string>> FetchIdsAsync(CancellationToken ct)
    {
        var url = $"https://catalog.gamepass.com/sigls/v2?id={PcGamesSigl}&language=en-us&market=US";
        using var res = await Http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        // The first element describes the list itself; the rest are { id }.
        return doc.RootElement.EnumerateArray()
            .Select(e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.ToUpperInvariant())
            .Distinct()
            .ToList();
    }

    private static async Task<List<GamePassEntry>> FetchProductsAsync(string[] ids, CancellationToken ct)
    {
        var url = "https://displaycatalog.mp.microsoft.com/v7.0/products" +
                  $"?bigIds={string.Join(",", ids)}&market=US&languages=en-us&MS-CV=DGU1mcuYo0WMMp+F.1";
        using var res = await Http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));

        var list = new List<GamePassEntry>();
        if (!doc.RootElement.TryGetProperty("Products", out var products) || products.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var p in products.EnumerateArray())
        {
            var productId = Str(p, "ProductId");
            if (productId is null) continue;
            if (!p.TryGetProperty("LocalizedProperties", out var lps) || lps.ValueKind != JsonValueKind.Array
                || lps.GetArrayLength() == 0) continue;
            var lp = lps[0];
            var title = Str(lp, "ProductTitle");
            if (string.IsNullOrWhiteSpace(title)) continue;

            string? cover = null, backdrop = null, titledHero = null;
            if (lp.TryGetProperty("Images", out var images) && images.ValueKind == JsonValueKind.Array)
                foreach (var img in images.EnumerateArray())
                {
                    var purpose = Str(img, "ImagePurpose");
                    var uri = Str(img, "Uri");
                    if (uri is null) continue;
                    if (uri.StartsWith("//")) uri = "https:" + uri;
                    switch (purpose)
                    {
                        case "Poster": cover ??= uri; break;            // 2:3 box art
                        case "SuperHeroArt": backdrop ??= uri; break;   // 16:9 key art
                        case "TitledHeroArt": titledHero ??= uri; break;
                    }
                }

            string? pfn = null;
            if (p.TryGetProperty("Properties", out var props) && props.ValueKind == JsonValueKind.Object)
                pfn = Str(props, "PackageFamilyName");

            list.Add(new GamePassEntry
            {
                ProductId = productId.ToUpperInvariant(),
                Title = CleanTitle(title!),
                PackageFamilyName = pfn,
                CoverUrl = cover,
                BackdropUrl = backdrop ?? titledHero,
            });
        }
        return list;
    }

    /// <summary>
    /// The Store names the PC build of a cross-platform game after the platform -- "A Plague Tale:
    /// Requiem - Windows", "Grounded (Windows)" -- which is an artefact of the listing, not the
    /// game's name, and it would stop the strict title match against Steam ever succeeding.
    /// </summary>
    private static string CleanTitle(string title) =>
        Regex.Replace(title, @"\s*(?:[-–:]\s*|\()?(?:for\s+)?(?:Windows(?:\s+1[01])?|PC)(?:\s+Edition)?\)?\s*$",
            "", RegexOptions.IgnoreCase).Trim();

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private GamePassCache? Cached()
    {
        if (!_cacheLoaded)
        {
            _cacheLoaded = true;
            try
            {
                if (File.Exists(Paths.GamePassFile))
                    _cache = JsonSerializer.Deserialize<GamePassCache>(File.ReadAllText(Paths.GamePassFile));
            }
            catch (Exception ex) { Log.Info($"Game Pass cache unreadable: {ex.Message}"); }
        }
        return _cache;
    }

    private void SaveCache(GamePassCache cache)
    {
        _cache = cache;
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.GamePassFile, JsonSerializer.Serialize(cache, JsonOpts));
        }
        catch (Exception ex) { Log.Info($"Game Pass cache not written: {ex.Message}"); }
    }
}
