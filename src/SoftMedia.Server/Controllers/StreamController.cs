using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for serving media streams with HTTP Range Request support.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class StreamController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<StreamController> _logger;

    public StreamController(AppDbContext context, ILogger<StreamController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Streams the media file with HTTP Range Request support for seeking.
    /// Supports both GET (stream content) and HEAD (probe headers) for vidstack compatibility.
    /// </summary>
    [HttpGet("{id}")]
    [HttpHead("{id}")]
    public async Task<IActionResult> GetStream(Guid id)
    {
        // Fetch item with Library for path validation
        var item = await _context.MediaItems
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (item?.Library == null)
        {
            return NotFound();
        }

        if (!System.IO.File.Exists(item.Path))
        {
            _logger.LogWarning("File not found on disk: {Path}", item.Path);
            return NotFound("File not found on disk.");
        }

        // Security: LFI Protection - verify file path is within authorized library directories
        var canonicalPath = Path.GetFullPath(item.Path);
        var isAuthorized = item.Library.Paths.Any(p =>
            canonicalPath.StartsWith(Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase));

        if (!isAuthorized)
        {
            _logger.LogWarning("LFI attempt blocked: {Path}", item.Path);
            return Forbid();
        }

        var mimeType = MimeTypeResolver.GetMimeType(item.Path);
        
        // Serve the file with Range processing enabled (HTTP 206 Partial Content)
        return PhysicalFile(item.Path, mimeType, enableRangeProcessing: true);
    }
}
