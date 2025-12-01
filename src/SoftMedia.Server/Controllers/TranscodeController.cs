using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Route("api/transcode")]
public class TranscodeController : ControllerBase
{
    private readonly TranscodeService _transcodeService;
    private readonly AppDbContext _context;

    public TranscodeController(TranscodeService transcodeService, AppDbContext context)
    {
        _transcodeService = transcodeService;
        _context = context;
    }

    [HttpGet("{id}/master.m3u8")]
    public async Task<IActionResult> GetMasterPlaylist(Guid id)
    {
        var mediaItem = await _context.MediaItems.FindAsync(id);
        if (mediaItem == null) return NotFound();

        // Start transcoding if not already running
        // Note: In a real app, we might want to check if the file actually NEEDS transcoding first
        // For now, we force transcode for testing this feature
        await _transcodeService.StartTranscodeAsync(id, mediaItem.Path);

        var stream = _transcodeService.GetPlaylist(id);
        if (stream == null)
        {
            // Might need more time or failed
            return StatusCode(500, "Transcoding failed to start or playlist not ready");
        }

        return File(stream, "application/vnd.apple.mpegurl");
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
