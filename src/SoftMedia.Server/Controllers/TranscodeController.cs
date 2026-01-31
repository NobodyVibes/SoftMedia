using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Abstractions;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Transcoding.Models;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using System.IdentityModel.Tokens.Jwt;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for HLS transcoding endpoints with throttling support.
/// Refactored to delegate logic to specialized services.
/// </summary>
[Authorize]
[ApiController]
[Route("api/transcode")]
public class TranscodeController : ControllerBase
{
    private readonly ITranscodeService _transcodeService;
    private readonly IStreamPlanService _streamPlanService;
    private readonly AppDbContext _context;
    private readonly ILogger<TranscodeController> _logger;
    private readonly ITranscodeDebugService _debugService;
    private readonly IVideoPreviewService _videoPreviewService;
    private readonly IStreamSecurityService _streamSecurityService;
    
    // New Services
    private readonly ITranscodeSessionService _sessionService;
    private readonly IStreamResultService _streamResultService;

    public TranscodeController(
        ITranscodeService transcodeService, 
        IStreamPlanService streamPlanService,
        AppDbContext context,
        ITranscodeDebugService debugService,
        IVideoPreviewService videoPreviewService,
        IStreamSecurityService streamSecurityService,
        ITranscodeSessionService sessionService,
        IStreamResultService streamResultService,
        ILogger<TranscodeController> logger)
    {
        _transcodeService = transcodeService;
        _streamPlanService = streamPlanService;
        _context = context;
        _debugService = debugService;
        _videoPreviewService = videoPreviewService;
        _streamSecurityService = streamSecurityService;
        _sessionService = sessionService;
        _streamResultService = streamResultService;
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

    [HttpPost("{id}/plan")]
    public async Task<ActionResult<StreamPlan>> GetStreamPlan(Guid id, [FromBody] ClientCapabilities capabilities)
    {
        try
        {
            var userId = GetUserId();

            var mediaItem = await _context.MediaItems
                .Include(m => m.Library)
                .FirstOrDefaultAsync(m => m.Id == id);

            if (mediaItem?.Library == null) return NotFound("Media item not found");

            // Centralized Security Check
            var accessResult = _streamSecurityService.ValidateMediaAccess(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound) return NotFound("File not found on disk.");
            if (accessResult == MediaAccessResult.Unauthorized) return Forbid();

            var token = Request.GetToken();
            var plan = await _streamPlanService.ComputeStreamPlanAsync(id, mediaItem, capabilities, token);

            _logger.LogInformation("Stream plan for {Id}: Method={Method}, Profile={Profile}, Reason={Reason}",
                id, plan.Method, plan.DisplayProfile, plan.Reason);

            return Ok(plan);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing stream plan for {Id}", id);
            return StatusCode(500, "Failed to compute stream plan");
        }
    }

    [HttpGet("{id}/master.m3u8")]
    public async Task<IActionResult> GetMasterPlaylist(Guid id, [FromQuery] int? sub = null, [FromQuery] double? seek = null, [FromQuery] string? resolution = null, [FromQuery] string? codec = null, [FromQuery] bool? hdr = null, [FromQuery] int? audio = null, [FromQuery] int? bitrate = null, [FromQuery] bool? burnSubtitles = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            var mediaItem = await _context.MediaItems.Include(m => m.Library).FirstOrDefaultAsync(m => m.Id == id);
            
            if (mediaItem?.Library == null) return NotFound("Media item not found");

            var accessResult = _streamSecurityService.ValidateMediaAccess(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound) return NotFound("File not found on disk.");
            if (accessResult == MediaAccessResult.Unauthorized) return Forbid();

            _logger.LogInformation("Starting transcode for media {Id} (user={UserId})", id, userId);
            
            await _transcodeService.StartTranscodeAsync(id, userId, mediaItem.Path, sub, seek, resolution, codec: codec, preserveHdr: hdr, audioTrack: audio, maxBitrate: bitrate, burnSubtitles: burnSubtitles);

            var token = Request.GetToken();
            return await _streamResultService.GenerateMasterPlaylistResultAsync(id, userId, sub, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetMasterPlaylist for {Id}", id);
            return StatusCode(500, $"Transcoding error: {ex.Message}");
        }
    }

    [HttpGet("{id}/{segment}")]
    public IActionResult GetSegment(Guid id, string segment, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            // Throttling Logic delegated to service
            _sessionService.UpdateClientPosition(id, userId, sub, segment);
            return _streamResultService.GetSegmentResult(id, userId, sub, segment);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpGet("{id}/init.mp4")]
    public IActionResult GetInitSegment(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            return _streamResultService.GetInitSegmentResult(id, userId, sub);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving init segment for {Id}", id);
            return StatusCode(500, "Error reading initialization segment");
        }
    }

    [HttpGet("{id}/subtitles.vtt")]
    public IActionResult GetSubtitlesVtt(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            return _streamResultService.GetSubtitleResult(id, userId, sub);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving subtitles for {Id}", id);
            return StatusCode(500, "Error reading subtitle file");
        }
    }

    [HttpPost("{id}/pause")]
    public IActionResult Pause(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var result = _sessionService.PauseSession(id, GetUserId(), sub);
            return ResultToActionResult(result);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpPost("{id}/resume")]
    public IActionResult Resume(Guid id, [FromQuery] int? sub = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var result = _sessionService.ResumeSession(id, GetUserId(), sub);
            return ResultToActionResult(result);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpDelete("{id}")]
    public IActionResult StopTranscode(Guid id, [FromQuery] int? sub = null, [FromQuery] bool all = false)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            if (all) _sessionService.StopAllSessions(id, userId);
            else _sessionService.StopSession(id, userId, sub);
            return Ok();
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpPost("{id}/stop")]
    public IActionResult StopTranscodePost(Guid id, [FromQuery] int? sub = null, [FromQuery] bool all = false)
    {
        return StopTranscode(id, sub, all);
    }

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

    [HttpGet("{id}/frame")]
    public async Task<IActionResult> GetFramePreview(Guid id, [FromQuery] double time, [FromQuery] string? token = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(token))
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                if (tokenHandler.CanReadToken(token))
                {
                    var jwtToken = tokenHandler.ReadJwtToken(token);
                    var userIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub");
                    if (userIdClaim == null) return Unauthorized();
                }
            }
            else
            {
                GetUserId(); 
            }
            
            var (data, contentType) = await _videoPreviewService.GetPreviewImageAsync(id, time);
            if (data == null || data.Length == 0) return NotFound("Could not extract frame");
            
            return File(data, contentType);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting frame preview for {Id} at {Time}", id, time);
            return StatusCode(500, ex.Message);
        }
    }

    private IActionResult ResultToActionResult(TranscodeSessionResult result)
    {
        return result switch
        {
            TranscodeSessionResult.Success => Ok(),
            TranscodeSessionResult.NotFound => NotFound("Session not found"),
            TranscodeSessionResult.Unauthorized => Forbid(),
            _ => StatusCode(500)
        };
    }
}
