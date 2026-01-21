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

    // Rich Metadata (Promoted from JSON)
    public int? Year { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public double? CommunityRating { get; set; }
    public string? ContentRating { get; set; }

    // Type-Specific Metadata (JSON)
    public string? MetadataJson { get; set; }

    public MediaType Type { get; set; }

    public Guid? SeriesId { get; set; }
    public MediaItem? Series { get; set; }

    public Guid? SeasonId { get; set; }
    public MediaItem? Season { get; set; }

    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    public Guid? ArtistId { get; set; }
    public MediaItem? Artist { get; set; }

    public Guid? AlbumId { get; set; }
    public MediaItem? Album { get; set; }

    /// <summary>
    /// Path to cover art file (for albums/artists) or cached extraction.
    /// SECURITY: Must be validated before file access to prevent path traversal.
    /// </summary>
    public string? CoverArtPath { get; set; }

    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
}

public enum MediaType
{
    Movie = 0,
    Series = 1,
    Episode = 2,
    Audio = 3,
    Book = 4,
    Game = 5,
    Photo = 6,
    
    // New Types
    Season = 7,
    Artist = 8,
    Album = 9,
    Track = 10
}
