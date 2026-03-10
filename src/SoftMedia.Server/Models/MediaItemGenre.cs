using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

/// <summary>
/// Junction table linking MediaItems to Genres.
/// Enables efficient indexed genre filtering without wildcard string matching.
/// </summary>
public class MediaItemGenre
{
    [Required]
    public Guid MediaItemId { get; set; }

    [ForeignKey(nameof(MediaItemId))]
    public MediaItem? MediaItem { get; set; }

    [Required]
    public int GenreId { get; set; }

    [ForeignKey(nameof(GenreId))]
    public Genre? Genre { get; set; }
}
