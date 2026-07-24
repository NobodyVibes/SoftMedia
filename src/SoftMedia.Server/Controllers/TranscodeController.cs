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
[Authorize(Policy = ScopePolicies.ReadLibrary)] // B-18: content = read:library for tokens
[ApiController]
[Route("api/v1/transcode")] // NR-WI-004: canonical, consistent with every other controller
[Route("api/transcode")]    // legacy alias — deprecated, kept for minted URLs and older clients
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
    private readonly IStreamPlanStore _planStore;
    private readonly ISettingsService _settingsService;
    private readonly Services.Sessions.ITerminatedSessionRegistry _terminatedSessions;

    /// <summary>
    /// 410 Gone: an admin stopped this session and it must not be resurrected by the
    /// client's automatic recovery. The player treats this code as terminal (stops and
    /// reports it) rather than retrying, which is what kept the old kill from sticking.
    /// </summary>
    private IActionResult? TerminatedResult(Guid mediaId, Guid userId, string? sid)
    {
        if (!_terminatedSessions.IsTerminated(mediaId, userId, sid)) return null;
        return StatusCode(StatusCodes.Status410Gone, "Playback was stopped by an administrator.");
    }

    public TranscodeController(
        ITranscodeService transcodeService,
        IStreamPlanService streamPlanService,
        IMediaRepository mediaRepository,
        ITranscodeDebugService debugService,
        IVideoPreviewService videoPreviewService,
        IStreamSecurityService streamSecurityService,
        ITranscodeSessionService sessionService,
        IStreamResultService streamResultService,
        IStreamPlanStore planStore,
        AppDbContext dbContext,
        ITokenService tokenService,
        ISettingsService settingsService,
        Services.Sessions.ITerminatedSessionRegistry terminatedSessions,
        ILogger<TranscodeController> logger)
    {
        _terminatedSessions = terminatedSessions;
        _transcodeService = transcodeService;
        _streamPlanService = streamPlanService;
        _mediaRepository = mediaRepository;
        _debugService = debugService;
        _videoPreviewService = videoPreviewService;
        _streamSecurityService = streamSecurityService;
        _sessionService = sessionService;
        _streamResultService = streamResultService;
        _planStore = planStore;
        _settingsService = settingsService;
        _dbContext = dbContext;
        _tokenService = tokenService;
        _logger = logger;
    }

    /// B-02 — quality-label ordering for the server-wide resolution clamp. Null or
    /// "original" ranks highest (they mean source quality, which must also clamp).
    public static int ResolutionRank(string? quality) => quality?.ToLowerInvariant() switch
    {
        "480p" => 480,
        "720p" => 720,
        "1080p" => 1080,
        "1440p" => 1440,
        "4k" or "2160p" => 2160,
        "8k" or "4320p" => 4320,
        _ => int.MaxValue, // null / "original" / unknown = uncapped request
    };

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

            // An admin-stopped session must not be re-planned back to life either. Uses the
            // capabilities' StreamId (this endpoint's sid); StatusCode is inlined because the
            // helper's IActionResult doesn't fit this action's ActionResult<StreamPlan>.
            if (_terminatedSessions.IsTerminated(id, userId, capabilities.StreamId))
                return StatusCode(StatusCodes.Status410Gone, "Playback was stopped by an administrator.");

            var mediaItem = await _mediaRepository.GetByIdWithLibraryAsync(id);

            if (mediaItem?.Library == null) return NotFound("Media item not found");

            // Centralized Security Check (Wave C — also covers per-user library ACL)
            var accessResult = await _streamSecurityService.ValidateMediaAccessAsync(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound) return NotFound("File not found on disk.");
            if (accessResult == MediaAccessResult.Unauthorized) return NotFound();

            // WS-6: the plan URL must NEVER echo the caller's bearer token. The plan POST
            // authenticates with the full ACCESS token in a header — and T6.1 rejects
            // access tokens in query strings, so echoing it would 401 every DirectPlay
            // src fetch (and would put a role-bearing token into a URL besides). Mint the
            // right reduced-privilege token for the URL instead, mirroring the cast path.
            string token;
            var user = await _dbContext.Users.FindAsync(userId);
            if (user == null) return Unauthorized();
            if (cast)
            {
                // Casting: the Chromecast fetches the stream itself and can't refresh the
                // short-lived session JWT, so embed a long-lived token in the plan URL. It is
                // scoped to THIS media's stream routes only (CC-WI-003) — see CastTokenClaims.
                token = _tokenService.GenerateCastToken(user, id);
            }
            else
            {
                token = _tokenService.GenerateMediaToken(user).Token;
            }

            var userMaxBitrate = await GetUserMaxBitrateAsync(userId);
            var plan = await _streamPlanService.ComputeStreamPlanAsync(
                id, mediaItem, capabilities, token ?? string.Empty,
                HttpContext.Connection.RemoteIpAddress, userMaxBitrate);

            // R-WI-002: persist the negotiated quality/security params, keyed by this session's
            // sid, so a later master.m3u8 request (especially a far-seek that rebuilds the URL
            // with only token+sid) is resolved against the authoritative plan rather than the
            // client-controlled query string — closing the far-seek quality loss and the
            // per-user bitrate-cap bypass (D-4). DirectPlay/sid-less requests are stateless.
            if (plan.Method is PlaybackMethod.Transcode or PlaybackMethod.Remux)
            {
                _planStore.Save(id, userId, capabilities.StreamId, new PersistedStreamPlan(
                    plan.Method,
                    plan.TranscodeResolution,
                    plan.TranscodeCodec,
                    plan.TranscodeMaxBitrate,
                    plan.TranscodePreserveHdr,
                    plan.TranscodeAudioCopy,
                    plan.TranscodeAudioCodec,
                    plan.TranscodeAudioChannels));
            }

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
            // THE resurrection path: the client reloads the playlist when its segments start
            // failing, and this handler would otherwise start a brand-new ffmpeg for the
            // session an admin just stopped.
            if (TerminatedResult(id, userId, sid) is { } stopped) return stopped;

            var mediaItem = await _mediaRepository.GetByIdWithLibraryAsync(id);

            if (mediaItem?.Library == null) return NotFound("Media item not found");

            var accessResult = await _streamSecurityService.ValidateMediaAccessAsync(mediaItem);
            if (accessResult == MediaAccessResult.FileNotFound) return NotFound("File not found on disk.");
            if (accessResult == MediaAccessResult.Unauthorized) return NotFound();

            // R-WI-002/R-WI-005: if a plan was negotiated for this session (sid), its quality/
            // security params are authoritative — override any (possibly minimal, e.g. far-seek)
            // query values so the negotiated resolution/codec/HDR and the per-user bitrate cap
            // cannot be dropped or bypassed by a client-crafted URL. User-controlled choices —
            // subtitle/audio track, seek position, burn-in — still come from the request.
            var storedPlan = _planStore.Get(id, userId, sid);
            if (storedPlan != null)
            {
                resolution = storedPlan.Resolution;
                codec = storedPlan.Codec;
                hdr = storedPlan.PreserveHdr;
                bitrate = storedPlan.MaxBitrate;
            }

            // SR-WI-028: the plan path floors every client bitrate at 1000 kbps (SanitizeCapabilities'
            // Math.Clamp) — the null-plan path must too, or a fabricated/expired sid with ?bitrate=1
            // reaches ffmpeg as "-maxrate 1k" (unwatchable output that still burns a transcode slot).
            // Applied BEFORE the per-user cap so an admin policy below 1000 still wins, exactly as
            // on the plan path (user cap is applied after sanitization there).
            if (storedPlan == null && bitrate is > 0 and < 1000)
            {
                _logger.LogWarning("Raising fabricated-sid bitrate {Requested} kbps to the 1000 kbps floor for {MediaId}", bitrate, id);
                bitrate = 1000;
            }

            // Enforce the per-user bitrate cap on EVERY transcode request, independent of whether
            // a plan was persisted. A client can reach master.m3u8 with a never-negotiated sid and
            // a high ?bitrate=, which would otherwise flow to ffmpeg unclamped — the residual D-4
            // hole the plan-store resolver alone did not close (it only covered the happy path).
            // The stored plan's bitrate was already cap-clamped at negotiation, so this is a
            // redundant-but-safe re-clamp there and the sole guard on the null-plan path.
            var userBitrateCap = await GetUserMaxBitrateAsync(userId);
            if (userBitrateCap is > 0 && (bitrate is null or <= 0 || bitrate > userBitrateCap))
            {
                bitrate = userBitrateCap;
            }

            // B-02: the SERVER-WIDE quality settings must hold on the null-plan path
            // too. A fabricated sid with ?resolution=4k&codec=av1 previously flowed
            // straight to ffmpeg even when the admin set MaxTranscodeResolution=720p /
            // OutputVideoCodec=h264 — only the per-user bitrate was re-clamped above.
            // The stored plan already honoured these at negotiation, so like the
            // bitrate clamp this is redundant-but-safe there and the sole guard here.
            if (storedPlan == null)
            {
                var maxResSetting = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");
                if (!string.Equals(maxResSetting, "original", StringComparison.OrdinalIgnoreCase)
                    && ResolutionRank(resolution) > ResolutionRank(maxResSetting))
                {
                    _logger.LogWarning(
                        "Clamping fabricated-sid resolution {Requested} to server max {Max} for {MediaId}",
                        resolution, maxResSetting, id);
                    resolution = maxResSetting;
                }

                var codecSetting = await _settingsService.GetSettingAsync("OutputVideoCodec", "auto");
                if (!string.Equals(codecSetting, "auto", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(codec)
                    && !string.Equals(codec, codecSetting, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Overriding fabricated-sid codec {Requested} with server setting {Setting} for {MediaId}",
                        codec, codecSetting, id);
                    codec = codecSetting;
                }
                // SR-WI-028: with OutputVideoCodec=auto the codec param previously flowed to
                // ffmpeg unvalidated — a client could force an expensive hevc/av1 encode (or feed
                // garbage) via a fabricated sid. Restrict to the supported encode set the plan
                // path negotiates: h264/hevc always, av1 only when the admin enabled it.
                else if (!string.IsNullOrEmpty(codec)
                         && !string.Equals(codec, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    var codecLower = codec.ToLowerInvariant();
                    var av1Enabled = await _settingsService.GetSettingAsync("EnableAV1Encoding", false);
                    var codecAllowed = codecLower is "h264" or "hevc" || (codecLower == "av1" && av1Enabled);
                    if (!codecAllowed)
                    {
                        _logger.LogWarning(
                            "Rejecting fabricated-sid codec {Requested} for {MediaId}; falling back to h264",
                            codec, id);
                        codec = "h264";
                    }
                }
            }

            // R-WI-003: honour the negotiated Remux method (stream-copy) when the plan says so.
            // Only trust the persisted plan for this — a client cannot force a copy of an
            // incompatible source by fiddling the URL (there is no remux query param).
            var remux = storedPlan?.Method == PlaybackMethod.Remux;

            // R-WI-004: the audio decision (copy / codec / channels) comes ONLY from the negotiated
            // plan — there is no audio-codec query param, so a fabricated-sid request with no stored
            // plan safely falls back to the default stereo AAC.
            var audioCopy = storedPlan?.AudioCopy ?? false;
            var audioCodec = storedPlan?.AudioCodec;
            var audioChannels = storedPlan?.AudioChannels ?? 0;

            _logger.LogInformation("Starting {Method} for media {Id} (user={UserId}){Restored}",
                remux ? "remux" : "transcode", id, userId, storedPlan != null ? " [plan restored]" : "");

            await _transcodeService.StartTranscodeAsync(id, userId, mediaItem.Path, sub, seek, resolution, codec: codec, preserveHdr: hdr, audioTrack: audio, maxBitrate: bitrate, burnSubtitles: burnSubtitles, sid: sid, remux: remux, audioCopy: audioCopy, audioCodec: audioCodec, audioChannels: audioChannels);

            // Stamp the playing client on the freshly-started session so the admin dashboard has
            // a device/IP from the first row it renders (segments keep it refreshed thereafter).
            _sessionService.SetClientDevice(id, userId, sub, sid, Request.GetClientDevice());

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
            if (TerminatedResult(id, userId, sid) is { } stopped) return stopped;
            // Throttling Logic delegated to service
            _sessionService.UpdateClientPosition(id, userId, sub, segment, sid);
            // Keep the dashboard's device/IP tracking the client actually pulling segments.
            _sessionService.SetClientDevice(id, userId, sub, sid, Request.GetClientDevice());
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
            if (TerminatedResult(id, userId, sid) is { } stopped) return stopped;
            return _streamResultService.GetInitSegmentResult(id, userId, sub, sid);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving init segment for {Id}", id);
            return StatusCode(500, "Error reading initialization segment");
        }
    }

    /// B-13/B-14 — the master's subtitle rendition points here: a compliant WebVTT
    /// media playlist wrapping the session VTT (native HLS players require a
    /// playlist URI; the raw .vtt broke hls.js parsing and iOS entirely).
    [HttpGet("{id}/subtitles.m3u8")]
    public async Task<IActionResult> GetSubtitlesPlaylist(Guid id, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            var item = await _mediaRepository.GetByIdAsync(id);
            Response.Headers.CacheControl = "no-store"; // same staleness rules as the VTT itself
            return _streamResultService.GetSubtitlePlaylistResult(id, userId, sub, sid, Request.GetToken(), item?.Duration ?? 0);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving subtitle playlist for {Id}", id);
            return StatusCode(500, "Error building subtitle playlist");
        }
    }

    [HttpGet("{id}/subtitles.vtt")]
    public IActionResult GetSubtitlesVtt(Guid id, [FromQuery] int? sub = null, [FromQuery] string? sid = null)
    {
        if (sub.HasValue && sub.Value < 0) sub = null;
        try
        {
            var userId = GetUserId();
            // R-WI-018 review: the VTT URL is IDENTICAL across far-seek restarts of a
            // playback while its CONTENT changes with every seek offset — any cache
            // (browser heuristic, proxy, service worker) serving a stale copy desyncs
            // subtitles by the whole seek. Never cache it.
            Response.Headers.CacheControl = "no-store";
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
            // SR-WI-024: sid must reach the debug service — sessions are keyed with it, so
            // dropping it here made every sid-keyed lookup miss ("likely direct play").
            if (!TranscodeSid.IsValid(sid)) return BadRequest("Invalid session id.");
            var userId = GetUserId();
            var isAdmin = User.IsInRole("Admin");
            var result = await _debugService.GetDebugInfoAsync(id, userId, clientCaps, sub, isAdmin, sid);
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
