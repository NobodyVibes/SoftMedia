using System.Globalization;
using System.Text.RegularExpressions;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// Turns a provider's raw genre string into zero or more canonical genre names.
///
/// Providers disagree wildly about what a "genre" is. Video providers send clean
/// single values ("Comedy"). Music providers send lowercase tags ("heavy metal").
/// Book providers send BISAC subject headings — whole taxonomic paths crammed into
/// one string:
///
///     "FICTION / Science Fiction / Space Opera"
///     "Fiction, science fiction, action &amp; adventure"
///     "Dune (imaginary place), fiction"
///
/// Stored verbatim those become unusable as genres: they never match anything, they
/// bloat the genre list, and a genre-browse UI renders them as garbage. This splits
/// composite strings into their parts, drops the parts that are subject headings
/// rather than genres, and canonicalises casing so "Science Fiction", "Science
/// fiction" and "science fiction" collapse to a single genre instead of three rows.
///
/// Casing note: the Genre table has a UNIQUE index on Name, but SQLite's default
/// BINARY collation makes it case-SENSITIVE — so the index never prevented the
/// duplicates. Canonicalising here is what actually enforces one row per genre.
/// </summary>
public static class GenreNormalizer
{
    /// <summary>
    /// Longest plausible genre name. Anything longer is a description or a subject
    /// heading that survived splitting, not a genre. ("Melodic Death Metal", the
    /// longest real value observed, is 19 characters.)
    /// </summary>
    private const int MaxLength = 40;

    /// <summary>
    /// Cap per item. A book's subject list can run to dozens of entries; past a
    /// handful they stop describing the work and start describing its contents.
    /// </summary>
    public const int MaxGenresPerItem = 12;

    /// <summary>
    /// Separators that join several genres into one provider string.
    ///
    /// Deliberately narrow: only a SPACED slash (or pipe), which is the shape BISAC
    /// paths use — "FICTION / Science Fiction / Space Opera". A bare slash is left
    /// alone because music tags use it inside a single genre name ("pop/rock",
    /// "melodic/death-metal"); splitting those produced nonsense genres like
    /// "Melodic".
    ///
    /// Comma splitting was tried and REMOVED. Book providers emit subject headings,
    /// not genres, and they are comma-joined indistinguishably from real lists:
    /// "Fiction, science fiction, general" splits usefully, but
    /// "Herbert, frank, 1920-1986" (an author) becomes the genres "Herbert" and
    /// "Frank", and "Serial murders, fiction" becomes "Serial Murders". Inventing
    /// junk genres is worse than leaving one ugly row that nothing links to — those
    /// subject rows carry 1-2 items each and never reach a top-genres row anyway.
    /// </summary>
    private static readonly string[] Separators = { " / ", " | " };

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Split, clean and canonicalise one raw provider string. Returns an empty
    /// sequence when nothing usable survives.
    /// </summary>
    public static IEnumerable<string> Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = Clean(part);
            if (cleaned != null) yield return cleaned;
        }
    }

    /// <summary>
    /// Normalize a whole provider list at once: splits every entry, drops junk, and
    /// de-duplicates case-insensitively while preserving first-seen order. This is
    /// the entry point callers should prefer — it applies <see cref="MaxGenresPerItem"/>.
    /// </summary>
    public static List<string> NormalizeAll(IEnumerable<string?>? rawNames)
    {
        var result = new List<string>();
        if (rawNames == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawNames)
        {
            foreach (var name in Normalize(raw))
            {
                if (!seen.Add(name)) continue;
                result.Add(name);
                if (result.Count >= MaxGenresPerItem) return result;
            }
        }
        return result;
    }

    /// <summary>
    /// Clean a single already-split segment. Returns null when the segment is not a
    /// usable genre.
    /// </summary>
    private static string? Clean(string segment)
    {
        var trimmed = WhitespaceRun.Replace(segment, " ").Trim();
        // Strip decorative punctuation providers leave at the edges. Hyphens are
        // NOT stripped from the interior — "Sci-Fi" must survive intact.
        trimmed = trimmed.Trim('-', '.', ':', '"', '\'', ' ');
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxLength) return null;

        // Parenthetical entries are subject headings, not genres — "Dune (imaginary
        // place)", "Holmes, Sherlock (fictitious character)". A genre never needs a
        // qualifier in brackets.
        if (trimmed.Contains('(') || trimmed.Contains(')')) return null;

        // Must contain at least one letter: drops years, ISBNs and stray numbering
        // that book providers mix into subject lists.
        if (!trimmed.Any(char.IsLetter)) return null;

        return ToCanonicalCase(trimmed);
    }

    /// <summary>
    /// Title Case, which is what the existing video genres already use ("Comedy",
    /// "Adventure"). Applying it everywhere is what merges the lowercase music tags
    /// and SHOUTED BISAC headings into the same rows.
    ///
    /// Words that are already mixed-case are left alone so deliberate styling
    /// survives — "iTunes", "R&amp;B" — since ToTitleCase would otherwise mangle
    /// an all-caps word into something wrong.
    /// </summary>
    private static string ToCanonicalCase(string value)
    {
        var textInfo = CultureInfo.InvariantCulture.TextInfo;
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            // Mixed case that is not simply Capitalised is intentional styling.
            var hasInnerUpper = word.Skip(1).Any(char.IsUpper);
            var isAllUpper = word.All(ch => !char.IsLetter(ch) || char.IsUpper(ch));
            if (hasInnerUpper && !isAllUpper) continue;

            words[i] = textInfo.ToTitleCase(word.ToLowerInvariant());
        }

        return string.Join(' ', words);
    }
}
