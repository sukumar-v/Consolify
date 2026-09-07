using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Consolify.Services;

/// <summary>
/// Decides whether a search result is the same game as a library entry.
///
/// Steam games are looked up by app id and never come through here. Everything else -- Epic, GOG,
/// Xbox, manually added -- has nothing to match on but its title, and a wrong match is worse than
/// no match: it puts another game's art and another game's score on the tile, which is precisely
/// the failure this whole change set out to fix. So the rule is deliberately strict: the two
/// titles must be *equal* once normalised. A near miss is not a match, it is a decline.
///
/// That means some games get nothing. "Nothing" shows an initials placeholder, which reads as
/// missing. A confident-looking wrong cover reads as broken, and the user has no way to tell it
/// is wrong without knowing the game.
/// </summary>
public static class TitleMatch
{
    /// <summary>
    /// Trailing words that name a release rather than a game. Stripped from the end only, and
    /// longest first so "game of the year edition" is not left as "edition" by an earlier rule.
    ///
    /// Kept short on purpose. Every entry here is a chance to collapse two genuinely different
    /// games into one name -- "complete" would merge a game called "X" with one called
    /// "X Complete" -- so a word earns its place only if it is meaningless on its own.
    /// </summary>
    private static readonly string[] EditionSuffixes =
    {
        "game of the year edition",
        "anniversary edition",
        "definitive edition",
        "enhanced edition",
        "complete edition",
        "ultimate edition",
        "standard edition",
        "special edition",
        "deluxe edition",
        "goty edition",
        "gold edition",
        "remastered",
    };

    /// <summary>
    /// Lower-cases, drops accents and trademark marks, spells "&amp;" out, and reduces everything
    /// that is not a letter or a digit to a single space. "Pokémon: Let's Go!" and
    /// "POKEMON LETS GO" come out the same; two different games do not.
    /// </summary>
    public static string Normalise(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        // Apostrophes are removed rather than blanked, because they join where other punctuation
        // separates: "Let's Go" has to normalise to "lets go" to meet a source that writes it
        // without one, not to "let s go", which meets nothing.
        var text = title.Replace("&", " and ");
        foreach (var quote in new[] { "'", "\u2019", "\u2018", "\u00B4", "`" })
            text = text.Replace(quote, "");

        // Decompose, then drop the combining marks: "é" becomes "e" rather than being deleted.
        text = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Normalised, with one trailing edition suffix removed if there is one.</summary>
    public static string Base(string? title)
    {
        var text = Normalise(title);
        foreach (var suffix in EditionSuffixes)
        {
            if (!text.EndsWith(" " + suffix, StringComparison.Ordinal)) continue;
            // Never strip down to nothing: a game genuinely called "Remastered" keeps its name.
            var trimmed = text[..^(suffix.Length + 1)].Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return text;
    }

    /// <summary>
    /// True only when the two titles are the same game beyond reasonable doubt. Exact once
    /// normalised, or exact once a trailing edition suffix is discounted -- nothing fuzzier.
    /// </summary>
    public static bool IsConfident(string? wanted, string? candidate)
    {
        var a = Normalise(wanted);
        var b = Normalise(candidate);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        return Base(wanted) == Base(candidate);
    }

    /// <summary>
    /// The best confident match among some candidates, if there is one.
    ///
    /// Exact normalised equality is preferred over equality-after-edition-stripping, but a tie is
    /// not a reason to decline: two candidates with the same normalised title are the same game
    /// under different editions or bundles, so either one carries the right art. The thing being
    /// guarded against is a *different* game, and nothing that gets this far is one.
    ///
    /// A try-pattern rather than a nullable return because the callers hand it JsonElement, which
    /// is a struct and has no null to give back.
    /// </summary>
    public static bool TryBestMatch<T>(string? wanted, IEnumerable<T> candidates,
        Func<T, string?> nameOf, out T match)
    {
        var norm = Normalise(wanted);
        match = default!;
        var haveLoose = false;

        foreach (var c in candidates)
        {
            var name = nameOf(c);
            if (norm.Length > 0 && Normalise(name) == norm) { match = c; return true; }
            if (haveLoose || !IsConfident(wanted, name)) continue;
            match = c;
            haveLoose = true;
        }
        return haveLoose;
    }
}

/// <summary>
/// Reading numbers out of JSON that may legitimately carry null.
///
/// System.Text.Json's TryGetInt32 does not do what its name suggests: it *throws* when the element
/// is null or a string, and returns false only for a number that will not fit. A "criticScore":
/// null -- which is most games, since most are never scored -- therefore took out the whole
/// enrichment for that game, silently, forever.
/// </summary>
internal static class JsonNum
{
    public static int? Int(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n) ? n : null;

    public static long? Long(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : null;

    public static double? Double(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var n) ? n : null;
}
