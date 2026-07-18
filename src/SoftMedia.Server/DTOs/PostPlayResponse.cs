namespace SoftMedia.Server.DTOs;

/// <summary>
/// What the player's end-of-movie overlay shows after a movie finishes: unfinished movies from
/// the same collection first (so a marathon can jump straight to the next film), then
/// genre-similar movies from the caller's visible libraries.
/// </summary>
public class PostPlayResponse
{
    /// <summary>Name of the finished movie's collection, when it has one (e.g. "The Lord of the Rings").</summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// Unfinished movies from the same collection, ordered release-date ascending starting from
    /// the first film released AFTER the finished one (wrapping to earlier unwatched films last).
    /// </summary>
    public List<MediaItemDto> CollectionItems { get; set; } = new();

    /// <summary>Genre-similar unfinished movies (most shared genres first), excluding collection items.</summary>
    public List<MediaItemDto> SimilarItems { get; set; } = new();
}
