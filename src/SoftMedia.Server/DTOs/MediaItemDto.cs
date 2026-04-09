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
    
    // Timecode markers for progress bar
    public double? CreditsStart { get; set; }
    public List<ChapterDto>? Chapters { get; set; }

    // User Interaction
    public double? UserRating { get; set; } // Now represents the SoftMedia Average
    public int? PersonalRating { get; set; } // The logged-in user's individual rating
    public bool IsFavorite { get; set; }
    public bool Watched { get; set; }
    public double? PlaybackPosition { get; set; } // Resume position in seconds
    public double? Progress { get; set; } // Progress percentage 0-100

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

    public Guid? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    // Music-specific properties
    public Guid? ArtistId { get; set; }
    public Guid? AlbumId { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public double? DurationSeconds { get; set; }  // Raw duration for audio player

    public static MediaItemDto FromMediaItem(MediaItem item, string? imageProxyBaseUrl = null, UserMediaInteraction? interaction = null)
    {
        var dto = new MediaItemDto
        {
            Id = item.Id,
            LibraryId = item.LibraryId,
            Title = item.Title,
            SortTitle = item.SortTitle,
            Path = item.Path,
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
        }

        // Map pre-calculated internal average to UserRating
        dto.UserRating = item.InternalRating;

            // Map promoted properties
            dto.Year = item.Year;
            dto.Description = item.Overview;
            dto.CommunityRating = item.CommunityRating;
            dto.Rating = item.ContentRating;

            // Read genres exclusively from relational table
            if (item.MediaItemGenres != null && item.MediaItemGenres.Count > 0)
            {
                dto.Genres = item.MediaItemGenres
                    .Where(mg => mg.Genre != null)
                    .Select(mg => mg.Genre!.Name)
                    .ToList();
            }

            // Fallback to MetadataJson for extra fields or if promoted fields are null (migration scenario)
            if (!string.IsNullOrEmpty(item.MetadataJson))
            {
                try
                {
                    var metadata = Helpers.MetadataJsonHelper.Parse(item.MetadataJson);
                    if (metadata != null)
                    {
                        dto.Metadata = metadata; // Expose full metadata to frontend

                        // Only map if not already set by promoted properties
                        if (dto.Year == null && metadata.TryGetValue("year", out var yearObj) && int.TryParse(yearObj.ToString(), out var year))
                        {
                            dto.Year = year;
                        }
                        
                        if (string.IsNullOrEmpty(dto.Description) && metadata.TryGetValue("description", out var descObj)) 
                        {
                            dto.Description = descObj.ToString();
                        }

                        // Map content rating from JSON if model property is null
                        if (string.IsNullOrEmpty(dto.Rating))
                        {
                            if (metadata.TryGetValue("contentRating", out var crObj))
                            {
                                dto.Rating = crObj.ToString();
                            }
                            else if (metadata.TryGetValue("rating", out var ratingObj))
                            {
                                var rStr = ratingObj.ToString();
                                if (!double.TryParse(rStr, out _))
                                {
                                    dto.Rating = rStr;
                                }
                            }
                        }

                        if (dto.CommunityRating == null && metadata.TryGetValue("imdbRating", out var imdbRatingObj) && double.TryParse(imdbRatingObj.ToString(), out var imdbScore))
                        {
                            dto.CommunityRating = imdbScore;
                        }

                        if (metadata.TryGetValue("duration", out var durationObj)) dto.Duration = durationObj.ToString();
                        if (metadata.TryGetValue("quality", out var qualityObj)) dto.Quality = qualityObj.ToString();
                        
                        // Extract credits start timecode for progress bar marker
                        if (metadata.TryGetValue("creditsStart", out var creditsStartObj))
                        {
                            if (double.TryParse(creditsStartObj.ToString(), out var creditsStart))
                            {
                                dto.CreditsStart = creditsStart;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore metadata parsing errors
                }
            }

            dto.PosterPath = ResolvePosterPath(item, imageProxyBaseUrl, dto.Metadata);
            dto.BackdropPath = ResolveBackdropPath(item, imageProxyBaseUrl, dto.Metadata);

            // Fallback for Duration if not in metadata but in technical details
            if (string.IsNullOrEmpty(dto.Duration) && item.Duration > 0)
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

        return dto;
    }

    private static string? ResolvePosterPath(MediaItem item, string? imageProxyBaseUrl, Dictionary<string, object>? parsedMetadata)
    {
        string? url = item.PosterUrl;

        if (string.IsNullOrEmpty(url))
        {
            if (item.Type == MediaType.Episode && item.Series != null)
                url = item.Series.PosterUrl;
            else if ((item.Type == MediaType.Audio || item.Type == MediaType.Album) && item.Album != null)
                url = item.Album.PosterUrl;
        }

        if (string.IsNullOrEmpty(url) && parsedMetadata != null && parsedMetadata.TryGetValue("poster", out var posterObj))
        {
            url = posterObj?.ToString();
        }

        if (string.IsNullOrEmpty(url))
        {
            if (item.Series != null && !string.IsNullOrEmpty(item.Series.MetadataJson))
            {
                var fallbackMeta = Helpers.MetadataJsonHelper.Parse(item.Series.MetadataJson);
                if (fallbackMeta != null && fallbackMeta.TryGetValue("poster", out var pObj))
                    url = pObj?.ToString();
            }
            else if (item.Album != null && !string.IsNullOrEmpty(item.Album.MetadataJson))
            {
                var fallbackMeta = Helpers.MetadataJsonHelper.Parse(item.Album.MetadataJson);
                if (fallbackMeta != null && fallbackMeta.TryGetValue("poster", out var pObj))
                    url = pObj?.ToString();
            }
        }

        if (!string.IsNullOrEmpty(url))
        {
            if (url.StartsWith("http") && !string.IsNullOrEmpty(imageProxyBaseUrl))
                return $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(url)}";
            return url;
        }

        bool hasEmbeddedArt = parsedMetadata != null && parsedMetadata.ContainsKey("hasEmbeddedArt");
        if (hasEmbeddedArt && (item.Type == MediaType.Audio || item.Type == MediaType.Album))
            return $"/api/v1/audio/{item.Id}/cover";

        if (!string.IsNullOrEmpty(item.CoverArtPath))
        {
            if (item.Type == MediaType.Album || item.Type == MediaType.Audio)
                return $"/api/v1/music/album/{(item.AlbumId ?? item.Id)}/cover";
            if (item.Type == MediaType.Artist)
                return $"/api/v1/music/artist/{item.Id}/image";
        }

        if (item.Type == MediaType.Album)
            return $"/api/v1/music/album/{item.Id}/cover";

        if (item.Type == MediaType.Audio && item.AlbumId.HasValue)
            return $"/api/v1/music/album/{item.AlbumId}/cover";

        return null;
    }

    private static string? ResolveBackdropPath(MediaItem item, string? imageProxyBaseUrl, Dictionary<string, object>? parsedMetadata)
    {
        string? url = item.BackdropUrl;

        if (string.IsNullOrEmpty(url) && parsedMetadata != null && parsedMetadata.TryGetValue("backdrop", out var backdropObj))
        {
            url = backdropObj?.ToString();
        }

        if (!string.IsNullOrEmpty(url))
        {
            if (url.StartsWith("http") && !string.IsNullOrEmpty(imageProxyBaseUrl))
                return $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(url)}";
            return url;
        }

        return null;
    }
}
