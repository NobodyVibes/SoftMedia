namespace SoftMedia.Server.DTOs;

/// <summary>
/// Wave E1 — playlist DTOs.
/// </summary>

public record PlaylistSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsPublic,
    bool IsOwner,
    string OwnerUsername,
    int ItemCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record PlaylistDetailDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsPublic,
    bool IsOwner,
    string OwnerUsername,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<PlaylistEntryDto> Items);

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
}

public class UpdatePlaylistRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? IsPublic { get; set; }
}

public class AddPlaylistItemsRequest
{
    public List<Guid> MediaItemIds { get; set; } = new();
}

public class ReorderPlaylistRequest
{
    /// <summary>Ordered list of <see cref="PlaylistItem.Id"/> values (NOT MediaItemId).</summary>
    public List<Guid> ItemIds { get; set; } = new();
}
