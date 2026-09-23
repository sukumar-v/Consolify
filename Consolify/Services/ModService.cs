using System.IO;
using System.Net.Http;
using Consolify.Models;

namespace Consolify.Services;

/// <summary>
/// Everything the page sees about a game's mods, in one message. <see cref="State"/> is what the
/// page switches on, and every state that is not "ready" carries the sentence that explains it,
/// so the page never has to work out what went wrong from the pieces.
/// </summary>
public sealed class ModsView
{
    public string GameId { get; set; } = "";
    /// <summary>notInstalled | needsRestart | unavailable | unsupported | notManaged | ready | error</summary>
    public string State { get; set; } = "error";
    public string? Message { get; set; }
    public ModManagerStatus Vortex { get; set; } = new();
    public ModGame? Game { get; set; }
    public List<ModInfo> Mods { get; set; } = new();
    /// <summary>Questions Vortex is waiting on. The page offers each one's buttons.</summary>
    public List<ModPrompt> Prompts { get; set; } = new();
    /// <summary>Warnings and errors Vortex is showing, one line each.</summary>
    public List<string> Notices { get; set; } = new();
    /// <summary>For a game Vortex has no extension for: the extensions in its catalogue that look
    /// like they are for this game, exact matches first.</summary>
    public List<ModExtension> Extensions { get; set; } = new();
    public string DownloadUrl { get; set; } = VortexBackend.DownloadUrl;
}

/// <summary>
/// The mod side of the library: which manager is here, which of its games is which of ours, and
/// the list, the toggles and the removals for one game at a time. One backend today (Vortex);
/// the shape leaves room for another, and nothing on the page names Vortex except the copy.
/// </summary>
public sealed class ModService
{
    private readonly VortexBackend _vortex;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>The last games answer, so a repeat visit to the same game costs one request.</summary>
    private List<ModGame> _games = new();

    public ModService(Func<string?> vortexPath) => _vortex = new VortexBackend(vortexPath);

    public VortexBackend Vortex => _vortex;

    /// <summary>What Settings shows: installed or not, and where. No Vortex is started for it.
    /// <paramref name="refresh"/> looks on disk again; without it the last answer is used, since
    /// this rides on every state push.</summary>
    public ModManagerStatus Status(bool refresh = false)
    {
        if (refresh) _vortex.Locate();
        return new ModManagerStatus
        {
            Installed = _vortex.ExePath is not null,
            Path = _vortex.ExePath,
            Version = _vortex.Version,
            Running = VortexBackend.IsRunning,
        };
    }

    /// <summary>Can this game have mods at all: something on disk that a manager could deploy into.</summary>
    public static bool Eligible(Game g) => g.Installed && !g.Emulated && !string.IsNullOrEmpty(g.InstallDir);

    /// <summary>
    /// The Vortex game whose install folder is this game's. Never by title -- Vortex's names and
    /// the stores' names differ, and a wrong match would list another game's mods with nothing
    /// about it looking wrong. Equal paths first; otherwise the one nested inside the other, which
    /// is how a game whose exe sits in a subfolder comes out.
    /// </summary>
    public static ModGame? Match(Game g, IEnumerable<ModGame> games)
    {
        var mine = Norm(g.InstallDir);
        if (mine is null) return null;
        ModGame? best = null;
        var bestLen = -1;
        foreach (var vg in games)
        {
            var theirs = Norm(vg.Path);
            if (theirs is null) continue;
            var score = theirs == mine ? int.MaxValue
                : theirs.StartsWith(mine + "\\", StringComparison.Ordinal) || mine.StartsWith(theirs + "\\", StringComparison.Ordinal)
                    ? Math.Min(theirs.Length, mine.Length)
                    : -1;
            if (score > bestLen) { best = vg; bestLen = score; }
        }
        return best;
    }

    private static string? Norm(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return null; }
    }

    /// <summary>Everything the Mods screen needs for a game, starting Vortex if it has to.</summary>
    public async Task<ModsView> OpenAsync(Game game, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await BuildAsync(game, null, ct); }
        finally { _gate.Release(); }
    }

    public Task<ModsView> SetEnabledAsync(Game game, string modId, bool enabled, CancellationToken ct) =>
        ActAsync(game, (vg, c) => _vortex.SetEnabledAsync(vg.Id, new[] { modId }, enabled, c), ct);

    public Task<ModsView> RemoveAsync(Game game, string modId, CancellationToken ct) =>
        ActAsync(game, (vg, c) => _vortex.RemoveAsync(vg.Id, modId, c), ct);

    public Task<ModsView> DeployAsync(Game game, CancellationToken ct) =>
        ActAsync(game, async (vg, c) => { await _vortex.DeployAsync(vg.Id, c); return null; }, ct);

    /// <summary>Press a button on one of Vortex's questions, then give whatever it was waiting on
    /// -- usually an install -- a moment to move before the list is read again.</summary>
    public Task<ModsView> AnswerAsync(Game game, string dialogId, string action, CancellationToken ct) =>
        ActAsync(game, async (vg, c) =>
        {
            await _vortex.AnswerAsync(dialogId, action, c);
            await Task.Delay(1500, c);
            return null;
        }, ct);

    /// <summary>
    /// Set the game up in Vortex and switch to it: locate it first if Vortex knows it only by
    /// name, make its first profile if it has none, then the switch, which is what creates the
    /// staging folder and asks Vortex's first-time questions. Those come back as prompts on the
    /// screen this returns. <c>NeedsWindow</c> is true only when Vortex stopped without a
    /// question the screen can carry, which is when its own window is the only way on.
    /// </summary>
    public async Task<(ModsView View, bool NeedsWindow)> ManageAsync(Game game, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string? failure = null;
            var active = false;
            try
            {
                var status = await _vortex.EnsureBridgeAsync(ct);
                if (status.BridgeReady)
                {
                    _games = await _vortex.GamesAsync(ct);
                    var vg = Match(game, _games);
                    if (vg is null && KnownByName(game, _games) is { } known && !string.IsNullOrEmpty(game.InstallDir))
                    {
                        await _vortex.DiscoverAsync(known.Id, game.InstallDir, StoreOf(game.Platform), ct);
                        _games = await _vortex.GamesAsync(ct);
                        vg = Match(game, _games) ?? known;
                    }
                    if (vg is not null) active = (await _vortex.ActivateAsync(vg.Id, ct)).Active;
                }
            }
            catch (VortexBridgeException ex) { failure = ex.Message; }
            catch (HttpRequestException ex) { failure = $"Lost the connection to Vortex: {ex.Message}"; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "Vortex took too long to answer"; }

            var view = await BuildAsync(game, null, ct);
            if (failure is not null) view.Message = failure;
            var needsWindow = !active && view.State != "ready" && view.Prompts.Count == 0 && failure is null;
            return (view, needsWindow);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A game Vortex knows -- its extension is installed -- but has never located, so it has only
    /// a name. This is the one place a title is matched, and only exactly after normalising, the
    /// way TitleMatch demands everywhere; and nothing acts on the match until the user presses
    /// the row that says what it will do.
    /// </summary>
    private static ModGame? KnownByName(Game game, IEnumerable<ModGame> games)
    {
        var hits = games.Where(g => string.IsNullOrEmpty(g.Path) && TitleMatch.IsConfident(game.Title, g.Name)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Vortex's name for the store a game came from, for the record it keeps of where it found it.</summary>
    private static string? StoreOf(string platform) => platform switch
    {
        "Steam" => "steam", "GOG" => "gog", "Epic" => "epic", "Xbox" => "xbox", _ => null,
    };

    /// <summary>Run one change through the bridge and hand back the screen afterwards. A bridge
    /// error becomes the screen's message rather than a toast, because the list is still worth
    /// showing with the failure written across the top of it.</summary>
    private async Task<ModsView> ActAsync(Game game, Func<ModGame, CancellationToken, Task<List<ModInfo>?>> act, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var status = await _vortex.EnsureBridgeAsync(ct);
            if (!status.BridgeReady) return await BuildAsync(game, null, ct);
            var vg = Match(game, _games.Count > 0 ? _games : _games = await _vortex.GamesAsync(ct));
            if (vg is null || !vg.Managed) return await BuildAsync(game, null, ct);
            string? failure = null;
            List<ModInfo>? mods = null;
            try { mods = await act(vg, ct); }
            catch (VortexBridgeException ex) { failure = ex.Message; }
            catch (HttpRequestException ex) { failure = $"Lost the connection to Vortex: {ex.Message}"; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = "Vortex took too long to answer"; }
            var view = await BuildAsync(game, mods, ct);
            if (failure is not null) view.Message = failure;
            return view;
        }
        finally { _gate.Release(); }
    }

    private async Task<ModsView> BuildAsync(Game game, List<ModInfo>? mods, CancellationToken ct)
    {
        var view = new ModsView { GameId = game.Id };
        try
        {
            // Look again every time: the Get Vortex row exists so that this answer can change
            // while the launcher is up.
            _vortex.Locate();
            var status = await _vortex.EnsureBridgeAsync(ct);
            view.Vortex = status;
            if (!status.Installed)
            {
                view.State = "notInstalled";
                view.Message = "Vortex, the Nexus Mods manager, is not installed on this PC";
                return view;
            }
            if (!status.BridgeReady)
            {
                view.State = status.NeedsRestart ? "needsRestart" : "unavailable";
                view.Message = status.Error;
                return view;
            }

            _games = await _vortex.GamesAsync(ct);
            var vg = Match(game, _games);
            view.Game = vg;
            // Whatever state the game is in, a question Vortex is showing is the first thing to
            // say: its first-time setup of a game asks them, and so does an odd archive.
            try { (view.Prompts, view.Notices) = await _vortex.AttentionAsync(ct); } catch { /* cosmetic */ }
            if (vg is null && KnownByName(game, _games) is { } known)
            {
                view.Game = known;
                view.State = "notDiscovered";
                view.Message = $"Vortex has the extension for {game.Title} but has not been told where the game is. Consolify knows: {game.InstallDir}";
                return view;
            }
            if (vg is null)
            {
                view.State = "unsupported";
                // Vortex only knows a game through an extension, and its catalogue says which
                // exist. The list is offered rather than acted on: the match is by name.
                try { view.Extensions = (await _vortex.ExtensionsAsync(game.Title, ct)).OrderByDescending(e => e.Exact).ThenBy(e => e.Name).ToList(); }
                catch { /* the catalogue needs the network; without it the rows are just absent */ }
                view.Message = view.Extensions.Count > 0
                    ? $"Vortex has no extension for {game.Title} yet. An extension is what teaches Vortex a game; these look like they are for it"
                    : $"Vortex has not found {game.Title}. Vortex learns a game through an extension, and its catalogue has none that looks like this one. It may also simply not have looked for the game yet";
                return view;
            }
            if (!vg.Managed)
            {
                view.State = "notManaged";
                view.Message = $"Vortex knows {game.Title} but has not been set up for it. That first step -- a folder for the mods, how they are deployed -- is done in Vortex's own window, once";
                return view;
            }
            view.Mods = mods ?? await _vortex.ModsAsync(vg.Id, ct);
            view.State = "ready";
        }
        catch (VortexBridgeException ex)
        {
            view.State = "error";
            view.Message = ex.Message;
        }
        catch (HttpRequestException ex)
        {
            view.State = "error";
            view.Message = $"Could not reach Vortex: {ex.Message}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            view.State = "error";
            view.Message = "Vortex took too long to answer";
        }
        return view;
    }

    /// <summary>The Nexus Mods page for a game: its own section when Vortex named one, else the
    /// games index.</summary>
    public static string NexusUrl(ModGame? vg) =>
        vg?.NexusDomain is { Length: > 0 } d ? $"https://www.nexusmods.com/{d}/mods/" : "https://www.nexusmods.com/games";

    /// <summary>One mod's own page. The section is the mod's, then the game's; a section name is
    /// only ever letters and digits, and anything else is refused rather than put in a URL.</summary>
    public static string? ModUrl(ModGame? vg, long nexusModId, string? modDomain)
    {
        var domain = modDomain is { Length: > 0 } && modDomain.All(c => char.IsAsciiLetterOrDigit(c)) ? modDomain : vg?.NexusDomain;
        if (nexusModId <= 0 || domain is null || !domain.All(c => char.IsAsciiLetterOrDigit(c))) return null;
        return $"https://www.nexusmods.com/{domain}/mods/{nexusModId}";
    }

    /// <summary>Vortex's own extension browser, on one extension, so its Install button is a click away.</summary>
    public async Task<bool> ShowExtensionAsync(long modId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var status = await _vortex.EnsureBridgeAsync(ct);
            if (!status.BridgeReady) return false;
            await _vortex.ShowExtensionAsync(modId, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The Vortex game matched to this one on the last answer, without asking again.</summary>
    public ModGame? Cached(Game game) => Match(game, _games);
}
