namespace SoftMedia.Server.Helpers;

/// <summary>
/// Resolves whether a sort runs ascending or descending.
///
/// Every sort key has a NATURAL direction — the one a user means when they pick it
/// without saying more. Title reads A-Z; "Date Added", "Year", "Rating" and the
/// play-count sorts all mean newest/highest/most first. Hard-coding those was fine
/// until the direction became user-controllable, at which point two things had to stay
/// true at once:
///
///   1. An explicit direction always wins.
///   2. NO direction must behave exactly as before — every home-row "See more" link
///      already in the wild omits it, and those links must not silently flip.
///
/// Hence: null means "use the natural default for this key", not "ascending".
/// </summary>
public static class SortDirection
{
    public const string Ascending = "asc";
    public const string Descending = "desc";

    /// <summary>
    /// Sort keys that read ascending when the user has not said otherwise. Everything
    /// else — dates, counts, ratings, years — defaults to descending, because "most" and
    /// "newest" are what a person means by those.
    /// </summary>
    private static readonly HashSet<string> AscendingByNature =
        new(StringComparer.OrdinalIgnoreCase) { "title", "artist" };

    /// <summary>
    /// True when the query should run descending.
    /// <paramref name="sortDir"/> null/blank/unrecognised falls back to the key's
    /// natural direction rather than erroring — this drives a UI control and a stale
    /// or hand-edited URL should still produce a sensible page.
    /// </summary>
    public static bool IsDescending(string? sortBy, string? sortDir)
    {
        if (string.Equals(sortDir, Ascending, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(sortDir, Descending, StringComparison.OrdinalIgnoreCase)) return true;

        return !AscendingByNature.Contains(sortBy ?? "title");
    }

    /// <summary>The direction a key uses when none is specified — for the UI's initial state.</summary>
    public static string NaturalFor(string? sortBy) =>
        AscendingByNature.Contains(sortBy ?? "title") ? Ascending : Descending;
}
