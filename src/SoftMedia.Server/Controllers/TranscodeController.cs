using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Services;

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
    private readonly IBinaryLocationService _binaryLocationService;

    public TranscodeController(
        TranscodeService transcodeService, 
        IStreamPlanService streamPlanService,
        ISettingsService settingsService,
        AppDbContext context,
        IHlsManifestService hlsManifestService,
        IBinaryLocationService binaryLocationService,
        ILogger<TranscodeController> logger)
    {
        _transcodeService = transcodeService;
        _streamPlanService = streamPlanService;
        _settingsService = settingsService;
        _context = context;
        _hlsManifestService = hlsManifestService;
        _binaryLocationService = binaryLocationService;
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

            // Security: Verify file exists
            if (!System.IO.File.Exists(mediaItem.Path))
            {
                _logger.LogWarning("Stream plan requested for missing file: {Path}", mediaItem.Path);
                return NotFound("File not found on disk.");
            }

            // Security: LFI Protection - verify path is within authorized library directories
            var canonicalPath = Path.GetFullPath(mediaItem.Path);
            var isAuthorized = mediaItem.Library.Paths.Any(p =>
                canonicalPath.StartsWith(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));

            if (!isAuthorized)
            {
                _logger.LogWarning("LFI attempt blocked in stream plan: {Path}", mediaItem.Path);
                return Forbid();
            }

            // Get token from query or authorization header
            var token = Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(token))
            {
                var authHeader = Request.Headers["Authorization"].ToString();
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = authHeader.Substring(7);
                }
            }

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
    public async Task<IActionResult> GetMasterPlaylist(Guid id, [FromQuery] int? sub = null, [FromQuery] double? seek = null, [FromQuery] string? resolution = null, [FromQuery] string? codec = null, [FromQuery] bool? hdr = null, [FromQuery] int? audio = null, [FromQuery] int? bitrate = null)
    {

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

            // Security: Verify file exists
            if (!System.IO.File.Exists(mediaItem.Path))
            {
                _logger.LogWarning("Transcode requested for missing file: {Path}", mediaItem.Path);
                return NotFound("File not found on disk.");
            }

            // Security: LFI Protection - verify path is within authorized library directories
            var canonicalPath = Path.GetFullPath(mediaItem.Path);
            var isAuthorized = mediaItem.Library.Paths.Any(p =>
                canonicalPath.StartsWith(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));

            if (!isAuthorized)
            {
                _logger.LogWarning("LFI attempt blocked in transcode: {Path}", mediaItem.Path);
                return Forbid();
            }

            // Start transcoding with user ID for session ownership
            _logger.LogInformation("Starting transcode for media {Id} (user={UserId}, sub={Sub}, seek={Seek}, resolution={Res}, codec={Codec}, hdr={HDR}, audio={Audio}, bitrate={Bitrate})", 
                id, userId, sub, seek, resolution, codec, hdr, audio, bitrate);
            await _transcodeService.StartTranscodeAsync(id, userId, mediaItem.Path, sub, seek, resolution, codec: codec, preserveHdr: hdr, audioTrack: audio, maxBitrate: bitrate);

            var stream = _transcodeService.GetPlaylist(id, userId, sub);
            if (stream == null)
            {
                _logger.LogWarning("Playlist not ready for {Id} - transcoding may still be starting", id);
                return StatusCode(503, "Transcoding in progress, playlist not ready yet. Please retry in a few seconds.");
            }

            // Read and rewrite M3U8 to inject token AND subtitle track into segment URLs
            var token = Request.Query["token"].ToString();
            
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
            var session = _transcodeService.GetSession(id, userId, sub);
            
            // Fetch individual server settings
            var outputVideoCodec = await _settingsService.GetSettingAsync("OutputVideoCodec", "auto");
            var maxResolution = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");
            var preserveHdr = await _settingsService.GetSettingAsync("PreserveHDR", "true") == "true";
            var enableAv1 = await _settingsService.GetSettingAsync("EnableAV1Encoding", "false") == "true";
            var hwAccel = await _settingsService.GetSettingAsync("HardwareAcceleration", "none");
            var preset = await _settingsService.GetSettingAsync("TranscodePreset", "veryfast");
            var crf = await _settingsService.GetSettingAsync("TranscodeCRF", "23");
            var audioChannels = await _settingsService.GetSettingAsync("DefaultAudioChannels", "auto");
            
            // Get source media info
            using var scope = HttpContext.RequestServices.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mediaItem = await dbContext.MediaItems.FindAsync(id);
            
            if (session == null)
            {
                return Ok(new
                {
                    playbackMode = "DirectPlay",
                    isTranscoding = false,
                    message = "No active transcode session - likely direct play",
                    clientCapabilities = clientCaps != null ? new
                    {
                        videoCodecs = clientCaps.VideoCodecs,
                        audioCodecs = clientCaps.AudioCodecs,
                        supportsHdr = clientCaps.SupportsHdr,
                        maxAudioChannels = clientCaps.MaxAudioChannels,
                        requestedQuality = clientCaps.RequestedQuality,
                        supportedSubtitleFormats = clientCaps.SupportedSubtitleFormats
                    } : null,
                    serverSettings = new
                    {
                        outputVideoCodec,
                        maxResolution,
                        preserveHdr,
                        enableAv1,
                        hardwareAcceleration = hwAccel,
                        targetAudioChannels = audioChannels
                    },
                    selectedSubtitleTrack = sub
                });
            }
            
            
            bool isAdmin = User.IsInRole("Admin");
            
            // Get probe info from transcoded output
            var probeInfo = await ProbeTranscodedOutput(session, isAdmin);
            
            // Build comprehensive debug response
            var debugResponse = new
            {
                playbackMode = "Transcode",
                isTranscoding = true,
                
                // 1. Client Capabilities - what the browser/client sent
                clientCapabilities = clientCaps != null ? new
                {
                    videoCodecs = clientCaps.VideoCodecs,
                    audioCodecs = clientCaps.AudioCodecs,
                    supportsHdr = clientCaps.SupportsHdr,
                    maxAudioChannels = clientCaps.MaxAudioChannels,
                    maxResolution = clientCaps.MaxResolution,
                    maxBitrate = clientCaps.MaxBitrate,
                    requestedQuality = clientCaps.RequestedQuality,
                    supportedContainers = clientCaps.SupportedContainers,
                    supportedSubtitleFormats = clientCaps.SupportedSubtitleFormats
                } : null,
                
                // 2. Server Settings - admin-configured transcode settings
                serverSettings = new
                {
                    outputVideoCodec,
                    maxResolution,
                    preserveHdr,
                    enableAv1,
                    hardwareAcceleration = hwAccel,
                    preset,
                    crf,
                    targetAudioChannels = audioChannels
                },
                
                // 3. Source Media Info - what was detected from the source file
                sourceMedia = mediaItem != null ? new
                {
                    videoCodec = mediaItem.VideoCodec,
                    audioCodec = mediaItem.AudioCodec,
                    resolution = mediaItem.Resolution,
                    container = mediaItem.Container,
                    duration = mediaItem.Duration
                } : null,
                
                // 4. Final Decision - what the backend ultimately decided to do
                decision = new
                {
                    targetCodec = session.TargetCodec ?? "h264",
                    targetResolution = session.TargetResolution ?? "original",
                    preserveHdr = session.PreserveHdr,
                    toneMapped = !session.PreserveHdr,
                    subtitleBurnIn = session.IsBitmapSubtitle,
                    subtitleTrack = session.Key.SubtitleTrackIndex,
                    subtitleLanguage = session.SubtitleLanguage
                },
                
                // 5. Actual Output - FFprobe data from the transcoded file
                probe = probeInfo,
                
                // Metadata
                sessionDirectory = isAdmin ? session.SessionDirectory : "<redacted>",
                probedAt = DateTime.UtcNow
            };
            
            return Ok(debugResponse);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting debug info for {Id}", id);
            return StatusCode(500, $"Error getting debug info: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Probe the transcoded output file to get actual codec/HDR info
    /// </summary>
    private async Task<object?> ProbeTranscodedOutput(TranscodeSession session, bool includeSensitiveData)
    {
        try
        {
            // Try init.mp4 first (fMP4 mode), then fall back to first segment
            var initPath = Path.Combine(session.SessionDirectory, "init.mp4");
            var probeFile = System.IO.File.Exists(initPath) 
                ? initPath 
                : Directory.GetFiles(session.SessionDirectory, "seg_000.*").FirstOrDefault();
                
            if (probeFile == null)
            {
                return new { error = "No transcode output files found yet" };
            }
            
            var ffprobePath = _binaryLocationService.ResolveFFprobePath();
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_streams \"{probeFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return new { error = "Failed to start FFprobe" };
            
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            // Parse JSON and extract video and audio stream info
            var probeData = System.Text.Json.JsonDocument.Parse(output);
            var streams = probeData.RootElement.GetProperty("streams");
            
            // Find video and audio streams
            string? videoCodec = null, pixelFormat = null, colorSpace = null, colorTransfer = null, colorPrimaries = null, resolution = null;
            bool hasHdrMetadata = false, isHdr = false;
            string? audioCodec = null;
            int? audioChannels = null;
            
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var codecType))
                {
                    var type = codecType.GetString();
                    
                    if (type == "video" && videoCodec == null)
                    {
                        videoCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                        pixelFormat = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                        colorSpace = stream.TryGetProperty("color_space", out var cs) ? cs.GetString() : null;
                        colorTransfer = stream.TryGetProperty("color_transfer", out var ct) ? ct.GetString() : null;
                        colorPrimaries = stream.TryGetProperty("color_primaries", out var cp) ? cp.GetString() : null;
                        var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        var height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        resolution = $"{width}x{height}";
                        
                        hasHdrMetadata = stream.TryGetProperty("side_data_list", out var sideData) &&
                            sideData.EnumerateArray().Any(sd => 
                                sd.TryGetProperty("side_data_type", out var sdType) &&
                                (sdType.GetString()?.Contains("Mastering") == true || 
                                 sdType.GetString()?.Contains("Content light") == true));
                                 
                        isHdr = colorTransfer == "smpte2084" || colorSpace == "bt2020nc";
                    }
                    else if (type == "audio" && audioCodec == null)
                    {
                        audioCodec = stream.TryGetProperty("codec_name", out var acn) ? acn.GetString() : null;
                        audioChannels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : null;
                    }
                }
            }
            
            if (videoCodec == null)
            {
                return new { error = "No video stream found in probe data" };
            }
            
            return new
            {
                filePath = includeSensitiveData ? probeFile : Path.GetFileName(probeFile),
                videoCodec,
                pixelFormat,
                colorSpace,
                colorTransfer,
                colorPrimaries,
                resolution,
                hasHdrMetadata,
                isHdr,
                audioCodec,
                audioChannels
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe transcoded output");
            return new { error = ex.Message };
        }
    }
    


    /// <summary>
    /// Get a single frame preview at a specific timestamp.
    /// Used for scrubber thumbnail preview while dragging.
    /// </summary>
    private static readonly Dictionary<string, (byte[] Data, DateTime Expires)> _frameCache = new();
    private static readonly object _frameCacheLock = new();
    
    [HttpGet("{id}/frame")]
    public async Task<IActionResult> GetFramePreview(Guid id, [FromQuery] double time, [FromQuery] string? token = null)
    {
        try
        {
            // Validate token (same as other endpoints)
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
            
            // Round time to 1 second to increase cache hits
            var roundedTime = Math.Floor(time);
            var cacheKey = $"{id}_{roundedTime}";
            
            // Check cache first
            lock (_frameCacheLock)
            {
                if (_frameCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
                {
                    return File(cached.Data, "image/jpeg");
                }
            }

            // Get media item path
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mediaItem = await context.MediaItems.FindAsync(id);
            if (mediaItem == null) return NotFound();

            // Extract single frame using FFmpeg
            var ffmpegPath = "ffmpeg"; // Assumes ffmpeg is on PATH
            var tempFile = Path.Combine(Path.GetTempPath(), $"frame_{id}_{roundedTime}.jpg");
            
            try
            {
                // Use FFmpeg to extract frame at timestamp (fast seek with -ss before -i)
                var arguments = $"-ss {roundedTime:F0} -i \"{mediaItem.Path}\" -vframes 1 -q:v 8 -vf scale=320:-1 -f image2 -y \"{tempFile}\"";
                _logger.LogDebug("FFmpeg frame extraction: {Args}", arguments);
                
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process == null)
                {
                    _logger.LogError("Failed to start FFmpeg process");
                    return StatusCode(500, "Failed to start FFmpeg");
                }
                
                // Read stderr asynchronously
                var stderrTask = process.StandardError.ReadToEndAsync();
                
                // Wait with timeout of 5 seconds (increased for slower files)
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timeout - kill the process
                    try { process.Kill(true); } catch { }
                    _logger.LogWarning("Frame extraction timed out for {MediaId} at {Time}s", id, time);
                    return StatusCode(504, "Frame extraction timed out");
                }
                
                var stderr = await stderrTask;
                
                if (process.ExitCode != 0)
                {
                    _logger.LogError("FFmpeg frame extraction failed with exit code {ExitCode}: {Stderr}", process.ExitCode, stderr);
                    return StatusCode(500, $"FFmpeg failed: {stderr.Substring(0, Math.Min(500, stderr.Length))}");
                }

                if (!System.IO.File.Exists(tempFile))
                {
                    _logger.LogError("FFmpeg did not create output file. Stderr: {Stderr}", stderr);
                    return StatusCode(500, "Failed to extract frame - no output file");
                }

                var bytes = await System.IO.File.ReadAllBytesAsync(tempFile);
                
                if (bytes.Length == 0)
                {
                    _logger.LogError("FFmpeg created empty file. Stderr: {Stderr}", stderr);
                    return StatusCode(500, "Failed to extract frame - empty file");
                }
                
                // Cache for 30 seconds
                lock (_frameCacheLock)
                {
                    _frameCache[cacheKey] = (bytes, DateTime.UtcNow.AddSeconds(30));
                    
                    // Cleanup old cache entries
                    var expiredKeys = _frameCache.Where(kv => kv.Value.Expires < DateTime.UtcNow).Select(kv => kv.Key).ToList();
                    foreach (var key in expiredKeys) _frameCache.Remove(key);
                }
                
                // Cleanup temp file
                try { System.IO.File.Delete(tempFile); } catch { }
                
                _logger.LogDebug("Frame extracted successfully: {Bytes} bytes", bytes.Length);

                return File(bytes, "image/jpeg");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract frame at {Time}s for {MediaId}", time, id);
                return StatusCode(500, $"Frame extraction failed: {ex.Message}");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
