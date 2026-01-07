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
    private readonly AppDbContext _context;
    private readonly ILogger<TranscodeController> _logger;

    public TranscodeController(
        TranscodeService transcodeService, 
        IStreamPlanService streamPlanService,
        AppDbContext context, 
        ILogger<TranscodeController> logger)
    {
        _transcodeService = transcodeService;
        _streamPlanService = streamPlanService;
        _context = context;
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
    /// </summary>
    [HttpGet("{id}/master.m3u8")]
    public async Task<IActionResult> GetMasterPlaylist(Guid id, [FromQuery] int? sub = null, [FromQuery] double? seek = null, [FromQuery] string? resolution = null, [FromQuery] string? codec = null, [FromQuery] bool? hdr = null)
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
            _logger.LogInformation("Starting transcode for media {Id} (user={UserId}, sub={Sub}, seek={Seek}, resolution={Res}, codec={Codec}, hdr={HDR})", 
                id, userId, sub, seek, resolution, codec, hdr);
            await _transcodeService.StartTranscodeAsync(id, userId, mediaItem.Path, sub, seek, resolution, codec: codec, preserveHdr: hdr);

            var stream = _transcodeService.GetPlaylist(id, userId, sub);
            if (stream == null)
            {
                _logger.LogWarning("Playlist not ready for {Id} - transcoding may still be starting", id);
                return StatusCode(503, "Transcoding in progress, playlist not ready yet. Please retry in a few seconds.");
            }

            // Read and rewrite M3U8 to inject token AND subtitle track into segment URLs
            var token = Request.Query["token"].ToString();
            if (!string.IsNullOrEmpty(token))
            {
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                
                _logger.LogDebug("M3U8 content length: {Length}, rewriting with token", content.Length);
                
                // Build query string for segments (include token and subtitle track)
                var queryParts = new List<string> { $"token={token}" };
                if (sub.HasValue)
                {
                    queryParts.Add($"sub={sub.Value}");
                }
                var queryString = string.Join("&", queryParts);
                
                // Check if we have a WebVTT subtitle file to include in the playlist
                var session = _transcodeService.GetSession(id, userId, sub);
                var hasSubtitles = session?.SubtitleVttPath != null && System.IO.File.Exists(session.SubtitleVttPath);
                
                _logger.LogInformation("Playlist for {Id}: session={Session}, SubtitleVttPath={Path}, hasSubtitles={HasSubs}",
                    id, session != null ? "found" : "null", session?.SubtitleVttPath ?? "null", hasSubtitles);
                
                var rewrittenContent = new System.Text.StringBuilder();
                
                // If subtitles available, add HLS subtitle track reference
                if (hasSubtitles && content.Contains("#EXTM3U"))
                {
                    // Insert subtitle track definition after #EXTM3U
                    // Include both token and sub parameter for proper session lookup
                    var subtitleQueryParts = new List<string> { $"token={token}" };
                    if (sub.HasValue) subtitleQueryParts.Add($"sub={sub.Value}");
                    var subtitleUrl = $"/api/transcode/{id}/subtitles.vtt?{string.Join("&", subtitleQueryParts)}";
                    
                    rewrittenContent.AppendLine("#EXTM3U");
                    rewrittenContent.AppendLine($"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Subtitles\",DEFAULT=YES,AUTOSELECT=YES,URI=\"{subtitleUrl}\"");
                    
                    // Add the rest of the playlist content (skip #EXTM3U since we already added it)
                    var restOfContent = content.Replace("#EXTM3U", "").TrimStart();
                    rewrittenContent.Append(restOfContent);
                    
                    _logger.LogInformation("Added subtitle track reference to HLS manifest for {Id}", id);
                }
                else
                {
                    rewrittenContent.Append(content);
                }
                
                // Append query string to all segment files (both .ts and .m4s)
                // Also handle init.mp4 for fMP4 initialization segment
                var finalContent = rewrittenContent.ToString()
                    .Replace(".ts", $".ts?{queryString}")
                    .Replace(".m4s", $".m4s?{queryString}")
                    .Replace("init.mp4", $"init.mp4?{queryString}");
                
                // Return modified playlist as bytes
                var bytes = System.Text.Encoding.UTF8.GetBytes(finalContent);
                return File(bytes, "application/vnd.apple.mpegurl");
            }

            // Fallback for no token
            _logger.LogWarning("No token provided for transcode request {Id}", id);
            return File(stream, "application/vnd.apple.mpegurl");
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
