namespace SoftMedia.Server.Models;

/// <summary>
/// Whether a playlist's membership is stored or derived.
///
/// <see cref="Manual"/> playlists own <see cref="PlaylistItem"/> rows and are
/// reordered/added to by hand. <see cref="Smart"/> playlists own no rows at all:
/// their contents are a query, re-evaluated on every read, so a smart playlist
/// reflects the library as it is now rather than as it was when it was built.
/// </summary>
public enum PlaylistKind
{
    Manual = 0,
    Smart = 1,
}

/// <summary>Ordering applied to a smart playlist's matches.</summary>
public enum SmartPlaylistSort
{
    RecentlyAdded = 0,
    MostPlayed = 1,
    RecentlyPlayed = 2,
    Title = 3,
    Artist = 4,
}

/// <summary>
/// The query behind a smart playlist, stored as JSON on <see cref="Playlist.SmartRules"/>.
///
/// Deliberately a small fixed set of composable filters rather than a general
/// rule builder: these compose into every preset users actually ask for
/// (Recently Added, Most Played, Favourites, Never Played, "Rock I haven't got
/// to yet") while staying something the client can present as a form and the
/// server can translate to a single SQL query.
///
/// PRIVACY: every play-related filter and sort here is evaluated against the
/// OWNER's own signals — <see cref="PlaybackHistory"/> rows and
/// <see cref="UserMediaInteraction"/> — never <see cref="MediaItem.PlayCount"/>
/// or <see cref="MediaItem.LastPlayed"/>. Those two are all-user aggregates by
/// design (see LibraryRepository), so building "my most played" on them would
/// quietly rank a personal playlist by the whole household's listening.
/// </summary>
public class SmartPlaylistRules
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    public const int MaxGenreLength = 100;

    /// <summary>Restrict to tracks the owner has favourited.</summary>
    public bool FavoritesOnly { get; set; }

    /// <summary>Restrict to tracks the owner has never played.</summary>
    public bool UnplayedOnly { get; set; }

    /// <summary>Restrict to tracks added to the library within the last N days.</summary>
    public int? AddedWithinDays { get; set; }

    /// <summary>Restrict to a single genre, matched by exact name.</summary>
    public string? Genre { get; set; }

    /// <summary>Restrict to a single artist.</summary>
    public Guid? ArtistId { get; set; }

    public SmartPlaylistSort Sort { get; set; } = SmartPlaylistSort.RecentlyAdded;

    /// <summary>
    /// Hard cap on how many tracks the playlist yields. Always bounded — an
    /// unlimited smart playlist over a large library would load the entire
    /// audio table into memory on every read of the index page.
    /// </summary>
    public int Limit { get; set; } = DefaultLimit;

    /// <summary>
    /// True when no filter narrows the library at all. Such a playlist is just
    /// "the first N tracks by <see cref="Sort"/>", which is a legitimate thing
    /// to want (a Most Played chart) — so this is informational, not an error.
    /// </summary>
    public bool HasNoFilters =>
        !FavoritesOnly && !UnplayedOnly && AddedWithinDays is null
        && string.IsNullOrWhiteSpace(Genre) && ArtistId is null;

    /// <summary>
    /// Rejects nonsense a client could otherwise persist. Returns null when the
    /// rules are usable, or a human-readable reason when they are not.
    /// </summary>
    public string? Validate()
    {
        if (FavoritesOnly && UnplayedOnly)
        {
            // Not strictly empty — you can favourite something you have never
            // played — but it is almost always a mis-click, and the resulting
            // empty playlist looks like a bug rather than a choice.
            return "A smart playlist cannot require both favourites-only and unplayed-only.";
        }
        if (AddedWithinDays is { } days && days < 1)
        {
            return "AddedWithinDays must be at least 1.";
        }
        if (Genre is { Length: > MaxGenreLength })
        {
            return $"Genre exceeds {MaxGenreLength} characters.";
        }
        if (UnplayedOnly && Sort is SmartPlaylistSort.MostPlayed or SmartPlaylistSort.RecentlyPlayed)
        {
            // Every match has zero plays, so the sort has nothing to order by
            // and the result would be arbitrary.
            return "Unplayed-only playlists cannot be sorted by play activity.";
        }
        return null;
    }

    /// <summary>
    /// Clamps and tidies the rules into the canonical form that gets persisted,
    /// so stored JSON never carries an out-of-range limit or a blank genre that
    /// later reads would have to defend against.
    /// </summary>
    public void Normalize()
    {
        Limit = Limit <= 0 ? DefaultLimit : Math.Min(Limit, MaxLimit);
        Genre = string.IsNullOrWhiteSpace(Genre) ? null : Genre.Trim();
        if (AddedWithinDays is <= 0) AddedWithinDays = null;
    }
}
