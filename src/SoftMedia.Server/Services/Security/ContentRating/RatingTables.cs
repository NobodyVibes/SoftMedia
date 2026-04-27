namespace SoftMedia.Server.Services.Security.ContentRating;

/// Ordered rating systems used by the parental-control filter (SDD §4.2 / §6.2).
///
/// Each table is a list of canonical rating labels in ASCENDING strictness. The
/// rank of a rating is its index in the list — so for movies, `G` ranks 0 and
/// `NC-17` ranks 4. To allow content "at or below" a ceiling, we keep every
/// label whose index is &lt;= the ceiling's index.
///
/// Unknown / null ratings are treated as MORE strict than the highest known
/// rating (i.e. always blocked when any ceiling is set). Admins bypass the
/// filter entirely upstream so they always see everything.
///
/// We deliberately keep tables flat string arrays (not enums) because:
///   1. <see cref="SoftMedia.Server.Models.MediaItem.ContentRating"/> is a
///      string column — no parse round-trip needed.
///   2. The metadata providers (Wikidata/OMDb/ComicInfoXml) populate the
///      string verbatim from upstream sources, so case and spelling are stable.
///   3. EF Core can translate `List&lt;string&gt;.Contains(column)` to
///      `WHERE column IN (...)` directly.
public static class RatingTables
{
    /// MPAA movie ratings, ascending strictness.
    public static readonly IReadOnlyList<string> Movie = new[]
    {
        "G",
        "PG",
        "PG-13",
        "R",
        "NC-17",
    };

    /// US TV Parental Guidelines, ascending strictness.
    public static readonly IReadOnlyList<string> Tv = new[]
    {
        "TV-Y",
        "TV-Y7",
        "TV-G",
        "TV-PG",
        "TV-14",
        "TV-MA",
    };

    /// ESRB game ratings, ascending strictness. AO is intentionally last.
    public static readonly IReadOnlyList<string> Game = new[]
    {
        "EC",
        "E",
        "E10+",
        "T",
        "M",
        "AO",
    };

    /// Returns the labels at or below the given ceiling, or null when the
    /// ceiling is unrecognised (caller treats null as "no filter for this type").
    public static IReadOnlyList<string>? AllowedAtOrBelow(IReadOnlyList<string> table, string? ceiling)
    {
        if (string.IsNullOrWhiteSpace(ceiling)) return null;
        var idx = -1;
        for (var i = 0; i < table.Count; i++)
        {
            if (string.Equals(table[i], ceiling, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return null; // unknown ceiling — fail open for that type
        return table.Take(idx + 1).ToArray();
    }
}
