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
            communityRating = ratings.Average(r => r.Value);
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
