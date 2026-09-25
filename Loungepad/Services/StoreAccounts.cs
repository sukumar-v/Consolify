using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>What the Settings row for a store account shows, on every state push.</summary>
public class StoreStatus
{
    public bool SignedIn { get; set; }
    /// <summary>The account's display name, so the row can say whose library this is.</summary>
    public string? User { get; set; }
    public int Count { get; set; }
    public DateTime? FetchedAt { get; set; }
    /// <summary>Why the last fetch did not answer, in words meant for the row. Null when it did.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// A store the user signs in to, the way Playnite's library plugins work: an interactive sign-in
/// through the store's own web page, a token kept on this PC, and the store's API asked for the
/// library on every scan. Nothing here is required for the launcher to work; a store that is
/// not signed in simply contributes nothing.
/// </summary>
public interface IStoreAccount
{
    /// <summary>"epic", "gog" or "xbox" -- the key the page uses and the file names on disk.</summary>
    string Store { get; }
    string DisplayName { get; }
    StoreStatus Status { get; }

    /// <summary>Opens the sign-in window and returns once the user has signed in or closed it.</summary>
    Task<bool> SignInAsync(MainWindow owner);
    /// <summary>Forgets the token and the browser session. The next scan drops the store's games.</summary>
    void SignOut();
    /// <summary>The account's library, or the last good answer, or nothing -- never a throw.</summary>
    Task<List<Game>> GetOwnedAsync(bool force, CancellationToken ct = default);
}

/// <summary>The last successful library answer for one store, so a start with no network keeps it.</summary>
public class OwnedCache
{
    public DateTime FetchedAt { get; set; }
    public List<Game> Games { get; set; } = new();
}

/// <summary>
/// Where a store's credentials and cached library live.
///
/// Tokens are encrypted with DPAPI for the current Windows user before they touch the disk.
/// That is what Playnite does too, and it is the right level: a refresh token is a password's
/// equal, and settings.json is plain text that people paste into bug reports. The library cache
/// is plain JSON, because a list of game titles is not a secret and it is useful to be able to
/// read it when something looks wrong.
///
/// Each store also gets a WebView2 profile of its own, so its sign-in cookies persist between
/// runs (a silent refresh may need them) and signing out of one store cannot sign you out of
/// another.
/// </summary>
public static class AccountStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Dir { get; } = Path.Combine(Paths.DataDir, "accounts");

    /// <summary>Under Local rather than Roaming, like the launcher's own WebView2 profile: it is a
    /// browser cache and WebView2 will not serve a mapped folder that contains one.</summary>
    public static string ProfileDir(string store) => Path.Combine(Paths.LocalDir, "webview2-accounts", store);

    private static string SecretFile(string store) => Path.Combine(Dir, $"{store}.bin");
    private static string CacheFile(string store) => Path.Combine(Dir, $"{store}-library.json");

    public static bool HasSecret(string store) => File.Exists(SecretFile(store));

    public static void SaveSecret<T>(string store, T value)
    {
        Directory.CreateDirectory(Dir);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        var sealedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SecretFile(store), sealedBytes);
    }

    public static T? LoadSecret<T>(string store) where T : class
    {
        try
        {
            if (!File.Exists(SecretFile(store))) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(SecretFile(store)), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            // A token written by another Windows account, or a corrupt file. Either way the user
            // signs in again, which is the same outcome as an expired token.
            Log.Info($"{store}: stored sign-in unreadable ({ex.Message})");
            return null;
        }
    }

    public static void DeleteSecret(string store)
    {
        try { File.Delete(SecretFile(store)); } catch { /* already gone */ }
    }

    public static void SaveCache(string store, OwnedCache cache)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(CacheFile(store), JsonSerializer.Serialize(cache, JsonOpts));
        }
        catch (Exception ex) { Log.Info($"{store}: library cache not written ({ex.Message})"); }
    }

    public static OwnedCache? LoadCache(string store)
    {
        try
        {
            return File.Exists(CacheFile(store))
                ? JsonSerializer.Deserialize<OwnedCache>(File.ReadAllText(CacheFile(store)))
                : null;
        }
        catch (Exception ex)
        {
            Log.Info($"{store}: library cache unreadable ({ex.Message})");
            return null;
        }
    }

    public static void DeleteCache(string store)
    {
        try { File.Delete(CacheFile(store)); } catch { /* already gone */ }
    }

    /// <summary>Best effort: a profile in use by an open window cannot be removed, and the next
    /// sign-in overwrites the cookies anyway.</summary>
    public static void ClearProfile(string store)
    {
        try { Directory.Delete(ProfileDir(store), recursive: true); }
        catch (Exception ex) { Log.Info($"{store}: browser profile not cleared ({ex.Message})"); }
    }

    /// <summary>Shared by every client: the fetch policy that makes a store account cost nothing
    /// when the network is down or the store is having a day.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(6);
}
