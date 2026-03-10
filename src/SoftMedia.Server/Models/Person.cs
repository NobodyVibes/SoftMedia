using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// Represents an actor, director, or other person associated with media items.
/// Normalized from previously JSON-trapped cast data in MetadataJson.
/// </summary>
[Index(nameof(Name))]
[Index(nameof(ExternalId))]
public class Person
{
    public int Id { get; set; }

    /// <summary>Full name of the person (e.g., "Bryan Cranston").</summary>
    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>External provider ID (e.g., TVMaze person ID) for deduplication.</summary>
    public int? ExternalId { get; set; }

    /// <summary>
    /// Relative filesystem path to the cached headshot image.
    /// Stored under wwwroot/cache/images/tv/cast/{ExternalId}.jpg by ImageCacheService.
    /// </summary>
    [MaxLength(512)]
    public string? ImagePath { get; set; }
}
