using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

/// <summary>
/// Represents search results grouped by library for the global search feature.
/// </summary>
public class GlobalSearchResultDto
{
    public Guid LibraryId { get; set; }
    public string LibraryName { get; set; } = string.Empty;
    public string LibraryType { get; set; } = string.Empty;
    public List<MediaItemDto> Items { get; set; } = new();
}
