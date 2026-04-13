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

    [HttpGet("dump-books")]
    public async Task<IActionResult> DumpBooks()
    {
        var books = await _context.MediaItems.Where(m => m.Type == Models.MediaType.Book).ToListAsync();
        return Ok(books.Select(b => new { b.Id, b.Title, b.PosterUrl, b.CoverArtPath, b.Overview }));
    }

    [HttpGet("{id}/cover")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)] // Cache for 1 day
    public async Task<IActionResult> GetCoverArt(Guid id)
    {
        var item = await _context.MediaItems.FindAsync(id);
        if (item == null)
            return NotFound();

        // 1. Try cached cover art path (filesystem)
        if (!string.IsNullOrEmpty(item.CoverArtPath))
        {
            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", item.CoverArtPath.TrimStart('/'));
            if (System.IO.File.Exists(fullPath))
            {
                var mimeType = Path.GetExtension(fullPath).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                var stream = System.IO.File.OpenRead(fullPath);
                return File(stream, mimeType);
            }
        }

        // 2. Fallback: extract embedded cover art from audio file via TagLib
        if (!System.IO.File.Exists(item.Path))
            return NotFound();

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
