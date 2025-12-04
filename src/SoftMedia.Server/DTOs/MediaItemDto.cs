using System.Text.Json.Serialization;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.DTOs;

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
    public string? Description { get; set; }
    public string? Container { get; set; }

    public static MediaItemDto FromMediaItem(MediaItem item, string? imageProxyBaseUrl = null)
    {
        var dto = new MediaItemDto
        {
            Id = item.Id,
            LibraryId = item.LibraryId,
            Title = item.Title,
            SortTitle = item.SortTitle,
            Path = item.Path,
            DateAdded = item.DateAdded,
            Container = item.Container
        };

        if (!string.IsNullOrEmpty(item.MetadataJson))
        {
            try
            {
                var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(item.MetadataJson);
                if (metadata != null)
                {
                    if (metadata.TryGetValue("year", out var yearObj) && int.TryParse(yearObj.ToString(), out var year))
                    {
                        dto.Year = year;
                    }
                    
                    if (metadata.TryGetValue("poster", out var posterObj))
                    {
                        var posterUrl = posterObj.ToString();
                        if (!string.IsNullOrEmpty(posterUrl))
                        {
                            // If it's a URL, proxy it. If it's a local path, serve it (not implemented yet for local).
                            // For now, assume URLs from metadata providers.
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
                    if (metadata.TryGetValue("rating", out var ratingObj)) dto.Rating = ratingObj.ToString();
                    if (metadata.TryGetValue("description", out var descObj)) dto.Description = descObj.ToString();
                    
                    if (metadata.TryGetValue("genres", out var genresObj) && genresObj is System.Text.Json.JsonElement genresElement && genresElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        dto.Genres = genresElement.EnumerateArray().Select(x => x.ToString()).ToList();
                    }
                }
            }
            catch
            {
                // Ignore metadata parsing errors
            }
        }

        // Fallback for Duration if not in metadata but in technical details
        if (string.IsNullOrEmpty(dto.Duration) && item.Duration > 0)
        {
            var ts = TimeSpan.FromSeconds(item.Duration);
            dto.Duration = $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        return dto;
    }
}
