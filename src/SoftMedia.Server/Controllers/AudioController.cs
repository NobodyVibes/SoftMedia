using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using System.IO;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AudioController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<AudioController> _logger;

    public AudioController(AppDbContext context, ILogger<AudioController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet("{id}/cover")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)] // Cache for 1 day
    public async Task<IActionResult> GetCoverArt(Guid id)
    {
        // 1. Try DB first (Persistent Store)
        var dbImage = await _context.MediaImages
            .FirstOrDefaultAsync(img => img.MediaItemId == id && img.ImageType == "Poster");

        if (dbImage != null)
        {
            return File(dbImage.Data, dbImage.MimeType);
        }

        // 2. Fallback to File (Dynamic Read)
        var item = await _context.MediaItems.FindAsync(id);
        if (item == null || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        try
        {
            using var tfile = TagLib.File.Create(item.Path);
            if (tfile.Tag.Pictures.Length > 0)
            {
                var pic = tfile.Tag.Pictures[0];
                return File(pic.Data.Data, pic.MimeType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract cover art for {Id}", id);
        }

        return NotFound();
    }
}
