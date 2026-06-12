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
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;

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
    private readonly IMediaRepository _mediaRepository;
    private readonly ILogger<TranscodeController> _logger;
    private readonly ITranscodeDebugService _debugService;
    private readonly IVideoPreviewService _videoPreviewService;
    private readonly IStreamSecurityService _streamSecurityService;
    private readonly AppDbContext _dbContext;
    private readonly ITokenService _tokenService;

    // New Services
    private readonly ITranscodeSessionService _sessionService;
    private readonly IStreamResultService _streamResultService;

    public TranscodeController(
        ITranscodeService transcodeService,
        IStreamPlanService streamPlanService,
        IMediaRepository mediaRepository,
        ITranscodeDebugService debugService,
        IVideoPreviewService videoPreviewService,
        IStreamSecurityService streamSecurityService,
        ITranscodeSessionService sessionService,
        IStreamResultService streamResultService,
        AppDbContext dbContext,
        ITokenService tokenService,
        ILogger<TranscodeController> logger)
    {
        _transcodeService = transcodeService;
        _streamPlanService = streamPlanService;
        _mediaRepository = mediaRepository;
        _debugService = debugService;
        _videoPreviewService = videoPreviewService;
        _streamSecurityService = streamSecurityService;
        _sessionService = sessionService;
        _streamResultService = streamResultService;
        _dbContext = dbContext;
        _tokenService = tokenService;
        _logger = logger;
    }

    /// Resolves the per-user streaming bitrate override (P1-WI-003), if any.
    private async Task<int?> GetUserMaxBitrateAsync(Guid userId)
        => await _dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => u.MaxStreamBitrateKbps)
            .FirstOrDefaultAsync();

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
    public async Task<ActionResult<StreamPlan>> GetStreamPlan(Guid id, [FromBody] ClientCapabilities capabilities, [FromQuery] bool cast = false)
    {
        try
        {
            var userId = GetUserId();

            // Only a full session may mint a cast token. A cast token is itself in-scope for
            // this endpoint (/api/transcode/{id}/...), so without this early guard it could call
            // ?cast=true to self-renew indefinitely. Reject before doing any work.
            if (cast && User.FindFirst(CastTokenClaims.TokenUse)?.Value == CastTokenClaims.CastUse)
                return StatusCode(StatusCodes.Status403Forbidden, "A cast token cannot mint another cast token.");

            var mediaItem = await _mediaRepository.GetByIdWithLibraryAsync(id);

            if (mediaItem?.Library == null) return NotFound("Media item not found");

            // Centralized Security Check (Wave C — also covers per-user library ACL)
            var accessResult = await _streamSecurityService.ValidateMediaAccessAsync(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound) return NotFound("File not found on disk.");
            if (accessResult == MediaAccessResult.Unauthorized) return NotFound();

            var token = Request.GetToken();
            if (cast)
            {
                // Casting: the Chromecast fetches the stream itself and can't refresh the
                // short-lived session JWT, so embed a long-lived token in the plan URL. It is
                // scoped to THIS media's stream routes only (CC-WI-003) — see CastTokenClaims.
                var user = await _dbContext.Users.FindAsync(userId);
                if (user == null) return Unauthorized();
                token = _tokenService.GenerateCastToken(user, id);
            }

            var userMaxBitrate = await GetUserMaxBitrateAsync(userId);
            var plan = await _streamPlanService.ComputeStreamPlanAsync(
                id, mediaItem, capabilities, token ?? string.Empty,
                HttpContext.Connection.RemoteIpAddress, userMaxBitrate);

            _logger.LogInformation("Stream plan for {Id}: Method={Method}, Profile={Profile}, Reason={Reason}{Cast}",
                id, plan.Method, plan.DisplayProfile, plan.Reason, cast ? " [cast]" : "");

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
    public async Task<IActionResult> GetMasterPlaylist(Guid id, [FromQuery] int? sub = null, [FromQuery] double? seek = null, [FromQuery] string? resolution = null, [FromQuery] string? codec = null, [FromQuery] bool? hdr = null, [FromQuery] int? audio = null, [FromQuery] int? bitrate = null, [FromQuery] bool? burnSubtitles = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        // Security (audit wave-2 M-4): reject a malformed session id at the boundary with a clean
        // 400 (the service also re-validates before it touches the filesystem).
        if (!TranscodeSid.IsValid(sid)) return BadRequest("Invalid session id.");
        try
        {
            var userId = GetUserId();
            var mediaItem = await _mediaRepository.GetByIdWithLibraryAsync(id);
            
            if (mediaItem?.Library == null) return NotFound("Media item not found");

            var accessResult = await _streamSecurityService.ValidateMediaAccessAsync(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound) return NotFound("File not found on disk.");
            if (accessResult == MediaAccessResult.Unauthorized) return NotFound();

            _logger.LogInformation("Starting transcode for media {Id} (user={UserId})", id, userId);

            await _transcodeService.StartTranscodeAsync(id, userId, mediaItem.Path, sub, seek, resolution, codec: codec, preserveHdr: hdr, audioTrack: audio, maxBitrate: bitrate, burnSubtitles: burnSubtitles, sid: sid);

            var token = Request.GetToken();
            return await _streamResultService.GenerateMasterPlaylistResultAsync(id, userId, sub, token, sid);
        }
        catch (TranscodeCapacityException ex)
        {
            // Concurrency cap reached — tell the client to back off and retry.
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status429TooManyRequests, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetMasterPlaylist for {Id}", id);
            return StatusCode(500, "Transcoding failed. See server logs for details.");
        }
    }

    [HttpGet("{id}/{segment}")]
    public IActionResult GetSegment(Guid id, string segment, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            // Throttling Logic delegated to service
            _sessionService.UpdateClientPosition(id, userId, sub, segment, sid);
            return _streamResultService.GetSegmentResult(id, userId, sub, segment, sid);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpGet("{id}/init.mp4")]
    public IActionResult GetInitSegment(Guid id, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            return _streamResultService.GetInitSegmentResult(id, userId, sub, sid);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving init segment for {Id}", id);
            return StatusCode(500, "Error reading initialization segment");
        }
    }

    [HttpGet("{id}/subtitles.vtt")]
    public IActionResult GetSubtitlesVtt(Guid id, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            return _streamResultService.GetSubtitleResult(id, userId, sub, sid);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving subtitles for {Id}", id);
            return StatusCode(500, "Error reading subtitle file");
        }
    }

    [HttpPost("{id}/pause")]
    public IActionResult Pause(Guid id, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var result = _sessionService.PauseSession(id, GetUserId(), sub, sid);
            return ResultToActionResult(result);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpPost("{id}/resume")]
    public IActionResult Resume(Guid id, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var result = _sessionService.ResumeSession(id, GetUserId(), sub, sid);
            return ResultToActionResult(result);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> StopTranscode(Guid id, [FromQuery] int? sub = null, [FromQuery] bool all = false, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            if (all) await _sessionService.StopAllSessions(id, userId);
            else await _sessionService.StopSession(id, userId, sub, sid);
            return Ok();
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpPost("{id}/stop")]
    public Task<IActionResult> StopTranscodePost(Guid id, [FromQuery] int? sub = null, [FromQuery] bool all = false, [FromQuery] string? sid = null)
    {
        return StopTranscode(id, sub, all, sid);
    }

    [HttpPost("{id}/debug")]
    public async Task<IActionResult> GetPlaybackDebug(Guid id, [FromBody] ClientCapabilities? clientCaps, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
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
            return StatusCode(500, new { error = "Failed to compute debug info. See server logs for details." });
        }
    }

    [HttpGet("{id}/frame")]
    public async Task<IActionResult> GetFramePreview(Guid id, [FromQuery] double time)
    {
        // Auth: class-level [Authorize] + JwtBearerEvents.OnMessageReceived (which lifts
        // ?token= for /api/transcode/*) means the standard middleware has already
        // validated the JWT signature, expiry, issuer, and audience. SDD §4.5 forbids
        // bespoke JwtSecurityTokenHandler.ReadJwtToken checks here — they only decode.
        try
        {
            var (data, contentType) = await _videoPreviewService.GetPreviewImageAsync(id, time);
            if (data == null || data.Length == 0) return NotFound("Could not extract frame");

            return File(data, contentType);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting frame preview for {Id} at {Time}", id, time);
            return StatusCode(500, "Failed to extract frame.");
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
