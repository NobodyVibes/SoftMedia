using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Controllers;

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

    [HttpGet("{id}")]
    public async Task<IActionResult> GetStream(Guid id)
    {
        var item = await _context.MediaItems.FindAsync(id);
        if (item == null)
        {
            return NotFound();
        }

        if (!System.IO.File.Exists(item.Path))
        {
            _logger.LogWarning("File not found on disk: {Path}", item.Path);
            return NotFound("File not found on disk.");
        }

        var mimeType = MimeTypeResolver.GetMimeType(item.Path);
        
        // Serve the file with Range processing enabled
        return PhysicalFile(item.Path, mimeType, enableRangeProcessing: true);
    }
}
