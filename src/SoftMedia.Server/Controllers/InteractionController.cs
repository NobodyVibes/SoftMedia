using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using SoftMedia.Server.Services.Sessions;
using SoftMedia.Server.DTOs;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

// All interaction endpoints mutate user playback state, so the controller requires
// the write:state scope. JWT/cookie sessions satisfy this automatically (scopes only
// constrain API tokens — see ScopeAuthorizationHandler); a read-only API token is 403.
[Authorize(Policy = ScopePolicies.WriteState)]
[ApiController]
[Route("api/v1/interaction")]
public class InteractionController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<InteractionController> _logger;
    private readonly IRecommendationService _recommendationService;
    private readonly IUserMediaInteractionService _interactionService;
    private readonly IUserLibraryAccessProvider _libraryAccess;
    private readonly IUserContentRatingProvider _ratings;
    private readonly IActiveStreamRegistry _streamRegistry;
    private readonly ITerminatedSessionRegistry _terminatedSessions;
    private readonly SoftMedia.Server.Services.Transcoding.ITranscodeService _transcodeService;

    public InteractionController(
        AppDbContext context,
        ILogger<InteractionController> logger,
        IRecommendationService recommendationService,
        IUserMediaInteractionService interactionService,
        IUserLibraryAccessProvider libraryAccess,
        IUserContentRatingProvider ratings,
        IActiveStreamRegistry streamRegistry,
        ITerminatedSessionRegistry terminatedSessions,
        SoftMedia.Server.Services.Transcoding.ITranscodeService transcodeService)
    {
        _context = context;
        _logger = logger;
        _recommendationService = recommendationService;
        _interactionService = interactionService;
        _libraryAccess = libraryAccess;
        _ratings = ratings;
        _streamRegistry = streamRegistry;
        _terminatedSessions = terminatedSessions;
        _transcodeService = transcodeService;
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

    /// <summary>
    /// Wave E3 — adds or removes the media item from the calling user's watchlist.
    /// Idempotent: re-adding refreshes the WatchlistedAt timestamp.
    ///
    /// Music items (Artist / Album / Audio) are rejected: the playlist feature
    /// covers "I want to come back to this" for music. Mixing the two creates
    /// a confusing UX where albums could be both watchlisted AND on a playlist.
    /// </summary>
    [HttpPost("{mediaId}/watchlist")]
    public async Task<IActionResult> ToggleWatchlist(Guid mediaId, [FromBody] WatchlistRequest request)
    {
        var userId = GetUserId();
        // Validate the media exists and capture its type for the music-type guard.
        var media = await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.Id == mediaId)
            .Select(m => new { m.Type })
            .FirstOrDefaultAsync();
        if (media == null) return NotFound();

        if (media.Type == MediaType.Audio
            || media.Type == MediaType.Album
            || media.Type == MediaType.Artist)
        {
            return BadRequest("Watchlist is not available for music. Use playlists instead.");
        }

        await _interactionService.ToggleWatchlistAsync(userId, mediaId, request.IsWatchlisted);
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

        // R-WI-016: beats double as the direct-play liveness heartbeat (and can create
        // the entry — needed for fully browser-cached plays and post-restart recovery,
        // since the in-memory registry only otherwise learns of plays via /stream).
        // Guards: (1) a LIVE transcode for this user+media means the beat belongs to
        // the transcode, not a direct play — creating would double-list it. The state
        // filter is load-bearing: closing the player parks the session DORMANT for the
        // segment-retention window (up to 24h) — an unfiltered check suppressed
        // direct-play tracking of that media all day (review HIGH). (2) Only streamable
        // types, and only media the user can actually access — beats are writable for
        // arbitrary ids, and unchecked creation lets a user fabricate dashboard rows
        // for content outside their libraries/rating ceiling (review MED).
        var hasLiveTranscode = _transcodeService.GetAllSessions()
            .Any(s => s.UserId == userId && s.Key.MediaId == mediaId
                      && s.State != Services.Transcoding.Models.TranscodeState.Dormant
                      && s.State != Services.Transcoding.Models.TranscodeState.Completed);
        // (3) A stream an admin just stopped keeps beating while the player drains its buffer
        // and retries. Guard (1) no longer covers that — the transcode it keyed off is exactly
        // what was removed — so those beats registered the title as a DIRECT PLAY and the
        // dashboard listed it twice once the viewer pressed play again. Only CREATION is
        // barred, not the beat itself: a real direct play opens a /stream request, and
        // suppressing its heartbeat too would leave that genuine row frozen at 0:00 under the
        // "Streaming" label (which means "open stream, maybe not playback") until the window
        // passed. Touching an entry /stream already created cannot conjure a phantom.
        var recentlyStopped = _terminatedSessions.WasRecentlyTerminatedForUser(mediaId, userId);
        if (!hasLiveTranscode && await IsStreamableAndAccessibleAsync(userId, mediaId))
        {
            _streamRegistry.TouchOrCreate(userId, mediaId, request.Position, Request.GetClientDevice(),
                createIfMissing: !recentlyStopped);
        }
        return Ok();
    }

    /// <summary>
    /// R-WI-016 beat-creation gate: streamable type AND within the caller's library
    /// access + rating ceiling — progress beats accept arbitrary ids, and an
    /// unchecked TouchOrCreate would let a user paint the admin dashboard with
    /// content they cannot access. One PK-indexed query per ~10s beat; the ACL
    /// providers cache per scope. Deliberately NOT cached across beats: access and
    /// ceilings are admin-editable and a stale allow would defeat the gate.
    /// </summary>
    private async Task<bool> IsStreamableAndAccessibleAsync(Guid userId, Guid mediaId)
    {
        _ = userId; // scoping is via the caller-derived access/ceiling providers
        var access = await _libraryAccess.GetCurrentAsync();
        var ceilings = await _ratings.GetCurrentAsync();
        var mediaType = await _context.MediaItems.AsNoTracking()
            .Where(m => m.Id == mediaId)
            .ApplyLibraryAccessFilter(access)
            .ApplyContentRatingFilter(ceilings)
            .Select(m => (MediaType?)m.Type)
            .FirstOrDefaultAsync();
        return mediaType is MediaType.Movie or MediaType.Episode or MediaType.Audio;
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
            LastPlayed = interaction?.LastPlayed,
            IsWatched = interaction?.IsWatched ?? false
        });
    }

    /// <summary>
    /// R-WI-013 — the calling user's play history (video + music), newest first. SELF-scoped:
    /// a user only ever sees their own rows; there is deliberately no browse-others admin
    /// endpoint (privacy-first charter — maintainer can revisit).
    /// </summary>
    [HttpGet("history")]
    public async Task<ActionResult<IEnumerable<PlaybackHistoryEntryDto>>> GetHistory(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var userId = GetUserId();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Gate by the caller's current access so history can't leak titles of media they've
        // lost library access to or that a lowered rating ceiling now hides.
        var access = await _libraryAccess.GetCurrentAsync();
        var ceilings = await _ratings.GetCurrentAsync();

        var rows = await _interactionService.GetHistoryAsync(userId, page, pageSize, access, ceilings);
        return Ok(rows.Select(h => new PlaybackHistoryEntryDto(
            h.Id, h.MediaItemId, h.MediaItem.Title, h.MediaType.ToString(),
            h.StartedAt, h.LastBeatAt, h.MaxPosition, h.MediaItem.Duration, h.Completed)));
    }

    // ── ER-012: per-book reader preference overrides ──────────────────────────

    /// <summary>
    /// Returns this user's saved reader-pref overrides for a specific book, if
    /// any. Missing row → null Preferences with SchemaVersion 0, signalling
    /// "use global defaults only."
    /// </summary>
    [HttpGet("{mediaId}/reader-preferences")]
    public async Task<ActionResult<ReaderPreferencesResponse>> GetReaderPreferences(Guid mediaId)
    {
        var userId = GetUserId();
        var row = await _context.UserReaderPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.MediaItemId == mediaId);

        return Ok(new ReaderPreferencesResponse
        {
            SchemaVersion = row?.SchemaVersion ?? 0,
            PreferencesJson = row?.PreferencesJson,
            UpdatedAt = row?.UpdatedAt
        });
    }

    /// <summary>
    /// Upserts this user's per-book reader-pref overrides. A null / empty
    /// payload clears the row so the book returns to global defaults.
    /// Payload is treated as opaque by the server — the client owns the schema.
    /// </summary>
    // ── ER-052: Reading sessions ──────────────────────────────────────────

    /// <summary>
    /// Start a reading session. The client calls this on reader mount and
    /// holds onto the returned id for the lifetime of the session. Concurrent
    /// sessions for the same (user, book) are permitted — closing one doesn't
    /// affect the other; the summary endpoint sums them all.
    /// </summary>
    [HttpPost("{mediaId}/sessions/start")]
    public async Task<ActionResult<StartSessionResponse>> StartSession(Guid mediaId)
    {
        var userId = GetUserId();
        var exists = await _context.MediaItems.AnyAsync(m => m.Id == mediaId);
        if (!exists) return NotFound();

        var session = new ReadingSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MediaItemId = mediaId,
            StartedAt = DateTime.UtcNow,
        };
        _context.ReadingSessions.Add(session);
        await _context.SaveChangesAsync();
        return Ok(new StartSessionResponse { SessionId = session.Id });
    }

    /// <summary>
    /// End a reading session. When PagesRead is zero, the session is deleted
    /// rather than persisted — idle-timeout closures mustn't pollute stats.
    /// </summary>
    [HttpPost("{mediaId}/sessions/{sessionId}/end")]
    public async Task<IActionResult> EndSession(Guid mediaId, Guid sessionId, [FromBody] EndSessionRequest request)
    {
        var userId = GetUserId();
        var session = await _context.ReadingSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.MediaItemId == mediaId);
        if (session == null) return NotFound();

        if (request.PagesRead <= 0)
        {
            _context.ReadingSessions.Remove(session);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        session.EndedAt = DateTime.UtcNow;
        session.PagesRead = Math.Min(request.PagesRead, 10_000);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Per-book reading summary for the current user. Totals are the sum of
    /// completed sessions only (EndedAt is non-null). pages/min averages
    /// across all completed sessions, weighted by duration.
    /// </summary>
    [HttpGet("{mediaId}/sessions/summary")]
    public async Task<ActionResult<ReadingSessionSummary>> GetSessionSummary(Guid mediaId)
    {
        var userId = GetUserId();
        var rows = await _context.ReadingSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.MediaItemId == mediaId && s.EndedAt != null)
            .Select(s => new { s.StartedAt, s.EndedAt, s.PagesRead })
            .ToListAsync();

        if (rows.Count == 0)
        {
            return Ok(new ReadingSessionSummary());
        }

        double totalSeconds = 0;
        int totalPages = 0;
        foreach (var r in rows)
        {
            var end = r.EndedAt!.Value;
            totalSeconds += Math.Max(0, (end - r.StartedAt).TotalSeconds);
            totalPages += r.PagesRead;
        }

        var minutes = totalSeconds / 60.0;
        return Ok(new ReadingSessionSummary
        {
            SessionCount = rows.Count,
            TotalMinutes = Math.Round(minutes, 1),
            TotalPages = totalPages,
            PagesPerMinute = minutes > 0 ? Math.Round(totalPages / minutes, 2) : 0,
        });
    }

    [HttpPut("{mediaId}/reader-preferences")]
    public async Task<IActionResult> PutReaderPreferences(Guid mediaId, [FromBody] ReaderPreferencesRequest request)
    {
        var userId = GetUserId();

        // Confirm the media item exists before writing — prevents dangling
        // preference rows if a typo'd id sneaks through the client.
        var exists = await _context.MediaItems.AnyAsync(m => m.Id == mediaId);
        if (!exists) return NotFound();

        var row = await _context.UserReaderPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.MediaItemId == mediaId);

        // Empty or null payload → clear the row. Keeping the pref blob
        // semantically means "no overrides" so deleting is the right behaviour.
        var clearing = string.IsNullOrWhiteSpace(request.PreferencesJson)
                       || request.PreferencesJson.Trim() == "{}";

        if (clearing)
        {
            if (row != null)
            {
                _context.UserReaderPreferences.Remove(row);
                await _context.SaveChangesAsync();
            }
            return NoContent();
        }

        // Cap payload size defensively. The [MaxLength(8192)] attribute on the
        // column provides the ultimate bound; checking here gives the client a
        // clean 400 instead of a provider exception.
        if (request.PreferencesJson!.Length > 8192)
        {
            return BadRequest("PreferencesJson exceeds the 8 KB limit.");
        }

        if (row == null)
        {
            row = new UserReaderPreferences
            {
                UserId = userId,
                MediaItemId = mediaId,
            };
            _context.UserReaderPreferences.Add(row);
        }

        row.SchemaVersion = request.SchemaVersion <= 0 ? 1 : request.SchemaVersion;
        row.PreferencesJson = request.PreferencesJson!;
        row.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return NoContent();
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

    /// <summary>
    /// Recommendations for the player's end-of-movie overlay: unfinished movies from the same
    /// collection first (marathon-friendly), then genre-similar movies. 404 covers both a
    /// nonexistent movie and one the caller can't see (anti-probe).
    /// </summary>
    [HttpGet("/api/v1/movie/{movieId}/post-play")]
    public async Task<ActionResult<PostPlayResponse>> GetMoviePostPlay(Guid movieId)
    {
        var userId = GetUserId();
        var result = await _recommendationService.GetMoviePostPlayAsync(userId, movieId);

        if (result == null)
        {
            return NotFound(new { message = "Movie not found" });
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

public class WatchlistRequest
{
    public bool IsWatchlisted { get; set; }
}

/// <summary>R-WI-013 — one play in the self-scoped history feed.</summary>
public record PlaybackHistoryEntryDto(
    Guid Id, Guid MediaItemId, string Title, string MediaType,
    DateTime StartedAt, DateTime LastBeatAt, double MaxPosition, double Duration, bool Completed);

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
    public bool IsWatched { get; set; }
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

// ── ER-012 DTOs ──────────────────────────────────────────────────────────────

public class ReaderPreferencesRequest
{
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Opaque JSON payload owned by the client.</summary>
    public string? PreferencesJson { get; set; }
}

public class ReaderPreferencesResponse
{
    /// <summary>0 when no row exists; otherwise the stored schema version.</summary>
    public int SchemaVersion { get; set; }
    public string? PreferencesJson { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// ── ER-052 DTOs ──────────────────────────────────────────────────────────────

public class StartSessionResponse
{
    public Guid SessionId { get; set; }
}

public class EndSessionRequest
{
    public int PagesRead { get; set; }
}

public class ReadingSessionSummary
{
    public int SessionCount { get; set; }
    public double TotalMinutes { get; set; }
    public int TotalPages { get; set; }
    public double PagesPerMinute { get; set; }
}
