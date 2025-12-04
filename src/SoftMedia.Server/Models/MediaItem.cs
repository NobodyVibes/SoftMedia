using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

[Index(nameof(LibraryId))]
public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LibraryId { get; set; }
    public Library? Library { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string SortTitle { get; set; } = string.Empty;

    [Required]
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    public DateTime DateModified { get; set; }

    public bool IsFavorite { get; set; }

    public int PlayCount { get; set; }

    public DateTime? LastPlayed { get; set; }

    // Technical Metadata
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Resolution { get; set; }
    public double Duration { get; set; } // Seconds

    // Type-Specific Metadata (JSON)
    public string? MetadataJson { get; set; }
}
