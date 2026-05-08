namespace SoftMedia.Server.DTOs;

/// <summary>
/// Wave E2 — collection DTOs.
/// </summary>

public record CollectionSummaryDto(
    Guid Id,
    string Name,
    string? Overview,
    string? PosterUrl,
    bool IsAuto,
    int VisibleItemCount);

public record CollectionDetailDto(
    Guid Id,
    string Name,
    string? Overview,
    string? PosterUrl,
    bool IsAuto,
    List<CollectionEntryDto> Items);

/// <summary>
/// One movie inside a collection response. <see cref="IsCurrent"/> is set
/// only by the by-movie strip endpoint to mark the movie the user is
/// currently viewing — mirrors the TV-detail "now playing" badge pattern.
/// </summary>
public record CollectionEntryDto(
    MediaItemDto Media,
    bool IsCurrent);

public class CreateCollectionRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public List<Guid> MovieIds { get; set; } = new();
}

public class UpdateCollectionRequest
{
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
}

public class AddCollectionItemsRequest
{
    public List<Guid> MovieIds { get; set; } = new();
}
