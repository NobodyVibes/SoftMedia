using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using System.Text.Json;

namespace SoftMedia.Server.Services;

public class RecommendationService : IRecommendationService
{
    private readonly AppDbContext _context;
    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(AppDbContext context, ILogger<RecommendationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<NextEpisodeResponse?> GetNextEpisodeAsync(Guid userId, Guid seriesId)
    {
        _logger.LogInformation("[SmartContinue] GetNextEpisode called for series {SeriesId}", seriesId);

        // Get all episodes for this series ordered by season/episode
        var episodes = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ToListAsync();

        _logger.LogInformation("[SmartContinue] Found {Count} episodes for series {SeriesId}", episodes.Count, seriesId);

        if (episodes.Count == 0)
        {
            return null;
        }

        // Get user interactions for all episodes
        var episodeIds = episodes.Select(e => e.Id).ToList();
        var interactions = await _context.UserMediaInteractions
            .AsNoTracking()
            .Where(i => i.UserId == userId && episodeIds.Contains(i.MediaItemId))
            .ToListAsync();

        _logger.LogInformation("[SmartContinue] Found {Count} user interactions", interactions.Count);

        // Find the most recently watched episode (by LastPlayed)
        var lastWatched = interactions
            .Where(i => i.LastPlayed.HasValue)
            .OrderByDescending(i => i.LastPlayed)
            .FirstOrDefault();

        if (lastWatched != null)
        {
            var episode = episodes.First(e => e.Id == lastWatched.MediaItemId);
            
            // Check if this episode is complete
            bool isComplete = IsEpisodeComplete(episode, lastWatched);

            if (!isComplete)
            {
                // Resume this incomplete episode
                return new NextEpisodeResponse
                {
                    EpisodeId = episode.Id,
                    SeriesId = seriesId,
                    SeasonNumber = episode.SeasonNumber ?? 1,
                    EpisodeNumber = episode.EpisodeNumber ?? 1,
                    Title = episode.Title,
                    ResumePosition = lastWatched.PlaybackPosition ?? 0,
                    DebugDuration = episode.Duration,
                    DebugThreshold = episode.Duration * 0.95,
                    DebugIsComplete = isComplete
                };
            }
            else
            {
                // Find the next episode in sequence
                var currentIndex = episodes.FindIndex(e => e.Id == episode.Id);
                if (currentIndex < episodes.Count - 1)
                {
                    var nextEp = episodes[currentIndex + 1];
                    var nextInteraction = interactions.FirstOrDefault(i => i.MediaItemId == nextEp.Id);
                    return new NextEpisodeResponse
                    {
                        EpisodeId = nextEp.Id,
                        SeriesId = seriesId,
                        SeasonNumber = nextEp.SeasonNumber ?? 1,
                        EpisodeNumber = nextEp.EpisodeNumber ?? 1,
                        Title = nextEp.Title,
                        ResumePosition = nextInteraction?.PlaybackPosition ?? 0
                    };
                }
                else
                {
                    // Series complete - return first episode for rewatch
                    var firstEp = episodes[0];
                    return new NextEpisodeResponse
                    {
                        EpisodeId = firstEp.Id,
                        SeriesId = seriesId,
                        SeasonNumber = firstEp.SeasonNumber ?? 1,
                        EpisodeNumber = firstEp.EpisodeNumber ?? 1,
                        Title = firstEp.Title,
                        ResumePosition = 0,
                        IsSeriesComplete = true
                    };
                }
            }
        }
        else
        {
            // No watch history - start from first episode
            var firstEp = episodes[0];
            var firstInteraction = interactions.FirstOrDefault(i => i.MediaItemId == firstEp.Id);
            return new NextEpisodeResponse
            {
                EpisodeId = firstEp.Id,
                SeriesId = seriesId,
                SeasonNumber = firstEp.SeasonNumber ?? 1,
                EpisodeNumber = firstEp.EpisodeNumber ?? 1,
                Title = firstEp.Title,
                ResumePosition = firstInteraction?.PlaybackPosition ?? 0
            };
        }
    }

    public async Task<NextEpisodeResponse?> GetNextEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId)
    {
        _logger.LogInformation("[PlayNext] GetNextEpisodeFromCurrent called for episode {EpisodeId}", currentEpisodeId);

        // Get the current episode
        var currentEpisode = await _context.MediaItems
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == currentEpisodeId && m.Type == MediaType.Episode);

        if (currentEpisode == null || currentEpisode.SeriesId == null)
        {
            return null;
        }

        // Get all episodes for this series ordered by season/episode
        var episodes = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.SeriesId == currentEpisode.SeriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ToListAsync();

        // Find current episode index and get next
        var currentIndex = episodes.FindIndex(e => e.Id == currentEpisodeId);
        if (currentIndex < 0 || currentIndex >= episodes.Count - 1)
        {
            // No next episode - at end of series
            return new NextEpisodeResponse
            {
                EpisodeId = Guid.Empty,
                SeriesId = currentEpisode.SeriesId.Value,
                IsSeriesComplete = true
            };
        }

        var nextEpisode = episodes[currentIndex + 1];
        
        // Get user interaction for resume position
        var interaction = await _context.UserMediaInteractions
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == nextEpisode.Id);

        // Extract poster/backdrop
        var (posterPath, backdropPath) = ExtractImages(nextEpisode, currentEpisode.SeriesId);

        _logger.LogInformation("[PlayNext] Next episode: S{Season}E{Episode} - {Title}, PosterPath: {Poster}",
            nextEpisode.SeasonNumber, nextEpisode.EpisodeNumber, nextEpisode.Title, posterPath ?? "none");

        return new NextEpisodeResponse
        {
            EpisodeId = nextEpisode.Id,
            SeriesId = currentEpisode.SeriesId.Value,
            SeasonNumber = nextEpisode.SeasonNumber ?? 1,
            EpisodeNumber = nextEpisode.EpisodeNumber ?? 1,
            Title = nextEpisode.Title,
            ResumePosition = interaction?.PlaybackPosition ?? 0,
            PosterPath = posterPath,
            BackdropPath = backdropPath,
            IsSeriesComplete = false
        };
    }

    public async Task<NextEpisodeResponse?> GetPreviousEpisodeFromCurrentAsync(Guid userId, Guid currentEpisodeId)
    {
        _logger.LogInformation("[EpisodeNav] GetPreviousEpisodeFromCurrent called for episode {EpisodeId}", currentEpisodeId);

        // Get the current episode
        var currentEpisode = await _context.MediaItems
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == currentEpisodeId && m.Type == MediaType.Episode);

        if (currentEpisode == null || currentEpisode.SeriesId == null)
        {
            return null;
        }

        // Get all episodes for this series ordered by season/episode
        var episodes = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.SeriesId == currentEpisode.SeriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ToListAsync();

        // Find current episode index and get previous
        var currentIndex = episodes.FindIndex(e => e.Id == currentEpisodeId);
        if (currentIndex <= 0)
        {
            // No previous episode - at start of series
            return new NextEpisodeResponse
            {
                EpisodeId = Guid.Empty,
                SeriesId = currentEpisode.SeriesId.Value,
                IsSeriesComplete = false 
            };
        }

        var previousEpisode = episodes[currentIndex - 1];
        
        // Get user interaction for resume position
        var interaction = await _context.UserMediaInteractions
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == previousEpisode.Id);

        // Extract poster/backdrop
        var (posterPath, backdropPath) = ExtractImages(previousEpisode, currentEpisode.SeriesId);

        _logger.LogInformation("[EpisodeNav] Previous episode: S{Season}E{Episode} - {Title}",
            previousEpisode.SeasonNumber, previousEpisode.EpisodeNumber, previousEpisode.Title);

        return new NextEpisodeResponse
        {
            EpisodeId = previousEpisode.Id,
            SeriesId = currentEpisode.SeriesId.Value,
            SeasonNumber = previousEpisode.SeasonNumber ?? 1,
            EpisodeNumber = previousEpisode.EpisodeNumber ?? 1,
            Title = previousEpisode.Title,
            ResumePosition = interaction?.PlaybackPosition ?? 0,
            PosterPath = posterPath,
            BackdropPath = backdropPath,
            IsSeriesComplete = false
        };
    }

    private bool IsEpisodeComplete(MediaItem episode, UserMediaInteraction interaction)
    {
        if (interaction.IsWatched)
        {
            _logger.LogInformation("[SmartContinue] Episode {Title} marked IsWatched=true", episode.Title);
            return true;
        }
        if (episode.Duration <= 0)
        {
            _logger.LogWarning("[SmartContinue] Episode {Title} has no duration (Duration={Duration})", episode.Title, episode.Duration);
            return false;
        }

        var position = interaction.PlaybackPosition ?? 0;
        var threshold = episode.Duration * 0.95;
        var isComplete = position >= threshold;
        
        // Try to get credits timecode from metadata
        if (!string.IsNullOrEmpty(episode.MetadataJson))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(episode.MetadataJson);
                if (metadata != null && metadata.TryGetValue("creditsStart", out var creditsStartObj))
                {
                    if (double.TryParse(creditsStartObj.ToString(), out var creditsStart))
                    {
                        return position >= creditsStart;
                    }
                }
            }
            catch { /* Ignore JSON parse errors */ }
        }

        return isComplete;
    }

    private (string? Poster, string? Backdrop) ExtractImages(MediaItem episode, Guid? seriesId)
    {
        string? posterPath = null;
        string? backdropPath = null;
        
        // Try to get episode-specific thumbnail/poster
        if (!string.IsNullOrEmpty(episode.MetadataJson))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(episode.MetadataJson);
                if (metadata != null)
                {
                    // Check for episode thumbnail/still first (episodes typically use these)
                    if (metadata.TryGetValue("thumbnail", out var thumbObj) && !string.IsNullOrEmpty(thumbObj?.ToString()))
                        posterPath = $"/api/v1/image/proxy?url={Uri.EscapeDataString(thumbObj.ToString() ?? "")}";
                    else if (metadata.TryGetValue("still", out var stillObj) && !string.IsNullOrEmpty(stillObj?.ToString()))
                        posterPath = $"/api/v1/image/proxy?url={Uri.EscapeDataString(stillObj.ToString() ?? "")}";
                    else if (metadata.TryGetValue("poster", out var posterObj) && !string.IsNullOrEmpty(posterObj?.ToString()))
                        posterPath = $"/api/v1/image/proxy?url={Uri.EscapeDataString(posterObj.ToString() ?? "")}";
                    
                    if (metadata.TryGetValue("backdrop", out var backdropObj) && !string.IsNullOrEmpty(backdropObj?.ToString()))
                        backdropPath = $"/api/v1/image/proxy?url={Uri.EscapeDataString(backdropObj.ToString() ?? "")}";
                }
            }
            catch { /* Ignore JSON parse errors */ }
        }
        
        // Fallback to series poster/backdrop if no episode-specific image found
        if ((string.IsNullOrEmpty(posterPath) || string.IsNullOrEmpty(backdropPath)) && seriesId.HasValue)
        {
            var series = _context.MediaItems.AsNoTracking().FirstOrDefault(m => m.Id == seriesId);
            if (series != null && !string.IsNullOrEmpty(series.MetadataJson))
            {
                try
                {
                    var seriesMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
                    if (seriesMetadata != null)
                    {
                        if (string.IsNullOrEmpty(posterPath) && seriesMetadata.TryGetValue("poster", out var seriesPosterObj) && !string.IsNullOrEmpty(seriesPosterObj?.ToString()))
                            posterPath = $"/api/v1/image/proxy?url={Uri.EscapeDataString(seriesPosterObj.ToString() ?? "")}";
                        if (string.IsNullOrEmpty(backdropPath) && seriesMetadata.TryGetValue("backdrop", out var seriesBackdropObj) && !string.IsNullOrEmpty(seriesBackdropObj?.ToString()))
                            backdropPath = $"/api/v1/image/proxy?url={Uri.EscapeDataString(seriesBackdropObj.ToString() ?? "")}";
                    }
                }
                catch { /* Ignore JSON parse errors */ }
            }
        }

        return (posterPath, backdropPath);
    }
}
