using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SoftMedia.Server.Models;

/// <summary>
/// Persistent metadata retry queue entry. Survives application restarts
/// unlike the previous in-memory ConcurrentQueue implementation.
/// </summary>
public class MetadataRetry
{
    public int Id { get; set; }

    /// <summary>Foreign key to the MediaItem that needs metadata re-fetch.</summary>
    [Required]
    public Guid MediaItemId { get; set; }

    [ForeignKey(nameof(MediaItemId))]
    public MediaItem? MediaItem { get; set; }

    /// <summary>Library type for routing to the correct metadata provider.</summary>
    [Required]
    public LibraryType LibraryType { get; set; }

    /// <summary>Number of retries attempted so far.</summary>
    public int RetryCount { get; set; }

    /// <summary>When this retry should next be attempted (UTC).</summary>
    public DateTime NextAttempt { get; set; }

    /// <summary>When this retry entry was first created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
