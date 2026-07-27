using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

/// <summary>
/// Wave E1 — playlist DTOs.
/// </summary>

/// <param name="CoverImagePaths">
/// Up to four distinct album covers drawn from the head of the playlist, in play
/// order — the client renders them as a mosaic so playlist cards carry real
/// artwork like every other card in the app. Empty for a playlist whose head
/// tracks have no art; the client falls back to a gradient tile. Subject to the
/// caller's library ACL, so a shared playlist never leaks art from a library the
/// viewer is denied.
/// </param>
public record PlaylistSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsPublic,
    bool IsOwner,
    string OwnerUsername,
    int ItemCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<string> CoverImagePaths,
    PlaylistKind Kind,
    SmartPlaylistRules? Rules,
    /// <summary>
    /// An uploaded cover, when the owner set one. Also delivered as the single
    /// entry of <see cref="CoverImagePaths"/> so display code needs no special
    /// case; this field exists so the UI can tell a custom cover from a mosaic
    /// and offer to remove it.
    /// </summary>
    string? CoverImagePath = null);

/// <param name="Rules">
/// Present only for a smart playlist, and only for its owner — the rules describe
/// the owner's favourites and listening, which is not a viewer's business even
/// when the playlist itself is readable.
/// </param>
public record PlaylistDetailDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsPublic,
    bool IsOwner,
    string OwnerUsername,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<PlaylistEntryDto> Items,
    PlaylistKind Kind,
    SmartPlaylistRules? Rules,
    /// <summary>An uploaded cover; null means the client builds a mosaic from the tracks.</summary>
    string? CoverImagePath = null);

/// <summary>
/// One ordered slot inside a playlist response. Carries the surrogate
/// PlaylistItem.Id so the reorder endpoint can validate by row identity
/// (duplicates of the same MediaItem are allowed).
/// </summary>
public record PlaylistEntryDto(
    Guid PlaylistItemId,
    int Order,
    MediaItemDto Media);

public class CreatePlaylistRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsPublic { get; set; }

    /// <summary>Supplying rules creates a SMART playlist; omitting them creates a manual one.</summary>
    public SmartPlaylistRules? Rules { get; set; }
}

public class UpdatePlaylistRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? IsPublic { get; set; }

    /// <summary>
    /// Replaces a smart playlist's rules. Rejected for a manual playlist: converting
    /// between kinds would either discard hand-curated rows or silently strand them.
    /// </summary>
    public SmartPlaylistRules? Rules { get; set; }
}

public class ImportPlaylistRequest
{
    /// <summary>Raw M3U text. The server never opens the paths inside it.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Overrides the file's own #PLAYLIST name, if it has one.</summary>
    public string? Name { get; set; }
}

/// <param name="UnmatchedSample">
/// A handful of the lines that matched nothing, so the user can see WHY an import
/// came up short (usually a different mount point) instead of just a number.
/// </param>
public record ImportPlaylistResultDto(
    PlaylistSummaryDto Playlist,
    int MatchedCount,
    int UnmatchedCount,
    List<string> UnmatchedSample);

public class AddPlaylistItemsRequest
{
    public List<Guid> MediaItemIds { get; set; } = new();
}

public class ReorderPlaylistRequest
{
    /// <summary>Ordered list of <see cref="PlaylistItem.Id"/> values (NOT MediaItemId).</summary>
    public List<Guid> ItemIds { get; set; } = new();
}
