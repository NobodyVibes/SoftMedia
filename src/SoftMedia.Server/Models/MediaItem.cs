using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

[Index(nameof(LibraryId))]
// SR-WI-062 — catalog hot-path indexes. Every browse endpoint filters
// (LibraryId, Type); home rows filter Type and sort DateAdded; list sorts use
// Title/Year. Path is the scanners' primary lookup key; the plain index here
// serves those lookups. Path uniqueness is enforced by a PARTIAL unique index
// (fluent config in AppDbContext) covering only file-backed types: container
// rows (Series/Season/Artist/Album/ComicSeries) share their folder Path by
// design — e.g. TvScanner.EnsureSeasonAsync sets season.Path = series.Path —
// so a blanket unique index would break scanning.
[Index(nameof(LibraryId), nameof(Type))]
[Index(nameof(Type), nameof(DateAdded))]
[Index(nameof(Title))]
[Index(nameof(Year))]
[Index(nameof(Path))]
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

    /// <summary>
    /// Photo EXIF display fields (camera, iso, fstop, exposure, gps, dateTaken) as a flat
    /// JSON string-to-string object. Photos only. Deliberately a single JSON column rather
    /// than promoted columns: these fields are display-only and never queried relationally
    /// (dateTaken is promoted separately to ReleaseDate/Year for sorting). Parsed into
    /// MediaItemDto.Metadata only for Photo items.
    /// </summary>
    public string? ExifJson { get; set; }
    
    // Tracking & Queuing logic
    public DateTime LastScannedUtc { get; set; } = DateTime.UtcNow;
    public string? MetadataHash { get; set; }

    // Rich Metadata (Promoted from JSON)
    public int? Year { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string? Overview { get; set; }
    public double? CommunityRating { get; set; }
    public string? ContentRating { get; set; }
    /// <summary>
    /// Producing organisation. Movies/TV: the studio or network. Books: the PUBLISHER —
    /// books deliberately reuse this column rather than owning a parallel one, so there is
    /// a single source of truth per item and no dual-write drift between them. The
    /// book-facing label is applied at the presentation layer (BookDetailView).
    /// </summary>
    public string? Studio { get; set; }

    /// <summary>
    /// Primary creator. Movies/TV: the director. Books: the AUTHOR (see <see cref="Studio"/>
    /// for why the column is shared). Multi-author books additionally get one
    /// <see cref="MediaItemCast"/> row per author with Character = "Author"; this column
    /// holds the single creator the scanner read out of the file.
    /// </summary>
    public string? Director { get; set; }

    /// <summary>
    /// Book ISBN, normalised to digits (plus a trailing 'X' check digit) with hyphens and
    /// spaces stripped — see <see cref="Services.Media.IsbnNormalizer"/>. Sourced from the
    /// EPUB OPF &lt;dc:identifier&gt; when the file carries one, otherwise from the metadata
    /// provider. The file-embedded value wins: it identifies the exact edition on disk.
    /// </summary>
    public string? Isbn { get; set; }

    /// <summary>
    /// Book page count for DISPLAY. Prefers the real page count of the file on disk (PDF
    /// page tree, EPUB numberOfPages metadata) and falls back to the provider's edition
    /// page count. Deliberately NOT the reader's pagination source — BookInfoDto.PageCount
    /// serves that and is computed from the actual archive/document at request time, so a
    /// provider's print-edition figure can never desynchronise the reader.
    /// </summary>
    public int? PageCount { get; set; }

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
    /// R-WI-014 — PosterUrl currently holds a cached copy of a LOCAL sidecar image
    /// (poster.jpg / folder.jpg / <stem>-poster.* beside the media, or an NFO local thumb).
    /// Local art is the user's explicit choice, so while set: (a) providers' poster URLs are
    /// neither applied nor downloaded (local wins), and (b) the enrichment-completeness check
    /// treats the item as poster-less until one enrichment pass has stamped MetadataHash —
    /// otherwise a poster.jpg movie would never receive a remote description (Relaxed mode
    /// declares any postered item complete).
    /// </summary>
    public bool PosterFromLocalFile { get; set; }

    /// <summary>R-WI-014 — BackdropUrl holds a cached local sidecar (fanart.jpg/backdrop.jpg);
    /// provider backdrops are suppressed while set. No completeness interaction (only posters
    /// gate enrichment).</summary>
    public bool BackdropFromLocalFile { get; set; }

    /// <summary>
    /// Indicates that metadata retries have been exhausted for this item.
    /// Replaces the legacy "retryExhausted" flag previously stored in MetadataJson.
    /// </summary>
    public bool IsRetryExhausted { get; set; }

    /// <summary>
    /// SR-WI-011 soft delete: the item's file was not found on disk during a scan.
    /// Missing items are hidden from catalog surfaces (browse/search/home/DLNA) but keep
    /// all child rows (play history, interactions, bookmarks, playlist membership) so a
    /// temporarily unavailable drive never destroys user data. A scan that re-finds the
    /// path clears the flag ("heal"). Hard delete happens only after the retention window
    /// (Scanning:MissingItemRetentionDays) or by explicit admin action.
    /// </summary>
    public bool IsMissing { get; set; }

    /// <summary>UTC time the item was first marked missing; drives retention hard-delete.</summary>
    public DateTime? MissingSinceUtc { get; set; }

    /// <summary>
    /// Admin "do not auto-overwrite" flag (P3-WI-003). When true, every metadata
    /// refresh and scan-time enrichment SKIPS this item. Set by manual-edit and
    /// fix-match actions; cleared by the explicit Unlock admin endpoint. The single
    /// chokepoint that honours this is MetadataQueueService.ProcessItemAsync.
    /// </summary>
    public bool MetadataLocked { get; set; }

    /// <summary>Timestamp the lock was set, for the UI "Locked since…" hint.</summary>
    public DateTime? MetadataLockedAt { get; set; }

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

    // Wave E2 — optional movie franchise / collection link.
    // Null for items not in any collection. SetNull on collection delete so
    // the row stays in the library if the collection is removed.
    public Guid? CollectionId { get; set; }
    public Collection? Collection { get; set; }

    /// <summary>
    /// Wave E2 — sentinel marker tracking whether the OMDb→Wikidata collection
    /// resolver has been attempted for this item. Mirrors the comic-provider
    /// "EMPTY" sentinel pattern at MetadataAggregator.cs lines 58-67. Avoids
    /// re-querying Wikidata on every metadata refresh for movies that have
    /// no series. Three values:
    ///   - null  => never attempted; resolver should run.
    ///   - true  => attempt found a collection; CollectionId is set.
    ///   - false => attempt found no series; do not retry.
    /// </summary>
    public bool? CollectionLookupAttempted { get; set; }
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
