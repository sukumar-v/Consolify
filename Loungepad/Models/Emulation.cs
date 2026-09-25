namespace Loungepad.Models;

/// <summary>
/// A program that runs ROMs. Set up once under Settings → Library and then named by the ROM
/// folders that use it; an emulator is never scanned for, because there is nothing on disk that
/// says "this exe is an emulator" and guessing would put things like a video player in the list.
/// </summary>
public class EmulatorDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    /// <summary>
    /// The command line, as a template. "{rom}" is the ROM's full path, "{romdir}" its folder,
    /// "{romname}" its file name without the extension, "{romfile}" with it, "{core}" the core a
    /// ROM folder chose (RetroArch) and "{emudir}" the emulator's own folder. Quoting is the
    /// template's job -- the presets write "{rom}" in quotes -- so an argument that must NOT be
    /// quoted (MAME's set name) can be written without them.
    /// </summary>
    public string Args { get; set; } = "\"{rom}\"";
    /// <summary>Which preset filled this in, if any. Informational: the presets are only ever a
    /// starting point, and everything they wrote can be edited.</summary>
    public string? Preset { get; set; }
    /// <summary>The systems this emulator is known to run, by platform id. Only used to put the
    /// likely emulator first when a ROM folder is being set up; a folder can pick any.</summary>
    public List<string> Platforms { get; set; } = new();
    /// <summary>Found by the scan rather than added by hand. Informational: it says so on the row.</summary>
    public bool Detected { get; set; }
}

/// <summary>
/// A folder of ROMs for one system, run by one emulator. The folder IS the platform: there is no
/// reliable way to tell a Genesis .bin from an Atari .bin from a PlayStation .bin by looking at the
/// file, and every ROM collection in the world is already sorted into one folder per system.
/// </summary>
public class RomFolderDef
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>One of <see cref="EmulatedPlatforms.All"/>.</summary>
    public string PlatformId { get; set; } = "";
    public string? EmulatorId { get; set; }
    /// <summary>What "{core}" expands to in the emulator's arguments -- a RetroArch core's path.
    /// Per folder rather than per emulator because RetroArch runs every system through the same
    /// exe, and the core is the only thing that says which.</summary>
    public string? Core { get; set; }
    /// <summary>Overrides the emulator's argument template for this folder alone. Same tokens.</summary>
    public string? Args { get; set; }
    /// <summary>Overrides the platform's default file extensions. Lower case, no dot.</summary>
    public List<string>? Extensions { get; set; }
    public bool Recurse { get; set; } = true;
    /// <summary>
    /// When set, <see cref="Path"/> is a RetroArch playlist (.lpl) rather than a folder, and the
    /// games are its entries. A playlist is RetroArch's own curated list for one system -- the
    /// path of every ROM, a database name for it, and the core that plays it -- so it is a
    /// better source than walking a folder, and the only source that works when the ROMs for
    /// several systems sit in one folder together.
    /// </summary>
    public bool Playlist { get; set; }
    /// <summary>Found by the scan rather than added by hand.</summary>
    public bool Detected { get; set; }
}

/// <summary>
/// The systems the launcher knows how to file a ROM under.
///
/// Each carries the file extensions a ROM for it normally has, so a folder can be scanned without
/// picking up save states, manuals and box scans that live beside the games; the IGDB platform
/// ids, so a lookup for "Doom" in the SNES folder asks about the 1993 game rather than the 2016
/// one; and the RetroArch cores that run it, best first, so a RetroArch folder can be set up
/// without typing a core's file name on a gamepad.
///
/// Disc systems deliberately leave ".bin" out: a .bin is one track of a .cue, and listing both
/// would put every game in the library twice (or six times, for a game that has five audio
/// tracks). The .cue, .chd, .m3u and .pbp are the files that stand for a whole game. A folder
/// can override the list.
/// </summary>
public static class EmulatedPlatforms
{
    public sealed record Def(string Id, string Name, string Short, string[] Extensions, int[] IgdbIds, string[] Cores);

    private static Def P(string id, string name, string shortName, string exts, int[] igdb, params string[] cores) =>
        new(id, name, shortName,
            exts.Split(',').Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).ToArray(),
            igdb, cores);

    /// <summary>Cartridge systems are routinely kept zipped, one game per archive.</summary>
    private static string Z(string exts) => exts + ",zip,7z";

    /// <summary>In the order they are offered, roughly by maker and age.</summary>
    public static readonly IReadOnlyList<Def> All = new[]
    {
        // ---- Nintendo ----
        P("nes",       "Nintendo Entertainment System", "NES",           Z("nes,fds,unf,unif"),        new[] { 18, 99, 51 },  "mesen_libretro.dll", "nestopia_libretro.dll", "fceumm_libretro.dll"),
        P("snes",      "Super Nintendo",                "SNES",          Z("sfc,smc,fig,swc,bs"),      new[] { 19, 58 },      "snes9x_libretro.dll", "bsnes_libretro.dll", "mesen-s_libretro.dll"),
        P("n64",       "Nintendo 64",                   "N64",           Z("n64,z64,v64,ndd"),         new[] { 4, 416 },      "mupen64plus_next_libretro.dll", "parallel_n64_libretro.dll"),
        P("gc",        "Nintendo GameCube",             "GameCube",      "iso,gcm,gcz,rvz,ciso,wbfs,dol", new[] { 21 },       "dolphin_libretro.dll"),
        P("wii",       "Nintendo Wii",                  "Wii",           "iso,wbfs,rvz,gcz,ciso,wad",  new[] { 5 },           "dolphin_libretro.dll"),
        P("wiiu",      "Nintendo Wii U",                "Wii U",         "wud,wux,rpx,wua",            new[] { 41 }),
        P("switch",    "Nintendo Switch",               "Switch",        "nsp,xci,nca,nro",            new[] { 130 }),
        P("gb",        "Game Boy",                      "Game Boy",      Z("gb"),                      new[] { 33 },          "gambatte_libretro.dll", "sameboy_libretro.dll", "mgba_libretro.dll"),
        P("gbc",       "Game Boy Color",                "GBC",           Z("gbc"),                     new[] { 22 },          "gambatte_libretro.dll", "sameboy_libretro.dll", "mgba_libretro.dll"),
        P("gba",       "Game Boy Advance",              "GBA",           Z("gba"),                     new[] { 24 },          "mgba_libretro.dll", "vba_next_libretro.dll"),
        P("nds",       "Nintendo DS",                   "DS",            Z("nds,dsi"),                 new[] { 20, 159 },     "melonds_libretro.dll", "desmume_libretro.dll"),
        P("3ds",       "Nintendo 3DS",                  "3DS",           "3ds,cia,cci,cxi,3dsx",       new[] { 37, 137 },     "citra_libretro.dll"),
        P("vb",        "Virtual Boy",                   "Virtual Boy",   Z("vb,vboy"),                 new[] { 87 },          "mednafen_vb_libretro.dll"),
        // ---- Sony ----
        P("ps1",       "PlayStation",                   "PS1",           "cue,chd,pbp,m3u,ecm,mds",    new[] { 7 },           "mednafen_psx_hw_libretro.dll", "swanstation_libretro.dll", "pcsx_rearmed_libretro.dll", "mednafen_psx_libretro.dll"),
        P("ps2",       "PlayStation 2",                 "PS2",           "iso,chd,cso,zso,gz",         new[] { 8 },           "pcsx2_libretro.dll", "play_libretro.dll"),
        P("psp",       "PlayStation Portable",          "PSP",           "iso,cso,pbp,chd",            new[] { 38 },          "ppsspp_libretro.dll"),
        // ---- Sega ----
        P("sms",       "Sega Master System",            "Master System", Z("sms"),                     new[] { 64 },          "genesis_plus_gx_libretro.dll", "smsplus_libretro.dll"),
        P("genesis",   "Sega Genesis / Mega Drive",     "Genesis",       Z("md,gen,smd,bin"),          new[] { 29 },          "genesis_plus_gx_libretro.dll", "picodrive_libretro.dll", "blastem_libretro.dll"),
        P("segacd",    "Sega CD",                       "Sega CD",       "cue,chd,iso,m3u",            new[] { 78 },          "genesis_plus_gx_libretro.dll", "picodrive_libretro.dll"),
        P("32x",       "Sega 32X",                      "32X",           Z("32x"),                     new[] { 30 },          "picodrive_libretro.dll"),
        P("saturn",    "Sega Saturn",                   "Saturn",        "cue,chd,iso,m3u,ccd,mds",    new[] { 32 },          "mednafen_saturn_libretro.dll", "kronos_libretro.dll", "yabasanshiro_libretro.dll"),
        P("dreamcast", "Sega Dreamcast",                "Dreamcast",     "gdi,chd,cdi,cue,m3u",        new[] { 23 },          "flycast_libretro.dll"),
        P("gg",        "Sega Game Gear",                "Game Gear",     Z("gg"),                      new[] { 35 },          "genesis_plus_gx_libretro.dll"),
        // ---- Microsoft. "Original" because "Xbox" is already the store's name in the library,
        // and a filter that could mean either would be no filter at all. ----
        P("xbox_og",   "Original Xbox",                 "Xbox",          "iso,xiso",                   new[] { 11 }),
        P("x360",      "Xbox 360",                      "Xbox 360",      "iso,xex,zar",                new[] { 12 }),
        // ---- Arcade and the rest ----
        P("arcade",    "Arcade",                        "Arcade",        "zip,7z,chd",                 new[] { 52 },          "fbneo_libretro.dll", "mame_libretro.dll", "mame2003_plus_libretro.dll"),
        P("neogeo",    "Neo Geo",                       "Neo Geo",       "zip,7z",                     new[] { 80, 79, 136 }, "fbneo_libretro.dll", "mame_libretro.dll"),
        P("tg16",      "TurboGrafx-16 / PC Engine",     "TG-16",         Z("pce,sgx"),                 new[] { 86, 128 },     "mednafen_pce_libretro.dll", "mednafen_pce_fast_libretro.dll"),
        P("tgcd",      "TurboGrafx-CD",                 "TG-CD",         "cue,chd,m3u",                new[] { 150 },         "mednafen_pce_libretro.dll", "mednafen_pce_fast_libretro.dll"),
        P("atari2600", "Atari 2600",                    "2600",          Z("a26,bin"),                 new[] { 59 },          "stella_libretro.dll", "stella2014_libretro.dll"),
        P("atari7800", "Atari 7800",                    "7800",          Z("a78"),                     new[] { 60 },          "prosystem_libretro.dll"),
        P("lynx",      "Atari Lynx",                    "Lynx",          Z("lnx"),                     new[] { 61 },          "handy_libretro.dll", "mednafen_lynx_libretro.dll"),
        P("jaguar",    "Atari Jaguar",                  "Jaguar",        Z("j64,jag,rom,abs,cof"),     new[] { 62 },          "virtualjaguar_libretro.dll"),
        P("ngp",       "Neo Geo Pocket",                "NGP",           Z("ngp,ngc"),                 new[] { 119, 120 },    "mednafen_ngp_libretro.dll"),
        P("ws",        "WonderSwan",                    "WonderSwan",    Z("ws,wsc"),                  new[] { 57, 123 },     "mednafen_wswan_libretro.dll"),
        P("3do",       "3DO",                           "3DO",           "cue,chd,iso",                new[] { 50 },          "opera_libretro.dll"),
        P("c64",       "Commodore 64",                  "C64",           Z("d64,t64,prg,crt,g64,tap"), new[] { 15 },          "vice_x64_libretro.dll", "vice_x64sc_libretro.dll"),
        P("amiga",     "Commodore Amiga",               "Amiga",         Z("adf,adz,hdf,ipf,lha"),     new[] { 16 },          "puae_libretro.dll"),
        P("msx",       "MSX",                           "MSX",           Z("rom,mx1,mx2,dsk,cas"),     new[] { 27, 53 },      "bluemsx_libretro.dll", "fmsx_libretro.dll"),
        P("zx",        "ZX Spectrum",                   "Spectrum",      Z("tzx,tap,z80,sna,dsk,trd"), new[] { 26 },          "fuse_libretro.dll"),
        P("intv",      "Intellivision",                 "Intellivision", Z("int,bin,rom"),             new[] { 67 },          "freeintv_libretro.dll"),
        P("coleco",    "ColecoVision",                  "ColecoVision",  Z("col,rom"),                 new[] { 68 },          "gearcoleco_libretro.dll", "bluemsx_libretro.dll"),
    };

    public static Def? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which system a folder called this is probably for. ROM collections are sorted into one
    /// folder per system and named the way people name them -- "SNES", "Super Nintendo", "psx",
    /// "MegaDrive" -- so the name is a strong hint and the wizard puts that system first. Only a
    /// hint: the person confirms, and a folder called "Games" gets no guess at all.
    /// </summary>
    public static string? Guess(string folderName)
    {
        var name = folderName.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Trim();
        var compact = name.Replace(" ", "");
        if (compact.Length == 0) return null;
        // An exact name first. Then the LONGEST name the folder contains -- "roms snes" is still
        // SNES, and "sega genesis" is Genesis, not the "nes" inside it. Two-letter names ("gb",
        // "md") are only ever matched exactly: "gb" is inside far too many words.
        foreach (var (needle, id) in FolderNames)
            if (compact == needle.Replace(" ", "")) return id;
        string? best = null;
        var bestLength = 2;
        foreach (var (needle, id) in FolderNames)
        {
            var n = needle.Replace(" ", "");
            if (n.Length > bestLength && compact.Contains(n)) { best = id; bestLength = n.Length; }
        }
        return best;
    }

    /// <summary>Every name a folder for the system is likely to carry. Order only matters for an
    /// exact match; a contained match is decided by length.</summary>
    private static readonly (string Name, string Id)[] FolderNames =
    {
        // The full name first: "Super Nintendo Entertainment System" also contains the NES's
        // full name, and the longest contained name is the one that wins.
        ("super nintendo entertainment system", "snes"),
        ("super nintendo", "snes"), ("super famicom", "snes"), ("snes", "snes"), ("sfc", "snes"),
        ("nintendo 64", "n64"), ("n64", "n64"),
        ("gamecube", "gc"), ("game cube", "gc"), ("ngc", "gc"), ("gcn", "gc"),
        ("wii u", "wiiu"), ("wiiu", "wiiu"),
        ("switch", "switch"), ("nsw", "switch"),
        ("game boy advance", "gba"), ("gameboy advance", "gba"), ("gba", "gba"),
        ("game boy color", "gbc"), ("gameboy color", "gbc"), ("gbc", "gbc"),
        ("game boy", "gb"), ("gameboy", "gb"),
        ("nintendo ds", "nds"), ("nds", "nds"),
        ("3ds", "3ds"),
        ("virtual boy", "vb"), ("virtualboy", "vb"),
        ("family computer", "nes"), ("famicom", "nes"), ("nintendo entertainment system", "nes"), ("nes", "nes"), ("fds", "nes"),
        ("satellaview", "snes"), ("sufami", "snes"),
        ("playstation 2", "ps2"), ("ps2", "ps2"),
        ("playstation portable", "psp"), ("psp", "psp"),
        ("playstation", "ps1"), ("psx", "ps1"), ("ps1", "ps1"), ("psone", "ps1"),
        ("master system", "sms"), ("mastersystem", "sms"), ("sms", "sms"),
        ("mega drive", "genesis"), ("megadrive", "genesis"), ("genesis", "genesis"),
        ("sega cd", "segacd"), ("segacd", "segacd"), ("mega cd", "segacd"), ("megacd", "segacd"),
        ("32x", "32x"),
        ("saturn", "saturn"),
        ("dreamcast", "dreamcast"),
        ("game gear", "gg"), ("gamegear", "gg"),
        ("xbox 360", "x360"), ("xbox360", "x360"), ("x360", "x360"),
        ("xbox", "xbox_og"),
        ("neo geo pocket", "ngp"), ("ngpc", "ngp"), ("ngp", "ngp"),
        ("neo geo", "neogeo"), ("neogeo", "neogeo"),
        ("arcade", "arcade"), ("mame", "arcade"), ("fbneo", "arcade"),
        ("turbografx cd", "tgcd"), ("pc engine cd", "tgcd"), ("tgcd", "tgcd"),
        ("turbografx", "tg16"), ("pc engine", "tg16"), ("pcengine", "tg16"), ("tg16", "tg16"), ("pce", "tg16"),
        ("atari 2600", "atari2600"), ("2600", "atari2600"),
        ("atari 7800", "atari7800"), ("7800", "atari7800"),
        ("lynx", "lynx"),
        ("jaguar", "jaguar"),
        ("wonderswan", "ws"),
        ("3do", "3do"),
        ("commodore 64", "c64"), ("c64", "c64"),
        ("amiga", "amiga"),
        ("msx", "msx"),
        ("spectrum", "zx"),
        ("intellivision", "intv"),
        ("colecovision", "coleco"), ("coleco", "coleco"),
        // Bare two-letter folders. Kept last and, being two letters, only ever matched exactly.
        ("gb", "gb"), ("gg", "gg"), ("dc", "dreamcast"), ("md", "genesis"), ("vb", "vb"), ("zx", "zx"),
        ("gc", "gc"), ("ds", "nds"),
        ("wii", "wii"),
    };
}

/// <summary>
/// What the launcher already knows about the common emulators, keyed on the exe's file name, so
/// picking RetroArch's exe fills in its name, the "-L core rom" command line and the list of
/// systems it runs, and nothing has to be typed on a gamepad. Every one of these is a starting
/// point that can be edited afterwards; an exe nobody here has heard of gets the plainest
/// possible template, which is the ROM's path in quotes and nothing else.
/// </summary>
public static class EmulatorPresets
{
    public sealed record Preset(string Key, string Name, string[] ExeNames, string Args, string[] Platforms);

    private static Preset P(string key, string name, string exes, string args, string platforms) =>
        new(key, name,
            exes.Split(',').Select(e => e.Trim().ToLowerInvariant()).ToArray(), args,
            platforms.Split(',').Select(e => e.Trim()).Where(e => e.Length > 0).ToArray());

    private const string Rom = "\"{rom}\"";

    public static readonly IReadOnlyList<Preset> All = new[]
    {
        // RetroArch runs everything through one exe, and which system is the core's business;
        // "{core}" is filled in per ROM folder from the platform's list of cores. -f is fullscreen.
        P("retroarch",   "RetroArch",   "retroarch.exe",                        "-L \"{core}\" " + Rom + " -f",
          "nes,snes,n64,gc,wii,gb,gbc,gba,nds,3ds,vb,ps1,ps2,psp,sms,genesis,segacd,32x,saturn,dreamcast,gg,arcade,neogeo,tg16,tgcd,atari2600,atari7800,lynx,jaguar,ngp,ws,3do,c64,amiga,msx,zx,intv,coleco"),
        P("dolphin",     "Dolphin",     "dolphin.exe,dolphinqt2.exe",            "-b -e " + Rom,                     "gc,wii"),
        P("pcsx2",       "PCSX2",       "pcsx2-qt.exe,pcsx2.exe,pcsx2-qtx64.exe,pcsx2-qtx64-avx2.exe", "-batch -fullscreen -- " + Rom, "ps2"),
        P("duckstation", "DuckStation", "duckstation-qt-x64-releaseltcg.exe,duckstation-qt.exe,duckstation-nogui-x64-releaseltcg.exe,duckstation-nogui.exe", "-batch -fullscreen -- " + Rom, "ps1"),
        P("ppsspp",      "PPSSPP",      "ppssppwindows64.exe,ppssppwindows.exe,ppsspp.exe", "--fullscreen " + Rom,    "psp"),
        P("cemu",        "Cemu",        "cemu.exe",                              "-f -g " + Rom,                     "wiiu"),
        P("yuzu",        "yuzu",        "yuzu.exe,suyu.exe,sudachi.exe,eden.exe", "-f -g " + Rom,                    "switch"),
        P("ryujinx",     "Ryujinx",     "ryujinx.exe",                           "--fullscreen " + Rom,              "switch"),
        P("citra",       "Citra",       "citra-qt.exe,azahar.exe,lime3ds.exe,lime3ds-gui.exe", Rom,                 "3ds"),
        P("melonds",     "melonDS",     "melonds.exe",                           "-f " + Rom,                        "nds"),
        P("mgba",        "mGBA",        "mgba.exe",                              "-f " + Rom,                        "gba,gb,gbc"),
        P("snes9x",      "Snes9x",      "snes9x-x64.exe,snes9x.exe",             "-fullscreen " + Rom,               "snes"),
        P("bsnes",       "bsnes",       "bsnes.exe",                             "--fullscreen " + Rom,              "snes"),
        P("mesen",       "Mesen",       "mesen.exe",                             "--fullscreen " + Rom,              "nes,snes,gb,gbc,gba,tg16,sms,gg,ws"),
        P("project64",   "Project64",   "project64.exe",                         Rom,                                "n64"),
        P("ares",        "ares",        "ares.exe",                              Rom,                                "nes,snes,n64,gb,gbc,gba,sms,genesis,segacd,32x,gg,tg16,tgcd,ngp,ws,msx,coleco,atari2600,ps1"),
        P("flycast",     "Flycast",     "flycast.exe",                           Rom,                                "dreamcast"),
        P("redream",     "Redream",     "redream.exe",                           Rom,                                "dreamcast"),
        // MAME takes the set's name and finds it in the rom path; the extension is not part of it.
        P("mame",        "MAME",        "mame.exe,mame64.exe",                   "-rompath \"{romdir}\" {romname}",  "arcade,neogeo"),
        P("xemu",        "xemu",        "xemu.exe",                              "-full-screen -dvd_path " + Rom,    "xbox_og"),
        P("xenia",       "Xenia",       "xenia.exe,xenia_canary.exe",            Rom,                                "x360"),
        P("mednafen",    "Mednafen",    "mednafen.exe",                          Rom,                                "ps1,saturn,tg16,tgcd,ngp,ws,lynx,vb,nes,snes,gb,gbc,gba,sms,genesis,gg"),
        P("blastem",     "BlastEm",     "blastem.exe",                           "-f " + Rom,                        "genesis"),
        P("fusion",      "Kega Fusion", "fusion.exe",                            Rom + " -fullscreen",               "genesis,sms,gg,segacd,32x"),
        P("bizhawk",     "BizHawk",     "emuhawk.exe",                           "--fullscreen " + Rom,              "nes,snes,n64,gb,gbc,gba,nds,ps1,sms,genesis,saturn,gg,tg16,atari2600,atari7800,lynx,ngp,ws,c64,zx,intv,coleco,vb,32x,segacd"),
        P("fceux",       "FCEUX",       "fceux.exe",                             Rom,                                "nes"),
        P("nestopia",    "Nestopia",    "nestopia.exe",                          Rom,                                "nes"),
        P("vbam",        "VBA-M",       "visualboyadvance-m.exe,vbam.exe",       Rom,                                "gba,gb,gbc"),
        P("desmume",     "DeSmuME",     "desmume.exe",                           Rom,                                "nds"),
        P("stella",      "Stella",      "stella.exe",                            "-fullscreen 1 " + Rom,             "atari2600"),
        P("vice",        "VICE",        "x64sc.exe,x64.exe",                     "-fullscreen " + Rom,               "c64"),
    };

    public static Preset? Detect(string exePath)
    {
        var name = System.IO.Path.GetFileName(exePath).ToLowerInvariant();
        return All.FirstOrDefault(p => p.ExeNames.Contains(name));
    }
}
