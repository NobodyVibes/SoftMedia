namespace SoftMedia.Server.DTOs;

/// <summary>
/// R-WI-020 — one personalized home row ("Because you watched X", "Top picks for
/// you", "More <genre>"). Rows are derived from the caller's play history
/// (R-WI-013) and are ACL/rating-filtered at the query; a user with no history
/// gets an empty list and the client renders nothing.
/// </summary>
public class HomeRowDto
{
    public string Title { get; set; } = string.Empty;
    public List<MediaItemDto> Items { get; set; } = new();
}
