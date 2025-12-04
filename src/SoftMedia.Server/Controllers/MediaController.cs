using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _context;

    public MediaController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MediaItemDto>> GetMediaItem(Guid id)
    {
        var item = await _context.MediaItems
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (item == null)
        {
            return NotFound();
        }

        return MediaItemDto.FromMediaItem(item, "/api/v1/image/proxy");
    }

    [HttpGet("recent")]
    public async Task<ActionResult<IEnumerable<MediaItemDto>>> GetRecentMedia([FromQuery] int limit = 20, [FromQuery] string? type = null)
    {
        IQueryable<MediaItem> query = _context.MediaItems;

        if (!string.IsNullOrEmpty(type) && Enum.TryParse<LibraryType>(type, true, out var libraryType))
        {
            var libraryIds = await _context.Libraries
                .Where(l => l.Type == libraryType)
                .Select(l => l.Id)
                .ToListAsync();

            query = query.Where(m => libraryIds.Contains(m.LibraryId));
        }

        var items = await query
            .OrderByDescending(m => m.DateAdded)
            .Take(limit)
            .ToListAsync();

        return items.Select(i => MediaItemDto.FromMediaItem(i, "/api/v1/image/proxy")).ToList();
    }
}
