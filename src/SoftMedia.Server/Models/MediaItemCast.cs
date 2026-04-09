using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

/// <summary>
/// Junction table linking MediaItems to Persons with character/role information.
/// Enables relational queries like "Find all shows starring Actor X".
/// </summary>
public class MediaItemCast
{
    [Key]
    public int Id { get; set; }

    [Required]
    public Guid MediaItemId { get; set; }

    [ForeignKey(nameof(MediaItemId))]
    public MediaItem? MediaItem { get; set; }

    [Required]
    public int PersonId { get; set; }

    [ForeignKey(nameof(PersonId))]
    public Person? Person { get; set; }

    /// <summary>Character name played in this media item (e.g., "Walter White").</summary>
    [MaxLength(256)]
    public string? Character { get; set; }

    /// <summary>Display ordering (0-based, lower = higher billing).</summary>
    public int Order { get; set; }
}
