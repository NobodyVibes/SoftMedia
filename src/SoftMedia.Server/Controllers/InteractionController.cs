using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.DTOs;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/interaction")]
public class InteractionController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<InteractionController> _logger;
    private readonly IRecommendationService _recommendationService;
    private readonly IUserMediaInteractionService _interactionService;

    public InteractionController(
        AppDbContext context, 
        ILogger<InteractionController> logger, 
        IRecommendationService recommendationService,
        IUserMediaInteractionService interactionService)
    {
        _context = context;
        _logger = logger;
        _recommendationService = recommendationService;
        _interactionService = interactionService;
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
        await _interactionService.RateMediaAsync(userId, mediaId, request.Rating);
        return Ok();
    }

    [HttpPost("{mediaId}/favorite")]
    public async Task<IActionResult> ToggleFavorite(Guid mediaId, [FromBody] FavoriteRequest request)
    {
        var userId = GetUserId();
        await _interactionService.ToggleFavoriteAsync(userId, mediaId, request.IsFavorite);
        return Ok();
    }

    [HttpPost("{mediaId}/watched")]
    public async Task<IActionResult> MarkWatched(Guid mediaId, [FromBody] WatchedRequest request)
    {
        var userId = GetUserId();
        await _interactionService.MarkWatchedAsync(userId, mediaId, request.Watched);
        return Ok();
    }

    [HttpPost("{mediaId}/progress")]
    public async Task<IActionResult> UpdateProgress(Guid mediaId, [FromBody] ProgressRequest request)
    {
        var userId = GetUserId();
        await _interactionService.UpdateProgressAsync(userId, mediaId, request.Position, request.BookLocation);
        return Ok();
    }

    [HttpGet("{mediaId}/progress")]
    public async Task<ActionResult<ProgressResponse>> GetProgress(Guid mediaId)
    {
        var userId = GetUserId();
        var interaction = await _interactionService.GetInteractionAsync(userId, mediaId);

        return Ok(new ProgressResponse
        {
            Position = interaction?.PlaybackPosition ?? 0,
            BookLocation = interaction?.BookLocation,
            LastPlayed = interaction?.LastPlayed
        });
    }

    /// <summary>
    /// Get the next episode to play for a TV series based on user's watch history.
    /// Returns the most recently watched incomplete episode, or the next episode after completed ones.
    /// </summary>
    /// <summary>
    /// Get the next episode to play for a TV series based on user's watch history.
    /// Returns the most recently watched incomplete episode, or the next episode after completed ones.
    /// </summary>
    [HttpGet("/api/v1/series/{seriesId}/next-episode")]
    public async Task<ActionResult<NextEpisodeResponse>> GetNextEpisode(Guid seriesId)
    {
        var userId = GetUserId();
        var result = await _recommendationService.GetNextEpisodeAsync(userId, seriesId);
        
        if (result == null)
        {
            return NotFound(new { message = "No episodes found for this series" });
        }
        
        return Ok(result);
    }

    /// <summary>
    /// Check if an episode is complete based on credits timecode or 95% threshold
    /// </summary>


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
    /// <summary>
    /// Get the next episode after a specific episode (for "play next" overlay).
    /// Returns the next episode in sequence or IsSeriesComplete if at end.
    /// </summary>
    [HttpGet("/api/v1/episode/{episodeId}/next")]
    public async Task<ActionResult<NextEpisodeResponse>> GetNextEpisodeFromCurrent(Guid episodeId)
    {
        var userId = GetUserId();
        var result = await _recommendationService.GetNextEpisodeFromCurrentAsync(userId, episodeId);
        
        if (result == null)
        {
             return NotFound(new { message = "Episode not found or no next episode" });
        }
        
        return Ok(result);
    }

    /// <summary>
    /// Get the previous episode before a specific episode (for navigation buttons).
    /// Returns the previous episode in sequence or indicates if at the start.
    /// </summary>
    /// <summary>
    /// Get the previous episode before a specific episode (for navigation buttons).
    /// Returns the previous episode in sequence or indicates if at the start.
    /// </summary>
    [HttpGet("/api/v1/episode/{episodeId}/previous")]
    public async Task<ActionResult<NextEpisodeResponse>> GetPreviousEpisodeFromCurrent(Guid episodeId)
    {
        var userId = GetUserId();
        var result = await _recommendationService.GetPreviousEpisodeFromCurrentAsync(userId, episodeId);
        
        if (result == null)
        {
             return NotFound(new { message = "Episode not found or no previous episode" });
        }
        
        return Ok(result);
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
    /// <summary>Opaque location string for books (e.g., EPUB CFI). Null for video/audio.</summary>
    public string? BookLocation { get; set; }
}

public class ProgressResponse
{
    public double Position { get; set; }
    public string? BookLocation { get; set; }
    public DateTime? LastPlayed { get; set; }
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
