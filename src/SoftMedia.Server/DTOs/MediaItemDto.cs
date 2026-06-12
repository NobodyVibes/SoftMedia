using System.Text.Json.Serialization;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

public class ChapterDto
{
    public double StartTime { get; set; }
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// Represents audio track information for display.
/// </summary>
public class AudioTrackDto
{
    public int Index { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public int Channels { get; set; }
    public string? ChannelLayout { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>
/// Represents subtitle track information for display.
/// </summary>
public class SubtitleTrackDto
{
    public int Index { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
}

public class CastMemberDto
{
    public int Id { get; set; }
    public int? ExternalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public List<string> Characters { get; set; } = new();
    public int Order { get; set; }
}

public class MediaItemDto
{
    public Guid Id { get; set; }
    public Guid LibraryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int? Year { get; set; }
    public DateTime DateAdded { get; set; }
    public MediaType Type { get; set; }
    
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? Duration { get; set; }
    public string? Quality { get; set; }
    public List<string>? Genres { get; set; }
    public string? Rating { get; set; }
    public double? CommunityRating { get; set; }
    public string? Description { get; set; }
    public string? Container { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Resolution { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    
    // Timecode markers for progress bar / skip pills.
    // Source fields tell the client whether each pair came from embedded chapters
    // or auto-detection — useful for the player debug panel.
    public double? CreditsStart { get; set; }
    public double? CreditsEnd { get; set; }
    public DetectionSource? CreditsSource { get; set; }
    public double? IntroStart { get; set; }
    public double? IntroEnd { get; set; }
    public DetectionSource? IntroSource { get; set; }
    public List<ChapterDto>? Chapters { get; set; }

    // User Interaction
    public double? UserRating { get; set; } // Now represents the SoftMedia Average
    public int? PersonalRating { get; set; } // The logged-in user's individual rating
    public bool IsFavorite { get; set; }
    public bool Watched { get; set; }
    public double? PlaybackPosition { get; set; } // Resume position in seconds
    public double? Progress { get; set; } // Progress percentage 0-100
    // Wave E3 — watchlist flag for the calling user. Hydrated from the user's
    // interaction row alongside IsFavorite / Watched. Null on responses that
    // don't carry interaction data (e.g. global search summaries).
    public bool IsWatchlisted { get; set; }

    // Phase 2: Extended Quality Metadata
    public int? BitDepth { get; set; }  // 8, 10, 12 bit
    public string? HdrFormat { get; set; }  // "HDR10", "Dolby Vision", etc.
    public int? AudioChannels { get; set; }  // Primary audio channel count
    public long? Bitrate { get; set; }  // bits/second
    public double? FrameRate { get; set; }  // fps
    public int? Width { get; set; }  // Video width
    public int? Height { get; set; }  // Video height
    public List<AudioTrackDto>? AudioTracks { get; set; }
    public List<SubtitleTrackDto>? SubtitleTracks { get; set; }
    public List<CastMemberDto>? Cast { get; set; }

    public Guid? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    // Music-specific properties
    public Guid? ArtistId { get; set; }
    public Guid? AlbumId { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public double? DurationSeconds { get; set; }  // Raw duration for audio player

    // Wave E2 — exposes the collection link so the movie detail view can
    // render the "More from this collection" strip and the library grid can
    // show a small franchise badge. Null for items not in any collection.
    public Guid? CollectionId { get; set; }

    // P3-WI-003 — admin metadata lock. When true, auto-refresh skips this item.
    // The UI renders a small "locked" badge and the Fix Match modal offers an Unlock.
    public bool MetadataLocked { get; set; }
    public DateTime? MetadataLockedAt { get; set; }

    public static MediaItemDto FromMediaItem(MediaItem item, string? imageProxyBaseUrl = null, UserMediaInteraction? interaction = null)
    {
        var dto = new MediaItemDto
        {
            Id = item.Id,
            LibraryId = item.LibraryId,
            Title = item.Title,
            SortTitle = item.SortTitle,
            // Security (audit wave-2 H-1): expose only the FILE NAME, never the absolute on-disk
            // path. The SPA needs the extension (book-format detection in BookReader/BookDetailView);
            // the server's directory layout is not the client's business and was leaking via every
            // MediaItemDto (notably the unfiltered recently-added cache).
            Path = System.IO.Path.GetFileName(item.Path),
            DateAdded = item.DateAdded,
            Container = item.Container,
            VideoCodec = item.VideoCodec,
            AudioCodec = item.AudioCodec,
            Resolution = item.Resolution,
            SeriesId = item.SeriesId,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            Type = item.Type,

            // Music-specific
            ArtistId = item.ArtistId,
            AlbumId = item.AlbumId,
            TrackNumber = item.TrackNumber,
            DiscNumber = item.DiscNumber,
            DurationSeconds = item.Duration > 0 ? item.Duration : null,

            // Wave E2 — collection link.
            CollectionId = item.CollectionId,

            // P3-WI-003 — admin metadata lock.
            MetadataLocked = item.MetadataLocked,
            MetadataLockedAt = item.MetadataLockedAt,

            // Phase 2: Extended Quality Metadata
            BitDepth = item.BitDepth,
            HdrFormat = item.HdrFormat,
            AudioChannels = item.AudioChannels,
            Bitrate = item.Bitrate,
            FrameRate = item.FrameRate,
            Width = item.Width,
            Height = item.Height
        };

        // Map audio tracks if present
        if (item.AudioTracks != null && item.AudioTracks.Any())
        {
            dto.AudioTracks = item.AudioTracks.Select(at => new AudioTrackDto
            {
                Index = at.Index,
                Codec = at.Codec,
                Language = at.Language,
                Channels = at.Channels,
                ChannelLayout = at.ChannelLayout,
                Title = at.Title,
                IsDefault = at.IsDefault
            }).ToList();
        }

        // Map subtitle tracks if present
        if (item.SubtitleTracks != null && item.SubtitleTracks.Any())
        {
            dto.SubtitleTracks = item.SubtitleTracks.Select(st => new SubtitleTrackDto
            {
                Index = st.Index,
                Codec = st.Codec,
                Language = st.Language,
                Title = st.Title,
                IsDefault = st.IsDefault,
                IsForced = st.IsForced
            }).ToList();
        }

        // Map chapters if present (Promoted to relational table in Phase 1)
        if (item.Chapters != null && item.Chapters.Count > 0)
        {
            dto.Chapters = item.Chapters.OrderBy(c => c.StartTime).Select(c => new ChapterDto
            {
                StartTime = c.StartTime,
                Title = c.Title
            }).ToList();
        }


        // Map user interaction if available
        if (interaction != null)
        {
            dto.PersonalRating = interaction.Rating;
            dto.IsFavorite = interaction.IsFavorite;
            dto.Watched = interaction.IsWatched;
            dto.IsWatchlisted = interaction.IsWatchlisted;
        }

        // Map pre-calculated internal average to UserRating
        dto.UserRating = item.InternalRating;

            // Map promoted properties
            dto.Year = item.Year;
            dto.Description = item.Overview;
            dto.CommunityRating = item.CommunityRating;
            dto.Rating = item.ContentRating;

            // Skip-pill timecodes
            dto.CreditsStart = item.CreditsStart;
            dto.CreditsEnd = item.CreditsEnd;
            dto.CreditsSource = item.CreditsSource;
            dto.IntroStart = item.IntroStart;
            dto.IntroEnd = item.IntroEnd;
            dto.IntroSource = item.IntroSource;

            // Read genres exclusively from relational table
            if (item.MediaItemGenres != null && item.MediaItemGenres.Count > 0)
            {
                dto.Genres = item.MediaItemGenres
                    .Where(mg => mg.Genre != null)
                    .Select(mg => mg.Genre!.Name)
                    .ToList();
            }

            // Read cast from relational junction (Person + MediaItemCast)
            if (item.MediaItemCasts != null && item.MediaItemCasts.Count > 0)
            {
                dto.Cast = item.MediaItemCasts
                    .Where(mc => mc.Person != null)
                    .OrderBy(mc => mc.Order)
                    .Select(mc => new CastMemberDto
                    {
                        Id = mc.Person!.Id,
                        ExternalId = mc.Person.ExternalId,
                        Name = mc.Person.Name,
                        ImageUrl = ResolveCastImageUrl(mc.Person.ImagePath, imageProxyBaseUrl),
                        Characters = SplitCharacters(mc.Character),
                        Order = mc.Order
                    })
                    .ToList();
            }


            // Duration formatting from promoted column
            if (item.Duration > 0)
            {
                var ts = TimeSpan.FromSeconds(item.Duration);
                if (ts.TotalHours >= 1)
                {
                    dto.Duration = $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
                }
                else
                {
                    dto.Duration = $"{ts.Minutes}m {ts.Seconds}s";
                }
            }

            dto.PosterPath = ResolvePosterPath(item, imageProxyBaseUrl);
            dto.BackdropPath = ResolveBackdropPath(item, imageProxyBaseUrl);

        return dto;
    }

    private static string? ResolvePosterPath(MediaItem item, string? imageProxyBaseUrl)
    {
        // Music items have dedicated endpoints (MusicController -> MusicImageService)
        // that serve the LOCAL cached cover/art file directly. Prefer them over the
        // remote-URL image proxy: the cover is already downloaded to disk
        // (CoverArtPath), so serving it locally is faster AND sidesteps the proxy's
        // redirect-following plus its sticky no-TTL negative cache — a stale ".404"
        // sentinel written before the Cover Art Archive redirect fix would otherwise
        // keep an album blank even though its cover file exists on disk. (PosterUrl
        // is NOT cleared after caching, so without this music albums would route to
        // the proxy and inherit that staleness; artist cards already take this local
        // path, which is why they render.)
        switch (item.Type)
        {
            case MediaType.Album when !string.IsNullOrEmpty(item.CoverArtPath):
                return $"/api/v1/music/album/{item.Id}/cover";
            case MediaType.Audio when item.AlbumId.HasValue:
                // Tracks inherit their album's cover even when the track row has no
                // CoverArtPath of its own; 404s cleanly if the album also lacks art.
                return $"/api/v1/music/album/{item.AlbumId}/cover";
            case MediaType.Artist:
                // Always emit so MusicImageService's first-album cover fallback runs
                // even when the artist row has no CoverArtPath of its own.
                return $"/api/v1/music/artist/{item.Id}/image";
        }

        // Non-music art (and music items with no local cover) use the remote URL,
        // proxied when it is an absolute http(s) source.
        string? url = item.PosterUrl;

        if (string.IsNullOrEmpty(url))
        {
            if (item.Type == MediaType.Episode && item.Series != null)
                url = item.Series.PosterUrl;
            else if ((item.Type == MediaType.Audio || item.Type == MediaType.Album) && item.Album != null)
                url = item.Album.PosterUrl;
        }

        if (!string.IsNullOrEmpty(url))
        {
            if (url.StartsWith("http") && !string.IsNullOrEmpty(imageProxyBaseUrl))
                return $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(url)}";
            return url;
        }

        return null;
    }

    private static string? ResolveBackdropPath(MediaItem item, string? imageProxyBaseUrl)
    {
        string? url = item.BackdropUrl;

        if (!string.IsNullOrEmpty(url))
        {
            if (url.StartsWith("http") && !string.IsNullOrEmpty(imageProxyBaseUrl))
                return $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(url)}";
            return url;
        }

        return null;
    }

    private static string? ResolveCastImageUrl(string? path, string? imageProxyBaseUrl)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(imageProxyBaseUrl))
            return $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(path)}";
        return path;
    }

    private static List<string> SplitCharacters(string? character)
    {
        if (string.IsNullOrWhiteSpace(character)) return new List<string>();
        return character
            .Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
