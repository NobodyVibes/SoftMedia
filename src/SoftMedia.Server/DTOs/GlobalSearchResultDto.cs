using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

/// <summary>
/// Represents search results grouped by library for the global search feature.
/// Groups arrive ordered by relevance: best <see cref="BestMatchTier"/> first,
/// then the library's configured position.
/// </summary>
public class GlobalSearchResultDto
{
    public Guid LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string LibraryType { get; set; } = string.Empty;
    public List<MediaItemDto> Items { get; set; } = new();

    /// <summary>
    /// The strongest match tier among <see cref="Items"/>: 0 = a title starts
    /// with the query, 1 = a title contains it, 2 = matched via another field
    /// (description, genre, cast, artist, album). The client merges this group
    /// with playlist and library-name hits on the same scale, so placement in
    /// the dropdown is decided by match quality rather than by result type.
    /// </summary>
    public int BestMatchTier { get; set; }

    /// <summary>
    /// For items whose TITLE did not match (tier 2), which field did — keyed by
    /// item id, values like "Matched genre: Rock" or "Matched cast: Ted Testa".
    /// A parallel map rather than a field on <see cref="MediaItemDto"/>: that DTO
    /// is shared by every media surface in the app, and a search-only annotation
    /// doesn't belong on all of them. Absent for items whose title matched —
    /// their presence needs no explanation.
    /// </summary>
    public Dictionary<string, string> MatchReasons { get; set; } = new();
}
