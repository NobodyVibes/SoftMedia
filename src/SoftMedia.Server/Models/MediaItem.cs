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
    
    // Tracking & Queuing logic
    public DateTime LastScannedUtc { get; set; } = DateTime.UtcNow;
    public string? MetadataHash { get; set; }

    // Rich Metadata (Promoted from JSON)
    public int? Year { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public double? CommunityRating { get; set; }
    public string? ContentRating { get; set; }
    public string? Studio { get; set; }
    public string? Director { get; set; }

    /// <summary>
    /// Start time (in seconds) of the credits chapter, used for progress bar markers.
    /// Promoted from MetadataJson to avoid JSON parsing on every DTO serialization.
    /// </summary>
    public double? CreditsStart { get; set; }

    /// <summary>
    /// End time (in seconds) of the credits segment. Populated by either chapter
    /// extraction or cross-episode fingerprint detection. Used by the player's
    /// "Skip Credits" pill to seek past the outro theme without skipping post-credit
    /// content.
    /// </summary>
    public double? CreditsEnd { get; set; }

    /// <summary>
    /// Source of the <see cref="CreditsStart"/> / <see cref="CreditsEnd"/> values.
    /// Auto-detection MUST NOT overwrite a value whose source is <see cref="DetectionSource.Chapter"/>.
    /// </summary>
    public DetectionSource? CreditsSource { get; set; }

    /// <summary>
    /// Start time (in seconds) of the intro / opening theme. Populated by either
    /// chapter extraction or cross-episode fingerprint detection.
    /// </summary>
    public double? IntroStart { get; set; }

    /// <summary>
    /// End time (in seconds) of the intro / opening theme.
    /// </summary>
    public double? IntroEnd { get; set; }

    /// <summary>
    /// Source of the <see cref="IntroStart"/> / <see cref="IntroEnd"/> values.
    /// Auto-detection MUST NOT overwrite a value whose source is <see cref="DetectionSource.Chapter"/>.
    /// </summary>
    public DetectionSource? IntroSource { get; set; }

    /// <summary>
    /// Last time the intro/credits detection pipeline attempted to populate timecodes
    /// for this item. Set on both success and failure so a hard-failing series doesn't
    /// re-run the expensive fingerprint pass on every scan.
    /// </summary>
    public DateTime? LastIntroDetectionUtc { get; set; }

    /// <summary>
    /// Original remote poster URL from the metadata provider.
    /// Promoted from MetadataJson to avoid JSON parsing on every DTO serialization.
    /// The local cached copy is resolved via ImageCacheService at serving time.
    /// </summary>
    public string? PosterUrl { get; set; }

    /// <summary>
    /// Original remote backdrop URL from the metadata provider.
    /// Promoted from MetadataJson to avoid JSON parsing on every DTO serialization.
    /// </summary>
    public string? BackdropUrl { get; set; }

    /// <summary>
    /// Indicates that metadata retries have been exhausted for this item.
    /// Replaces the legacy "retryExhausted" flag previously stored in MetadataJson.
    /// </summary>
    public bool IsRetryExhausted { get; set; }

    /// <summary>
    /// Average rating of all users on this SoftMedia server.
    /// This is pre-calculated on write to ensure high performance for hero sections and cards.
    /// </summary>
    public double? InternalRating { get; set; }


    /// <summary>
    /// Total number of users who have rated this item.
    /// </summary>
    public int InternalRatingCount { get; set; }

    // External IDs
    public string? ImdbId { get; set; }
    public int? TvMazeId { get; set; }
    public string? MusicBrainzId { get; set; }


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

    public ICollection<MediaItemGenre> MediaItemGenres { get; set; } = new List<MediaItemGenre>();
    public ICollection<MediaItemCast> MediaItemCasts { get; set; } = new List<MediaItemCast>();

    public ICollection<AudioTrack> AudioTracks { get; set; } = new List<AudioTrack>();
    public ICollection<SubtitleTrack> SubtitleTracks { get; set; } = new List<SubtitleTrack>();
    public ICollection<Chapter> Chapters { get; set; } = new List<Chapter>();

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

    // Comic hierarchy (Book library): parent series + child issues, mirroring Series/Episode.
    // Issues reuse EpisodeNumber for issue number and SeriesId to point at the parent series.
    ComicSeries = 10,
    ComicIssue = 11
}
