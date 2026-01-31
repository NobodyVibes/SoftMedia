using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;

using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Abstractions;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Services.Media;

using SoftMedia.Server.Services.Transcoding.Models;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for HLS transcoding endpoints with throttling support.
/// Requires authentication to prevent unauthorized access.
/// Supports optional subtitle burn-in via ?sub=trackIndex query parameter.
/// </summary>
[Authorize]
[ApiController]
[Route("api/transcode")]
public class TranscodeController : ControllerBase
{
    private readonly TranscodeService _transcodeService;
    private readonly IStreamPlanService _streamPlanService;
    private readonly ISettingsService _settingsService;
    private readonly AppDbContext _context;
    private readonly ILogger<TranscodeController> _logger;
    private readonly IHlsManifestService _hlsManifestService;
    private readonly ITranscodeDebugService _debugService;
    private readonly IVideoPreviewService _videoPreviewService;
    private readonly IStreamSecurityService _streamSecurityService;

    public TranscodeController(
        TranscodeService transcodeService, 
        IStreamPlanService streamPlanService,
        ISettingsService settingsService,
        AppDbContext context,
        IHlsManifestService hlsManifestService,
        ITranscodeDebugService debugService,
        IVideoPreviewService videoPreviewService,
        IStreamSecurityService streamSecurityService,
        ILogger<TranscodeController> logger)
    {
        _transcodeService = transcodeService;
        _streamPlanService = streamPlanService;
        _settingsService = settingsService;
        _context = context;
        _hlsManifestService = hlsManifestService;
        _debugService = debugService;
        _videoPreviewService = videoPreviewService;
        _streamSecurityService = streamSecurityService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current user ID from JWT claims.
    /// </summary>
    private Guid GetUserId()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user ID");
        }
        return userId;
    }

    /// <summary>
    /// Compute the optimal stream plan based on client capabilities.
    /// Returns a StreamPlan with the playback method (DirectPlay, Remux, Transcode) and URL.
    /// </summary>
    [HttpPost("{id}/plan")]
    public async Task<ActionResult<StreamPlan>> GetStreamPlan(Guid id, [FromBody] ClientCapabilities capabilities)
    {
        try
        {
            var userId = GetUserId();

            // Fetch media item with library for validation
            var mediaItem = await _context.MediaItems
                .Include(m => m.Library)
                .FirstOrDefaultAsync(m => m.Id == id);

            if (mediaItem?.Library == null)
            {
                _logger.LogWarning("Media item {Id} not found for stream plan", id);
                return NotFound("Media item not found");
            }

            // Centralized Security Check (Exists + LFI)
            var accessResult = _streamSecurityService.ValidateMediaAccess(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound)
            {
                _logger.LogWarning("Stream plan requested for missing file: {Path}", mediaItem.Path);
                return NotFound("File not found on disk.");
            }
            if (accessResult == MediaAccessResult.Unauthorized)
            {
                _logger.LogWarning("LFI attempt blocked in stream plan: {Path}", mediaItem.Path);
                return Forbid();
            }

            // Get token from query or authorization header
            var token = Request.GetToken();

            // Compute optimal stream plan
            var plan = await _streamPlanService.ComputeStreamPlanAsync(id, mediaItem, capabilities, token);

            _logger.LogInformation(
                "Stream plan for {Id}: Method={Method}, Profile={Profile}, Reason={Reason}",
                id, plan.Method, plan.DisplayProfile, plan.Reason);

            return Ok(plan);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing stream plan for {Id}", id);
            return StatusCode(500, "Failed to compute stream plan");
        }
    }

    /// <summary>
    /// Get the HLS master playlist for a media item.
    /// Optional query parameters:
    /// - token: JWT for authentication
    /// - sub: Subtitle track index to burn into the video
    /// - seek: Position in seconds to start from
    /// - resolution: Target resolution (e.g., "720p", "1080p", "4k", "original")
    /// - codec: Target video codec (e.g., "h264", "hevc", "av1")
    /// - hdr: Whether to preserve HDR (e.g., "true", "false")
    /// - audio: Audio track index to select
    /// - bitrate: Max bitrate in kbps (optional)
    /// </summary>
    [HttpGet("{id}/master.m3u8")]
    public async Task<IActionResult> GetMasterPlaylist(Guid id, [FromQuery] int? sub = null, [FromQuery] double? seek = null, [FromQuery] string? resolution = null, [FromQuery] string? codec = null, [FromQuery] bool? hdr = null, [FromQuery] int? audio = null, [FromQuery] int? bitrate = null, [FromQuery] bool? burnSubtitles = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        try
        {
            var userId = GetUserId();
            
            // Fetch media item with library for path validation
            var mediaItem = await _context.MediaItems
                .Include(m => m.Library)
                .FirstOrDefaultAsync(m => m.Id == id);

            if (mediaItem?.Library == null)
            {
                _logger.LogWarning("Media item {Id} not found or has no library", id);
                return NotFound("Media item not found");
            }

            // Centralized Security Check (Exists + LFI)
            var accessResult = _streamSecurityService.ValidateMediaAccess(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound)
            {
                _logger.LogWarning("Transcode requested for missing file: {Path}", mediaItem.Path);
                return NotFound("File not found on disk.");
            }
            if (accessResult == MediaAccessResult.Unauthorized)
            {
                _logger.LogWarning("LFI attempt blocked in transcode: {Path}", mediaItem.Path);
                return Forbid();
            }

            // Start transcoding with user ID for session ownership
            _logger.LogInformation("Starting transcode for media {Id} (user={UserId}, sub={Sub}, seek={Seek}, resolution={Res}, codec={Codec}, hdr={HDR}, audio={Audio}, bitrate={Bitrate}, burnSubtitles={BurnSubtitles})", 
                id, userId, sub, seek, resolution, codec, hdr, audio, bitrate, burnSubtitles);
            await _transcodeService.StartTranscodeAsync(id, userId, mediaItem.Path, sub, seek, resolution, codec: codec, preserveHdr: hdr, audioTrack: audio, maxBitrate: bitrate, burnSubtitles: burnSubtitles);

            var stream = _transcodeService.GetPlaylist(id, userId, sub);
            if (stream == null)
            {
                _logger.LogWarning("Playlist not ready for {Id} - transcoding may still be starting", id);
                return StatusCode(503, "Transcoding in progress, playlist not ready yet. Please retry in a few seconds.");
            }

            // Read and rewrite M3U8 to inject token AND subtitle track into segment URLs
            var token = Request.GetToken();
            
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("No token provided for transcode request {Id}", id);
                return File(stream, "application/vnd.apple.mpegurl");
            }
            
            // Rewrite path: We consume the stream, so we must dispose it
            using (stream)
            {
                var session = _transcodeService.GetSession(id, userId, sub);
                var subtitleVttPath = session?.SubtitleVttPath;
                
                var bytes = await _hlsManifestService.GenerateMasterPlaylistAsync(stream, token, id.ToString(), sub, subtitleVttPath);
                return File(bytes, "application/vnd.apple.mpegurl");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetMasterPlaylist for {Id}: {Message}", id, ex.Message);
            return StatusCode(500, $"Transcoding error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get an HLS segment. Updates client position for throttling.
    /// Supports both MPEG-TS (.ts) and fMP4 (.m4s) segments.
    /// </summary>
    [HttpGet("{id}/{segment}")]
    public IActionResult GetSegment(Guid id, string segment, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        try
        {
            var userId = GetUserId();
            
            // Extract segment index for throttling
            var segmentIndex = TranscodeService.ExtractSegmentIndex(segment);
            if (segmentIndex >= 0)
            {
                var sessionKey = new TranscodeSessionKey(id, userId, sub);
                _transcodeService.UpdateClientPosition(sessionKey, segmentIndex);
            }

            var stream = _transcodeService.GetSegment(id, userId, segment, sub);
            if (stream == null) return NotFound();

            // Return correct MIME type based on segment extension
            var mimeType = segment.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) 
                ? "video/mp4" 
                : "video/MP2T";
            return File(stream, mimeType);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }

    }

    /// <summary>
    /// Get the fMP4 initialization segment (init.mp4).
    /// Required for fMP4/CMAF HLS playback with HEVC/AV1/HDR content.
    /// </summary>
    [HttpGet("{id}/init.mp4")]
    public IActionResult GetInitSegment(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        try
        {
            var userId = GetUserId();
            var stream = _transcodeService.GetInitSegment(id, userId, sub);
            if (stream == null) 
            {
                _logger.LogWarning("Init segment not found for {Id}", id);
                return NotFound("Initialization segment not available");
            }
            return File(stream, "video/mp4");
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving init segment for {Id}", id);
            return StatusCode(500, "Error reading initialization segment");
        }
    }

    /// <summary>
    /// Get the WebVTT subtitle file for HLS sidecar delivery.
    /// HLS.js will request this file based on the manifest #EXT-X-MEDIA reference.
    /// </summary>
    [HttpGet("{id}/subtitles.vtt")]
    public IActionResult GetSubtitlesVtt(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        try
        {
            var userId = GetUserId();
            var session = _transcodeService.GetSession(id, userId, sub);
            
            if (session?.SubtitleVttPath == null || !System.IO.File.Exists(session.SubtitleVttPath))
            {
                _logger.LogWarning("Subtitle file not found for {Id}", id);
                return NotFound("Subtitle file not available");
            }
            
            var stream = new FileStream(session.SubtitleVttPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, "text/vtt");
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving subtitles for {Id}", id);
            return StatusCode(500, "Error reading subtitle file");
        }
    }

    /// <summary>
    /// Pause transcoding. FFmpeg will stop when buffer is full.
    /// </summary>
    [HttpPost("{id}/pause")]
    public IActionResult Pause(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        try
        {
            var userId = GetUserId();
            var sessionKey = new TranscodeSessionKey(id, userId, sub);
            
            if (!_transcodeService.SetPaused(sessionKey, userId, isPaused: true))
            {
                var session = _transcodeService.GetSession(sessionKey);
                if (session == null)
                {
                    return NotFound("Session not found");
                }
                if (session.UserId != userId)
                {
                    return Forbid();
                }
            }
            
            _logger.LogInformation("Pause requested for {MediaId} by user {UserId}", id, userId);
            return Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>
    /// Resume transcoding after pause.
    /// </summary>
    [HttpPost("{id}/resume")]
    public IActionResult Resume(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        try
        {
            var userId = GetUserId();
            var sessionKey = new TranscodeSessionKey(id, userId, sub);
            
            if (!_transcodeService.SetPaused(sessionKey, userId, isPaused: false))
            {
                var session = _transcodeService.GetSession(sessionKey);
                if (session == null)
                {
                    return NotFound("Session not found");
                }
                if (session.UserId != userId)
                {
                    return Forbid();
                }
            }
            
            _logger.LogInformation("Resume requested for {MediaId} by user {UserId}", id, userId);
            return Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>
    /// Stop transcoding for a media item and clean up files.
    /// Called when video playback ends.
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult StopTranscode(Guid id, [FromQuery] int? sub = null, [FromQuery] bool all = false)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;

        try
        {
            var userId = GetUserId();
            
            if (all)
            {
                // Stop all transcode sessions for this media and user
                _transcodeService.StopAllTranscodesForUser(id, userId);
                _logger.LogInformation("All transcodes stopped for {MediaId} by user {UserId}", id, userId);
            }
            else
            {
                // Stop specific session (already user-scoped by session key)
                _transcodeService.StopTranscode(id, userId, sub);
                _logger.LogInformation("Transcode stopped for {MediaId} (sub={Sub}) by user {UserId}", id, sub, userId);
            }
            
            return Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>
    /// Stop transcoding via POST (for sendBeacon during page unload).
    /// Same as DELETE but accepts POST since navigator.sendBeacon only sends POST.
    /// </summary>
    [HttpPost("{id}/stop")]
    public IActionResult StopTranscodePost(Guid id, [FromQuery] int? sub = null, [FromQuery] bool all = false)
    {
        return StopTranscode(id, sub, all);
    }

    /// <summary>
    /// Get playback debug information for a transcode session.
    /// Returns the full decision pipeline: client capabilities → server settings → decision → actual output.
    /// Requires authentication to protect server configuration details.
    /// </summary>
    [HttpPost("{id}/debug")]
    public async Task<IActionResult> GetPlaybackDebug(Guid id, [FromBody] ClientCapabilities? clientCaps, [FromQuery] int? sub = null)
    {
        try
        {
            var userId = GetUserId();
            var isAdmin = User.IsInRole("Admin");
            var result = await _debugService.GetDebugInfoAsync(id, userId, clientCaps, sub, isAdmin);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting debug info for {Id}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get a single frame preview at a specific timestamp.
    /// Used for scrubber thumbnail preview while dragging.
    /// </summary>
    [HttpGet("{id}/frame")]
    public async Task<IActionResult> GetFramePreview(Guid id, [FromQuery] double time, [FromQuery] string? token = null)
    {
        try
        {
            // Permission check logic...
            if (!string.IsNullOrEmpty(token))
            {
                var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                if (tokenHandler.CanReadToken(token))
                {
                    var jwtToken = tokenHandler.ReadJwtToken(token);
                    var userIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub");
                    if (userIdClaim == null) return Unauthorized();
                }
            }
            else
            {
                GetUserId(); // Will throw if not authenticated
            }
            
            var (data, contentType) = await _videoPreviewService.GetPreviewImageAsync(id, time);
            
            if (data == null || data.Length == 0)
            {
                return NotFound("Could not extract frame");
            }
            
            return File(data, contentType);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting frame preview for {Id} at {Time}", id, time);
            return StatusCode(500, ex.Message);
        }
    }
}
