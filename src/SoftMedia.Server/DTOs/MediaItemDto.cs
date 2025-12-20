using System.Text.Json.Serialization;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

public class ChapterDto
{
    public double StartTime { get; set; }
    public string Title { get; set; } = string.Empty;
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
    public int? UserRating { get; set; }
    public bool IsFavorite { get; set; }
    public bool Watched { get; set; }

    public Guid? SeriesId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

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
            EpisodeNumber = item.EpisodeNumber
        };

        // Map user interaction if available
        if (interaction != null)
        {
            dto.UserRating = interaction.Rating;
            dto.IsFavorite = interaction.IsFavorite;
            dto.Watched = interaction.IsWatched;
        }

            // Map promoted properties
            dto.Year = item.Year;
            dto.Description = item.Overview;
            dto.CommunityRating = item.CommunityRating;
            
            // Initialize Rating as null, ensuring it only gets populated from external sources
            dto.Rating = null; 

            // Fallback to MetadataJson for extra fields or if promoted fields are null (migration scenario)
            if (!string.IsNullOrEmpty(item.MetadataJson))
            {
                try
                {
                    var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item.MetadataJson);
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

                        if (metadata.TryGetValue("rating", out var ratingObj))
                        {
                            dto.Rating = ratingObj.ToString();
                        }

                        if (metadata.TryGetValue("poster", out var posterObj))
                        {
                            var posterUrl = posterObj.ToString();
                            if (!string.IsNullOrEmpty(posterUrl))
                            {
                                if (posterUrl.StartsWith("http") && !string.IsNullOrEmpty(imageProxyBaseUrl))
                                {
                                    dto.PosterPath = $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(posterUrl)}";
                                }
                                else
                                {
                                    dto.PosterPath = posterUrl;
                                }
                            }
                        }

                        if (metadata.TryGetValue("backdrop", out var backdropObj))
                        {
                             var backdropUrl = backdropObj.ToString();
                             if (!string.IsNullOrEmpty(backdropUrl) && backdropUrl.StartsWith("http") && !string.IsNullOrEmpty(imageProxyBaseUrl))
                             {
                                 dto.BackdropPath = $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(backdropUrl)}";
                             }
                             else
                             {
                                 dto.BackdropPath = backdropUrl?.ToString();
                             }
                        }

                        if (metadata.TryGetValue("duration", out var durationObj)) dto.Duration = durationObj.ToString();
                        if (metadata.TryGetValue("quality", out var qualityObj)) dto.Quality = qualityObj.ToString();
                        
                        if (metadata.TryGetValue("genres", out var genresObj) && genresObj is System.Text.Json.JsonElement genresElement && genresElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            dto.Genres = genresElement.EnumerateArray().Select(x => x.ToString()).ToList();
                        }
                        
                        // Extract credits start timecode for progress bar marker
                        if (metadata.TryGetValue("creditsStart", out var creditsStartObj))
                        {
                            if (double.TryParse(creditsStartObj.ToString(), out var creditsStart))
                            {
                                dto.CreditsStart = creditsStart;
                            }
                        }
                        
                        // Extract all chapters for progress bar markers
                        if (metadata.TryGetValue("chapters", out var chaptersObj) && chaptersObj is System.Text.Json.JsonElement chaptersElement && chaptersElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            dto.Chapters = new List<ChapterDto>();
                            foreach (var chapter in chaptersElement.EnumerateArray())
                            {
                                var chapterDto = new ChapterDto();
                                if (chapter.TryGetProperty("startTime", out var startEl))
                                {
                                    double.TryParse(startEl.ToString(), out var st);
                                    chapterDto.StartTime = st;
                                }
                                if (chapter.TryGetProperty("title", out var titleEl))
                                {
                                    chapterDto.Title = titleEl.GetString() ?? "";
                                }
                                dto.Chapters.Add(chapterDto);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore metadata parsing errors
                }
            }

            // Fallback to Series/Album poster if missing
            if (string.IsNullOrEmpty(dto.PosterPath))
            {
                string? fallbackJson = null;
                if (item.Series != null && !string.IsNullOrEmpty(item.Series.MetadataJson))
                {
                    fallbackJson = item.Series.MetadataJson;
                }
                else if (item.Album != null && !string.IsNullOrEmpty(item.Album.MetadataJson))
                {
                    fallbackJson = item.Album.MetadataJson;
                }

                if (fallbackJson != null)
                {
                    try 
                    {
                        var fallbackMeta = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(fallbackJson);
                        if (fallbackMeta != null && fallbackMeta.TryGetValue("poster", out var posterObj))
                        {
                             var posterUrl = posterObj.ToString();
                             if (!string.IsNullOrEmpty(posterUrl))
                             {
                                 if (posterUrl.StartsWith("http") && !string.IsNullOrEmpty(imageProxyBaseUrl))
                                 {
                                     dto.PosterPath = $"{imageProxyBaseUrl}?url={Uri.EscapeDataString(posterUrl)}";
                                 }
                                 else
                                 {
                                     dto.PosterPath = posterUrl;
                                 }
                             }
                        }
                    }
                    catch {}
                }
            }

            // Fallback for embedded art if flag is present (and no other poster was found)
            if (string.IsNullOrEmpty(dto.PosterPath) && dto.Metadata != null && dto.Metadata.ContainsKey("hasEmbeddedArt"))
            {
                 dto.PosterPath = $"/api/v1/audio/{dto.Id}/cover";
            }

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
}
