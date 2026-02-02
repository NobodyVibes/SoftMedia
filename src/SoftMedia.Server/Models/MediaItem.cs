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

    // Extended Technical Metadata (Phase 2)
    public int? BitDepth { get; set; }  // 8, 10, 12 bit color depth
    public string? HdrFormat { get; set; }  // "HDR10", "HDR10+", "Dolby Vision", "HLG", null for SDR
    public int? AudioChannels { get; set; }  // Primary audio channel count
    public long? Bitrate { get; set; }  // Overall bitrate in bits/second
    public double? FrameRate { get; set; }  // Frames per second
    public int? Width { get; set; }  // Video width in pixels
    public int? Height { get; set; }  // Video height in pixels
    public string? AudioTracksJson { get; set; }  // JSON array of AudioTrackInfo
    public string? SubtitleTracksJson { get; set; }  // JSON array of SubtitleTrackInfo

    // Rich Metadata (Promoted from JSON)
    public int? Year { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public double? CommunityRating { get; set; }
    public string? ContentRating { get; set; }

    /// <summary>
    /// Average rating of all users on this SoftMedia server.
    /// This is pre-calculated on write to ensure high performance for hero sections and cards.
    /// </summary>
    public double? InternalRating { get; set; }

    /// <summary>
    /// Total number of users who have rated this item.
    /// </summary>
    public int InternalRatingCount { get; set; }

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
