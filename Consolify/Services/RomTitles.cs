using System.Text.RegularExpressions;

namespace Consolify.Services;

/// <summary>
/// A game's title out of a ROM's file name.
///
/// ROM sets follow a couple of naming conventions, and both hang the game's name with a train of
/// tags: No-Intro writes "Legend of Zelda, The - A Link to the Past (USA) (Rev 1).sfc", GoodTools
/// writes "Super Mario World (U) [!].smc". The tags say region, revision, dump quality and disc
/// number, none of which is part of the title, and all of which would sink the metadata lookup --
/// TitleMatch wants an exact title and "super mario world u" is not one.
///
/// So: everything in round or square brackets goes, the No-Intro ", The" is put back at the
/// front, the " - " that separates a subtitle becomes ": ", and underscores become spaces. What
/// cannot be helped is left alone -- a MAME set called "sf2.zip" has no title in it to find, and
/// the entry can be renamed by hand from its Manage menu.
/// </summary>
public static class RomTitles
{
    private static readonly Regex Tags = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex TrailingArticle = new(@"^(?<title>.+?), (?<article>The|A|An)(?<rest>\s+-\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Subtitle = new(@"\s+-\s+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    /// <summary>A version stuck on the end outside any brackets: "Game v1.2", "Game 1.1".</summary>
    private static readonly Regex TrailingVersion = new(@"\s+v?\d+\.\d+[a-z]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string FromFileName(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(name) ? fileName : FromLabel(name);
    }

    /// <summary>
    /// The same cleaning for a name that is not a file name -- a RetroArch playlist label, which
    /// carries the database's No-Intro name and no extension. Kept separate because treating a
    /// label as a file name would cut "Dr. Mario" down to "Dr".
    /// </summary>
    public static string FromLabel(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        var text = Tags.Replace(name, " ");
        text = text.Replace('_', ' ');
        text = Spaces.Replace(text, " ").Trim().TrimEnd('-', ' ');
        text = TrailingVersion.Replace(text, "");

        // "Legend of Zelda, The - A Link to the Past" -> "The Legend of Zelda - A Link to the Past"
        var m = TrailingArticle.Match(text);
        if (m.Success)
            text = $"{m.Groups["article"].Value} {m.Groups["title"].Value}{m.Groups["rest"].Value}";

        // The convention's " - " stands for the colon a file name is not allowed to hold.
        text = Subtitle.Replace(text, ": ");
        text = Spaces.Replace(text, " ").Trim();

        // Everything stripped away -- "(USA).sfc", say -- falls back to the raw name rather than
        // an empty title, which would be a tile with nothing on it at all.
        return text.Length > 0 ? text : name.Trim();
    }
}
