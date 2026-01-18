using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/interaction")]
public class InteractionController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<InteractionController> _logger;

    public InteractionController(AppDbContext context, ILogger<InteractionController> logger)
    {
        _context = context;
        _logger = logger;
    }

    private Guid GetUserId()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user ID");
        }
        return userId;
    }

    [HttpPost("{mediaId}/rate")]
    public async Task<IActionResult> RateMedia(Guid mediaId, [FromBody] RateRequest request)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.Rating = request.Rating;
        await _context.SaveChangesAsync();

        // Recalculate average rating
        var ratings = await _context.UserMediaInteractions
            .Where(x => x.MediaItemId == mediaId && x.Rating != null)
            .Select(x => x.Rating)
            .ToListAsync();

        double? communityRating = null;
        if (ratings.Any())
        {
            communityRating = ratings.Average(r => r ?? 0);
        }

        var mediaItem = await _context.MediaItems.FindAsync(mediaId);
        if (mediaItem != null)
        {
            mediaItem.CommunityRating = communityRating;
            await _context.SaveChangesAsync();
        }

        return Ok();
    }

    [HttpPost("{mediaId}/favorite")]
    public async Task<IActionResult> ToggleFavorite(Guid mediaId, [FromBody] FavoriteRequest request)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsFavorite = request.IsFavorite;
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpPost("{mediaId}/watched")]
    public async Task<IActionResult> MarkWatched(Guid mediaId, [FromBody] WatchedRequest request)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.IsWatched = request.Watched;
        if (request.Watched)
        {
            interaction.LastPlayed = DateTime.UtcNow;
            // Reset playback position when marked as watched so it starts from beginning next time
            interaction.PlaybackPosition = 0;
        }
        
        await _context.SaveChangesAsync();

        return Ok();
    }

    // Maintenance endpoint removed after execution

    [HttpPost("{mediaId}/progress")]
    public async Task<IActionResult> UpdateProgress(Guid mediaId, [FromBody] ProgressRequest request)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        if (interaction == null)
        {
            interaction = new UserMediaInteraction
            {
                UserId = userId,
                MediaItemId = mediaId
            };
            _context.UserMediaInteractions.Add(interaction);
        }

        interaction.PlaybackPosition = request.Position;
        interaction.LastPlayed = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpGet("{mediaId}/progress")]
    public async Task<ActionResult<ProgressResponse>> GetProgress(Guid mediaId)
    {
        var userId = GetUserId();
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(x => x.UserId == userId && x.MediaItemId == mediaId);

        return Ok(new ProgressResponse
        {
            Position = interaction?.PlaybackPosition ?? 0,
            LastPlayed = interaction?.LastPlayed
        });
    }

    /// <summary>
    /// Get the next episode to play for a TV series based on user's watch history.
    /// Returns the most recently watched incomplete episode, or the next episode after completed ones.
    /// </summary>
    [HttpGet("/api/v1/series/{seriesId}/next-episode")]
    public async Task<ActionResult<NextEpisodeResponse>> GetNextEpisode(Guid seriesId)
    {
        _logger.LogInformation("[SmartContinue] GetNextEpisode called for series {SeriesId}", seriesId);
        var userId = GetUserId();

        // Get all episodes for this series ordered by season/episode
        var episodes = await _context.MediaItems
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ToListAsync();

        _logger.LogInformation("[SmartContinue] Found {Count} episodes for series {SeriesId}", episodes.Count, seriesId);

        if (episodes.Count == 0)
        {
            return NotFound(new { message = "No episodes found for this series" });
        }

        // Get user interactions for all episodes
        var episodeIds = episodes.Select(e => e.Id).ToList();
        var interactions = await _context.UserMediaInteractions
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
                return Ok(new NextEpisodeResponse
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
                });
            }
            else
            {
                // Find the next episode in sequence
                var currentIndex = episodes.FindIndex(e => e.Id == episode.Id);
                if (currentIndex < episodes.Count - 1)
                {
                    var nextEp = episodes[currentIndex + 1];
                    var nextInteraction = interactions.FirstOrDefault(i => i.MediaItemId == nextEp.Id);
                    return Ok(new NextEpisodeResponse
                    {
                        EpisodeId = nextEp.Id,
                        SeriesId = seriesId,
                        SeasonNumber = nextEp.SeasonNumber ?? 1,
                        EpisodeNumber = nextEp.EpisodeNumber ?? 1,
                        Title = nextEp.Title,
                        ResumePosition = nextInteraction?.PlaybackPosition ?? 0
                    });
                }
                else
                {
                    // Series complete - return first episode for rewatch
                    var firstEp = episodes[0];
                    return Ok(new NextEpisodeResponse
                    {
                        EpisodeId = firstEp.Id,
                        SeriesId = seriesId,
                        SeasonNumber = firstEp.SeasonNumber ?? 1,
                        EpisodeNumber = firstEp.EpisodeNumber ?? 1,
                        Title = firstEp.Title,
                        ResumePosition = 0,
                        IsSeriesComplete = true
                    });
                }
            }
        }
        else
        {
            // No watch history - start from first episode
            var firstEp = episodes[0];
            var firstInteraction = interactions.FirstOrDefault(i => i.MediaItemId == firstEp.Id);
            return Ok(new NextEpisodeResponse
            {
                EpisodeId = firstEp.Id,
                SeriesId = seriesId,
                SeasonNumber = firstEp.SeasonNumber ?? 1,
                EpisodeNumber = firstEp.EpisodeNumber ?? 1,
                Title = firstEp.Title,
                ResumePosition = firstInteraction?.PlaybackPosition ?? 0
            });
        }
    }

    /// <summary>
    /// Check if an episode is complete based on credits timecode or 95% threshold
    /// </summary>
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
        
        _logger.LogInformation("[SmartContinue] Episode: {Title}, Duration: {Duration}s, Position: {Position}s, Threshold: {Threshold}s, IsComplete: {IsComplete}",
            episode.Title, episode.Duration, position, threshold, isComplete);

        // Try to get credits timecode from metadata
        if (!string.IsNullOrEmpty(episode.MetadataJson))
        {
            try
            {
                var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(episode.MetadataJson);
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

        // Fallback: 95% of duration
        return isComplete;
    }

    /// <summary>
    /// Save subtitle preference for a TV series.
    /// This preference will apply to all episodes in the series.
    /// </summary>
    [HttpPost("/api/v1/series/{seriesId}/subtitle-preference")]
    public async Task<IActionResult> SaveSubtitlePreference(Guid seriesId, [FromBody] SubtitlePreferenceRequest request)
    {
        var userId = GetUserId();
        
        var preference = await _context.UserSeriesPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.SeriesId == seriesId);

        if (preference == null)
        {
            preference = new UserSeriesPreference
            {
                UserId = userId,
                SeriesId = seriesId
            };
            _context.UserSeriesPreferences.Add(preference);
        }

        preference.PreferredSubtitleLanguage = request.Language;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Saved subtitle preference '{Language}' for series {SeriesId} by user {UserId}", 
            request.Language ?? "off", seriesId, userId);

        return Ok();
    }

    /// <summary>
    /// Get subtitle preference for a TV series.
    /// </summary>
    [HttpGet("/api/v1/series/{seriesId}/subtitle-preference")]
    public async Task<ActionResult<SubtitlePreferenceResponse>> GetSubtitlePreference(Guid seriesId)
    {
        var userId = GetUserId();
        
        var preference = await _context.UserSeriesPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.SeriesId == seriesId);

        return Ok(new SubtitlePreferenceResponse
        {
            Language = preference?.PreferredSubtitleLanguage
        });
    }

    /// <summary>
    /// Save audio preference for a TV series.
    /// This preference will apply to all episodes in the series.
    /// </summary>
    [HttpPost("/api/v1/series/{seriesId}/audio-preference")]
    public async Task<IActionResult> SaveAudioPreference(Guid seriesId, [FromBody] AudioPreferenceRequest request)
    {
        var userId = GetUserId();
        
        var preference = await _context.UserSeriesPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.SeriesId == seriesId);

        if (preference == null)
        {
            preference = new UserSeriesPreference
            {
                UserId = userId,
                SeriesId = seriesId
            };
            _context.UserSeriesPreferences.Add(preference);
        }

        preference.PreferredAudioLanguage = request.Language;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Saved audio preference '{Language}' for series {SeriesId} by user {UserId}", 
            request.Language ?? "default", seriesId, userId);

        return Ok();
    }

    /// <summary>
    /// Get audio preference for a TV series.
    /// </summary>
    [HttpGet("/api/v1/series/{seriesId}/audio-preference")]
    public async Task<ActionResult<AudioPreferenceResponse>> GetAudioPreference(Guid seriesId)
    {
        var userId = GetUserId();
        
        var preference = await _context.UserSeriesPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.SeriesId == seriesId);

        return Ok(new AudioPreferenceResponse
        {
            Language = preference?.PreferredAudioLanguage
        });
    }

    /// <summary>
    /// Get the next episode after a specific episode (for "play next" overlay).
    /// Returns the next episode in sequence or IsSeriesComplete if at end.
    /// </summary>
    [HttpGet("/api/v1/episode/{episodeId}/next")]
    public async Task<ActionResult<NextEpisodeResponse>> GetNextEpisodeFromCurrent(Guid episodeId)
    {
        _logger.LogInformation("[PlayNext] GetNextEpisodeFromCurrent called for episode {EpisodeId}", episodeId);
        var userId = GetUserId();

        // Get the current episode
        var currentEpisode = await _context.MediaItems
            .FirstOrDefaultAsync(m => m.Id == episodeId && m.Type == MediaType.Episode);

        if (currentEpisode == null)
        {
            return NotFound(new { message = "Episode not found" });
        }

        if (currentEpisode.SeriesId == null)
        {
            return NotFound(new { message = "Episode is not part of a series" });
        }

        // Get all episodes for this series ordered by season/episode
        var episodes = await _context.MediaItems
            .Where(m => m.SeriesId == currentEpisode.SeriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ToListAsync();

        // Find current episode index and get next
        var currentIndex = episodes.FindIndex(e => e.Id == episodeId);
        if (currentIndex < 0 || currentIndex >= episodes.Count - 1)
        {
            // No next episode - at end of series
            return Ok(new NextEpisodeResponse
            {
                EpisodeId = Guid.Empty,
                SeriesId = currentEpisode.SeriesId.Value,
                IsSeriesComplete = true
            });
        }

        var nextEpisode = episodes[currentIndex + 1];
        
        // Get user interaction for resume position
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == nextEpisode.Id);

        // Extract poster/backdrop from metadata
        string? posterPath = null;
        string? backdropPath = null;
        
        // Try to get episode-specific thumbnail/poster
        if (!string.IsNullOrEmpty(nextEpisode.MetadataJson))
        {
            try
            {
                var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(nextEpisode.MetadataJson);
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
        if (string.IsNullOrEmpty(posterPath) || string.IsNullOrEmpty(backdropPath))
        {
            var series = await _context.MediaItems.FirstOrDefaultAsync(m => m.Id == currentEpisode.SeriesId);
            if (series != null && !string.IsNullOrEmpty(series.MetadataJson))
            {
                try
                {
                    var seriesMetadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
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

        _logger.LogInformation("[PlayNext] Next episode: S{Season}E{Episode} - {Title}, PosterPath: {Poster}",
            nextEpisode.SeasonNumber, nextEpisode.EpisodeNumber, nextEpisode.Title, posterPath ?? "none");

        return Ok(new NextEpisodeResponse
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
        });
    }

    /// <summary>
    /// Get the previous episode before a specific episode (for navigation buttons).
    /// Returns the previous episode in sequence or indicates if at the start.
    /// </summary>
    [HttpGet("/api/v1/episode/{episodeId}/previous")]
    public async Task<ActionResult<NextEpisodeResponse>> GetPreviousEpisodeFromCurrent(Guid episodeId)
    {
        _logger.LogInformation("[EpisodeNav] GetPreviousEpisodeFromCurrent called for episode {EpisodeId}", episodeId);
        var userId = GetUserId();

        // Get the current episode
        var currentEpisode = await _context.MediaItems
            .FirstOrDefaultAsync(m => m.Id == episodeId && m.Type == MediaType.Episode);

        if (currentEpisode == null)
        {
            return NotFound(new { message = "Episode not found" });
        }

        if (currentEpisode.SeriesId == null)
        {
            return NotFound(new { message = "Episode is not part of a series" });
        }

        // Get all episodes for this series ordered by season/episode
        var episodes = await _context.MediaItems
            .Where(m => m.SeriesId == currentEpisode.SeriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber)
            .ThenBy(m => m.EpisodeNumber)
            .ToListAsync();

        // Find current episode index and get previous
        var currentIndex = episodes.FindIndex(e => e.Id == episodeId);
        if (currentIndex <= 0)
        {
            // No previous episode - at start of series
            return Ok(new NextEpisodeResponse
            {
                EpisodeId = Guid.Empty,
                SeriesId = currentEpisode.SeriesId.Value,
                IsSeriesComplete = false // Using this field to indicate "no previous"
            });
        }

        var previousEpisode = episodes[currentIndex - 1];
        
        // Get user interaction for resume position
        var interaction = await _context.UserMediaInteractions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.MediaItemId == previousEpisode.Id);

        // Extract poster/backdrop from metadata
        string? posterPath = null;
        string? backdropPath = null;
        
        if (!string.IsNullOrEmpty(previousEpisode.MetadataJson))
        {
            try
            {
                var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(previousEpisode.MetadataJson);
                if (metadata != null)
                {
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
        if (string.IsNullOrEmpty(posterPath) || string.IsNullOrEmpty(backdropPath))
        {
            var series = await _context.MediaItems.FirstOrDefaultAsync(m => m.Id == currentEpisode.SeriesId);
            if (series != null && !string.IsNullOrEmpty(series.MetadataJson))
            {
                try
                {
                    var seriesMetadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(series.MetadataJson);
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

        _logger.LogInformation("[EpisodeNav] Previous episode: S{Season}E{Episode} - {Title}",
            previousEpisode.SeasonNumber, previousEpisode.EpisodeNumber, previousEpisode.Title);

        return Ok(new NextEpisodeResponse
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
        });
    }
}

public class RateRequest
{
    public int? Rating { get; set; }
}

public class FavoriteRequest
{
    public bool IsFavorite { get; set; }
}

public class WatchedRequest
{
    public bool Watched { get; set; }
}

public class ProgressRequest
{
    public double Position { get; set; }
}

public class ProgressResponse
{
    public double Position { get; set; }
    public DateTime? LastPlayed { get; set; }
}

public class NextEpisodeResponse
{
    public Guid EpisodeId { get; set; }
    public Guid SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public double ResumePosition { get; set; }
    public bool IsSeriesComplete { get; set; }
    
    /// <summary>Poster image URL for the next episode</summary>
    public string? PosterPath { get; set; }
    
    /// <summary>Backdrop image URL for the next episode</summary>
    public string? BackdropPath { get; set; }
    
    // Debug fields
    public double DebugDuration { get; set; }
    public double DebugThreshold { get; set; }
    public bool DebugIsComplete { get; set; }
}

public class SubtitlePreferenceRequest
{
    public string? Language { get; set; }
}

public class SubtitlePreferenceResponse
{
    public string? Language { get; set; }
}

public class AudioPreferenceRequest
{
    public string? Language { get; set; }
}

public class AudioPreferenceResponse
{
    public string? Language { get; set; }
}
