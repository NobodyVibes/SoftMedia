namespace SoftMedia.Server.DTOs;

/// <summary>
/// R-WI-020 — one personalized home row ("Most Watched", "Top picks for you",
/// "More <genre>"). Rows are derived from play history (R-WI-013) and are
/// ACL/rating-filtered at the query; a user with no history gets an empty list
/// and the client renders nothing.
/// </summary>
public class HomeRowDto
{
    /// <summary>
    /// Stable machine-readable row identity. The client keys row-specific chrome
    /// off this rather than off <see cref="Title"/>, which is display copy and
    /// changes with the caller's scope ("Most Watched" vs "Your Most Watched").
    /// See <see cref="HomeRowKinds"/>.
    /// </summary>
    public string Kind { get; set; } = HomeRowKinds.Generic;
    public string Title { get; set; } = string.Empty;
    public List<MediaItemDto> Items { get; set; } = new();

    /// <summary>
    /// The row's criteria, when they can be expressed as a browse filter. The client
    /// turns this into a "See more" link to /browse, which re-runs the same filter
    /// with paging — so the full grid is guaranteed to hold the same items as the row.
    ///
    /// Null when the row is NOT reproducible from a URL, and the client then omits
    /// the link rather than guessing. "Top picks for you" is the case that matters:
    /// it is ranked against a rolling history window and a mutable cross-row dedup
    /// set, so no fixed filter reproduces it.
    /// </summary>
    public HomeRowFilterDto? Filter { get; set; }
}

/// <summary>
/// A row's criteria in the vocabulary of <see cref="BrowseFilter"/>. Only the fields
/// a given row actually constrains are set; the rest stay null and the browse endpoint
/// ignores them.
/// </summary>
public class HomeRowFilterDto
{
    public string? Genre { get; set; }
    public int? Decade { get; set; }
    public bool? Unplayed { get; set; }
    public Guid? LibraryId { get; set; }
    public string? SortBy { get; set; }

    /// <summary>
    /// Media type names the row is restricted to, e.g. ["Movie", "Series"]. Null means
    /// the row spans everything browsable. Carried so a narrowed row's "See more" opens
    /// the same set the row showed.
    /// </summary>
    public List<string>? Types { get; set; }
}

/// <summary>Row identities carried by <see cref="HomeRowDto.Kind"/>.</summary>
public static class HomeRowKinds
{
    /// <summary>Play-count row; the only row carrying an Everyone/Me scope toggle.</summary>
    public const string MostWatched = "most-watched";
    public const string TopPicks = "top-picks";

    /// <summary>Taste-derived genre row ("More Comedy") — needs play history.</summary>
    public const string Genre = "genre";

    /// <summary>Catalog rows — no history required, and they span every media type.</summary>
    public const string GenreSpotlight = "genre-spotlight";
    public const string NeverPlayed = "never-played";

    public const string Generic = "generic";
}
