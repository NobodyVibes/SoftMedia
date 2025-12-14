using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for HLS transcoding endpoints.
/// Requires authentication to prevent unauthorized access.
/// </summary>
[Authorize]
[ApiController]
[Route("api/transcode")]
public class TranscodeController : ControllerBase
{
    private readonly TranscodeService _transcodeService;
    private readonly AppDbContext _context;
    private readonly ILogger<TranscodeController> _logger;

    public TranscodeController(TranscodeService transcodeService, AppDbContext context, ILogger<TranscodeController> logger)
    {
        _transcodeService = transcodeService;
        _context = context;
        _logger = logger;
    }

    [HttpGet("{id}/master.m3u8")]
    public async Task<IActionResult> GetMasterPlaylist(Guid id)
    {
        try
        {
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

            // Start transcoding if not already running
            _logger.LogInformation("Starting transcode for media {Id}, path: {Path}", id, mediaItem.Path);
            await _transcodeService.StartTranscodeAsync(id, mediaItem.Path);

            var stream = _transcodeService.GetPlaylist(id);
            if (stream == null)
            {
                _logger.LogWarning("Playlist not ready for {Id} - transcoding may still be starting", id);
                return StatusCode(503, "Transcoding in progress, playlist not ready yet. Please retry in a few seconds.");
            }

            // Read and rewrite M3U8 to inject token into segment URLs
            var token = Request.Query["token"].ToString();
            if (!string.IsNullOrEmpty(token))
            {
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                
                _logger.LogDebug("M3U8 content length: {Length}, rewriting with token", content.Length);
                
                // Append token to all .ts (segment) files
                var rewrittenContent = content.Replace(".ts", $".ts?token={token}");
                
                // Return modified playlist as bytes
                var bytes = System.Text.Encoding.UTF8.GetBytes(rewrittenContent);
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

    [HttpGet("{id}/{segment}")]
    public IActionResult GetSegment(Guid id, string segment)
    {
        var stream = _transcodeService.GetSegment(id, segment);
        if (stream == null) return NotFound();

        return File(stream, "video/MP2T");
    }

    [HttpDelete("{id}")]
    public IActionResult StopTranscode(Guid id)
    {
        _transcodeService.StopTranscode(id);
        return Ok();
    }
}
