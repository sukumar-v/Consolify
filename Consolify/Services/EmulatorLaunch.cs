using System.IO;
using Consolify.Models;

namespace Consolify.Services;

/// <summary>What actually gets started for an emulated game: the emulator, its arguments with
/// every token filled in, and the emulator's own folder as the working directory.</summary>
public sealed record EmulatorCommand(string Exe, string Args, string WorkingDir, EmulatorDef Emulator);

/// <summary>
/// Turns a ROM entry plus its folder's and emulator's settings into a command line.
///
/// Three layers of arguments, most specific first: the game's own (set under Manage), the ROM
/// folder's, then the emulator's template. Whichever is used is expanded in full -- the layers
/// replace each other rather than adding up, because "add my flag to the template" and "run it
/// with exactly this" are both things people want, and only replacing lets them have either.
/// </summary>
public static class EmulatorLaunch
{
    /// <summary>Null, with the reason, when the game cannot be started: the emulator has been
    /// removed, its exe has gone, the ROM has gone, or the folder wants a core that was never set.</summary>
    public static EmulatorCommand? Resolve(Game game, EmulatorDef? emulator, RomFolderDef? folder, out string? problem)
    {
        problem = null;
        if (!game.Emulated || game.RomPath is null) { problem = "This entry is not an emulated game"; return null; }
        if (emulator is null) { problem = $"No emulator is set up for {game.Platform}. Add one under Settings → Library"; return null; }
        if (string.IsNullOrWhiteSpace(emulator.ExePath) || !File.Exists(emulator.ExePath))
            { problem = $"{emulator.Name}'s program was not found at {emulator.ExePath}"; return null; }
        if (!File.Exists(game.RomPath)) { problem = $"The ROM file is missing: {game.RomPath}"; return null; }

        var template = FirstUseful(game.Args, folder?.Args, emulator.Args) ?? "\"{rom}\"";
        // A template that never names the ROM is almost certainly a mistake -- a flag typed on its
        // own -- and an emulator started with no game is a launch that looks like it did nothing.
        if (!template.Contains("{rom", StringComparison.OrdinalIgnoreCase)) template += " \"{rom}\"";

        if (template.Contains("{core}", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(folder?.Core))
        {
            problem = $"{emulator.Name} needs a core for {game.Platform}. Choose one for the ROM folder under Settings → Library";
            return null;
        }

        var emuDir = Path.GetDirectoryName(emulator.ExePath) ?? "";
        var args = Expand(template, game.RomPath, folder?.Core, emuDir);
        return new EmulatorCommand(emulator.ExePath, args, emuDir, emulator);
    }

    private static string? FirstUseful(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    /// <summary>The tokens, case-insensitively. Anything the template does not mention is simply
    /// not there; nothing is appended behind the person's back except the ROM itself, above.</summary>
    public static string Expand(string template, string romPath, string? core, string emuDir)
    {
        string Rep(string text, string token, string value) =>
            text.Replace(token, value, StringComparison.OrdinalIgnoreCase);

        var text = template;
        text = Rep(text, "{romdir}", Path.GetDirectoryName(romPath) ?? "");
        text = Rep(text, "{romname}", Path.GetFileNameWithoutExtension(romPath));
        text = Rep(text, "{romfile}", Path.GetFileName(romPath));
        text = Rep(text, "{rom}", romPath);
        text = Rep(text, "{core}", core ?? "");
        text = Rep(text, "{emudir}", emuDir);
        return text;
    }

    /// <summary>
    /// The core a RetroArch folder should start with, if one of the platform's known cores is
    /// installed: the first of the platform's list that exists under the emulator's cores folder.
    /// Null when the emulator does not use cores, or none of them is installed.
    /// </summary>
    public static string? SuggestCore(EmulatorDef emulator, EmulatedPlatforms.Def platform)
    {
        if (!emulator.Args.Contains("{core}", StringComparison.OrdinalIgnoreCase)) return null;
        var dir = CoresDir(emulator);
        if (dir is null) return null;
        foreach (var core in platform.Cores)
        {
            var path = Path.Combine(dir, core);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>RetroArch keeps its cores in "cores" beside the exe. Null when there is no such folder.</summary>
    public static string? CoresDir(EmulatorDef emulator)
    {
        var emuDir = Path.GetDirectoryName(emulator.ExePath);
        if (emuDir is null) return null;
        var dir = Path.Combine(emuDir, "cores");
        return Directory.Exists(dir) ? dir : null;
    }
}
