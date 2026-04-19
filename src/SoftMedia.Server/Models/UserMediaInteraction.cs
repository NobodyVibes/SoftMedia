using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

public class UserMediaInteraction
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    [Range(1, 10)]
    public int? Rating { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsWatched { get; set; }

    public DateTime? LastPlayed { get; set; }
    
    /// <summary>
    /// Playback position in seconds for resume functionality.
    /// For books: page number (1-based) for PDF/CBZ; unused for EPUB.
    /// </summary>
    public double? PlaybackPosition { get; set; }

    /// <summary>
    /// Opaque location string for formats that can't express position as a number.
    /// Currently used for EPUB CFI (Canonical Fragment Identifier).
    /// </summary>
    public string? BookLocation { get; set; }
}
