namespace Consolify.Models;

/// <summary>
/// One mod as the mod manager reports it. Everything here is the manager's own data, read
/// through the bridge and never edited by Consolify: the manager owns the files on disk, the
/// deployment and the load order, and Consolify only ever asks it to flip a switch.
/// </summary>
public sealed class ModInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Category { get; set; }
    /// <summary>The Nexus mod id, when the mod came from Nexus. Null for a mod installed from a
    /// file, which is common enough that the UI has to cope with it.</summary>
    public long? NexusModId { get; set; }
    /// <summary>The Nexus section the mod was downloaded from, when it was; it can differ from
    /// the game's own. Null for a mod installed from a file.</summary>
    public string? NexusDomain { get; set; }
    /// <summary>"installed", "installing" or "downloaded": Vortex keeps a mod on its list from the
    /// moment the archive lands. Only an installed one can be enabled.</summary>
    public string State { get; set; } = "installed";
    public bool Enabled { get; set; }
}

/// <summary>
/// A game as the mod manager knows it. <see cref="Path"/> is what it is matched to a library
/// entry on -- never the title -- and <see cref="Managed"/> says whether the manager has been
/// through its own first-time setup for the game (a staging folder, a deployment method), which
/// only its own window can do.
/// </summary>
public sealed class ModGame
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public bool Managed { get; set; }
    /// <summary>The game's section of nexusmods.com ("skyrimspecialedition"), which is where the
    /// Browse button goes. Null for a game Vortex supports through a community extension that
    /// did not say.</summary>
    public string? NexusDomain { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>
/// A question Vortex is showing and waiting on. The fallback installer's "install this anyway?"
/// is the usual one, raised by any archive whose layout the game's extension does not know. The
/// buttons come as labels, and a label is also what answering takes; a dialog that wants more
/// than a button -- a checkbox, a choice, a typed path -- is reported but not answerable here.
/// </summary>
public sealed class ModPrompt
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? Message { get; set; }
    public List<string> Actions { get; set; } = new();
    public string? DefaultAction { get; set; }
    public bool Answerable { get; set; }
}

/// <summary>
/// A game extension Vortex could install, from the catalogue Vortex keeps of them. Offered when
/// Vortex has no extension for a game: installing one is what turns "Vortex has not found this
/// game" into a game it can manage. <see cref="Exact"/> means the catalogue's game name is this
/// game's title after normalising; the rest are near misses shown by name for the user to judge.
/// </summary>
public sealed class ModExtension
{
    public long ModId { get; set; }
    public long FileId { get; set; }
    public string Name { get; set; } = "";
    public string? GameName { get; set; }
    public string? GameDomain { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public bool Exact { get; set; }
}

/// <summary>Where the mod manager stands, for the Settings row and the Mods screen's first line.</summary>
public sealed class ModManagerStatus
{
    public bool Installed { get; set; }
    public string? Path { get; set; }
    public string? Version { get; set; }
    public bool Running { get; set; }
    /// <summary>The bridge extension answered on this run. False while Vortex is starting, and
    /// false for good if Vortex was already running when the extension was first copied in --
    /// Vortex only loads extensions at startup.</summary>
    public bool BridgeReady { get; set; }
    public bool NeedsRestart { get; set; }
    public string? Error { get; set; }
}
