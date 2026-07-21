using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

/// <summary>
/// Criteria for a CROSS-library browse. Mirrors <see cref="LibraryItemFilter"/>'s flat
/// shape, but deliberately is not it: that one hard-requires a library and then narrows
/// by <c>library.Type</c> (TV → Series only, Music → Albums, …), which cannot express
/// "every Comedy item on the server regardless of library".
///
/// This is what backs the home rows' "See more" links — each row describes itself as a
/// filter, and the browse page re-runs that same filter with paging.
/// </summary>
public class BrowseFilter
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    /// <summary>Canonical genre name, matched case-insensitively.</summary>
    public string? Genre { get; set; }

    /// <summary>
    /// First year of a decade, e.g. 1990 selects 1990-1999. A decade rather than a
    /// single year because that is the granularity the era row browses at.
    /// </summary>
    public int? Decade { get; set; }

    /// <summary>
    /// When true, only items the calling user has never played — including via a
    /// child (an unplayed Series has no played episodes; an unplayed Album has no
    /// played tracks).
    /// </summary>
    public bool? Unplayed { get; set; }

    /// <summary>Optional narrowing to one library.</summary>
    public Guid? LibraryId { get; set; }

    /// <summary>
    /// title (default) | dateadded | year | rating | playcount | myplaycount | lastplayed.
    ///
    /// "playcount" ranks by the all-user aggregate on MediaItem, which is what the Most
    /// Watched row's Everyone scope shows. "myplaycount" ranks by the caller's own
    /// history rows, matching that row's Me scope — without it a See more link would
    /// quietly swap a personal ranking for the household one.
    /// </summary>
    public string? SortBy { get; set; }

    /// <summary>
    /// "asc" | "desc". Null means the sort key's natural direction (title A-Z,
    /// everything else newest/highest/most first) — see
    /// <see cref="Helpers.SortDirection"/>. Null must stay a no-op: every "See more"
    /// link already shipped omits it and must not flip.
    /// </summary>
    public string? SortDir { get; set; }

    /// <summary>Free text over title, overview, genre and cast.</summary>
    public string? Search { get; set; }

    /// <summary>Exact release year. Narrower than <see cref="Decade"/>; both may be set.</summary>
    public int? Year { get; set; }

    /// <summary>Minimum personal star rating (1-10) the caller has given.</summary>
    public int? MinRating { get; set; }

    /// <summary>Restrict to (or exclude) the caller's favourites.</summary>
    public bool? IsFavorite { get; set; }

    /// <summary>Restrict to (or exclude) items the caller has marked watched.</summary>
    public bool? Watched { get; set; }

    /// <summary>
    /// Items the caller has started but not finished — what the Continue Watching row
    /// shows. Distinct from Watched=false, which also includes never-started items.
    /// </summary>
    public bool? InProgress { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>
    /// Narrows the result to specific media types. Null/empty means all of
    /// <see cref="BrowsableTypes"/>.
    ///
    /// This exists so a row's criteria can round-trip: the genre spotlight is
    /// video-only, and without carrying that restriction into the link its "See more"
    /// would open a grid full of the albums and books the row deliberately left out.
    /// Always intersected with <see cref="BrowsableTypes"/> — a caller cannot use it to
    /// pull child rows (Episode/Audio/ComicIssue) into a grid.
    /// </summary>
    public MediaType[]? Types { get; set; }

    /// <summary>
    /// Types that make sense as cards in a mixed grid. Child rows are excluded:
    /// Episodes/Seasons reach the user through their Series, Audio through its Album,
    /// and ComicIssue through its ComicSeries. Artist is omitted because an artist
    /// card next to a film reads as a category, not a title.
    /// </summary>
    public static readonly MediaType[] BrowsableTypes =
    {
        MediaType.Movie,
        MediaType.Series,
        MediaType.Book,
        MediaType.Album,
        MediaType.ComicSeries,
    };

    /// <summary>
    /// The watchable subset. Genre rows use this because genre means something
    /// different across media: film/TV genres ("Comedy", "Adventure") are a browsing
    /// axis, whereas the book and music tags that dominate this table by volume
    /// ("Fiction", "Thrash Metal") would otherwise always win the top-genre ranking and
    /// bury the video ones.
    /// </summary>
    public static readonly MediaType[] VideoTypes =
    {
        MediaType.Movie,
        MediaType.Series,
    };

    /// <summary>
    /// Resolve the effective type set, clamped so a request can never widen past
    /// <see cref="BrowsableTypes"/> into child rows.
    /// </summary>
    public MediaType[] EffectiveTypes()
    {
        if (Types is not { Length: > 0 }) return BrowsableTypes;
        var allowed = Types.Where(BrowsableTypes.Contains).ToArray();
        return allowed.Length > 0 ? allowed : BrowsableTypes;
    }
}
